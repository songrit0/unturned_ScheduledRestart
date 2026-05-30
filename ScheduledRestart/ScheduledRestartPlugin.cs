// =============================================================================
//  ScheduledRestart - daily timed restarts for Unturned 3.x + RocketMod 4.9.3.18,
//  with Discord coordination over a shared MySQL database.
//
//  What it does:
//    * Restarts the server at fixed daily times (default 06:00 Asia/Bangkok).
//    * Warns in-game before each restart (default -10 min and -1 min) plus a
//      per-second countdown (default last 10s).
//    * Saves the world (SaveManager.save) before calling Provider.shutdown().
//    * Locks ALL non-admin commands during the final window (default 10 min).
//    * Publishes live status (players n/max, next restart) and announcements to
//      the Discord bot, and accepts emergency /restart + /restart cancel from it,
//      all via the sr_* MySQL tables (see RestartDatabase).
//
//  Timezone: the next restart is computed as DateTime.UtcNow + TimezoneOffsetHours
//  so it does NOT depend on the (Linux) host's local clock. next_restart is stored
//  as UTC; the bot converts it back to +7 for display.
//
//  Restart loop: Provider.shutdown() only EXITS the process. The host (e.g. the
//  RestoreMonarchy panel with "auto restart / keep online" enabled) is what brings
//  the server back up.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Rocket.API;
using Rocket.API.Serialisation;
using Rocket.Core;
using Rocket.Core.Plugins;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using Action = System.Action;
using Logger = Rocket.Core.Logging.Logger;

namespace ScheduledRestart
{
    public sealed class ScheduledRestartPlugin : RocketPlugin<ScheduledRestartConfiguration>
    {
        public static ScheduledRestartPlugin Instance { get; private set; }
        public RestartDatabase Database { get; private set; }

        private const string HarmonyId = "com.imaximum.scheduledrestart";
        private Harmony _harmony;

        // ---- restart target / scheduling state (main thread only) ----
        private DateTime _targetUtc;          // when the next restart fires
        private bool _isEmergency;            // target was set by an admin, not the schedule
        private bool[] _warnSent;             // parallel to cfg.WarnBeforeSeconds
        private int _lastCountdownSecond;     // last value broadcast in the final countdown
        private bool _lockedDown;
        private bool _shuttingDown;

        // ---- background-DB throttling ----
        private float _nextStatusWriteAt;
        private float _nextCommandPollAt;
        private int _statusInFlight;
        private int _pollInFlight;

        // main-thread action queue (DB callbacks marshal back here)
        private readonly object _lock = new object();
        private readonly Queue<Action> _main = new Queue<Action>();

        // per-player anti-spam for the lockdown notice
        private readonly Dictionary<ulong, float> _lastBlockNotice = new Dictionary<ulong, float>();

        public bool IsLockedDown { get { return _lockedDown; } }

        // ---------------------------------------------------------------------
        //  Lifecycle
        // ---------------------------------------------------------------------
        protected override void Load()
        {
            Instance = this;
            ScheduledRestartConfiguration cfg = Configuration.Instance;

            DatabaseSection db = cfg.Database;
            Database = new RestartDatabase(db.ConnectionString, db.TablePrefix);

            if (!cfg.Enabled)
            {
                Logger.Log("[ScheduledRestart] Disabled via config (Enabled=false). No restarts will fire.");
                Instance = this; // still allow status writes? no - just return.
                return;
            }

            // Harmony patch for the command lockdown. If it fails we still warn + restart,
            // we just can't hard-block commands.
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(ScheduledRestartPlugin).Assembly);
                Logger.Log("[ScheduledRestart] Command-lockdown patch applied.");
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[ScheduledRestart] Harmony patch FAILED - lockdown will be advisory only");
            }

            SetTarget(NextScheduledUtc(), false, announce: false);

            float now = Time.realtimeSinceStartup;
            _nextStatusWriteAt = now;                                  // write immediately
            _nextCommandPollAt = now + Mathf.Max(1, cfg.CommandPollSeconds);

            // Announce "back online + next restart" to Discord.
            string when = DescribeNext();
            QueueEvent("online", "🟢 เซิร์ฟเวอร์ออนไลน์แล้ว — รีสตาร์ทรอบถัดไป " + when);

            Logger.Log("[ScheduledRestart] Loaded. Next restart (UTC) " + _targetUtc.ToString("yyyy-MM-dd HH:mm")
                       + " | " + when);
        }

        protected override void Unload()
        {
            if (_harmony != null)
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
            }
            lock (_lock) _main.Clear();
            _lastBlockNotice.Clear();
            Database = null;
            Instance = null;
            Logger.Log("[ScheduledRestart] Unloaded.");
        }

        // ---------------------------------------------------------------------
        //  Main tick. RocketPlugin is a MonoBehaviour; FixedUpdate is the hook
        //  SellVault uses (no clash with the base class). ~50/s is ample for a
        //  1-second countdown — repeats are de-duped by _lastCountdownSecond.
        // ---------------------------------------------------------------------
        private void FixedUpdate()
        {
            // drain queued main-thread actions (results of background DB work)
            while (true)
            {
                Action a = null;
                lock (_lock) { if (_main.Count > 0) a = _main.Dequeue(); }
                if (a == null) break;
                try { a(); } catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] main action"); }
            }

            if (Instance == null || _shuttingDown) return;
            ScheduledRestartConfiguration cfg = Configuration.Instance;
            if (cfg == null || !cfg.Enabled) return;

            float rt = Time.realtimeSinceStartup;
            TickCommandPoll(cfg, rt);
            TickSchedule(cfg);
            TickStatusWrite(cfg, rt);
        }

        // ---- emergency-command polling (background DB, applied on main thread) ----
        private void TickCommandPoll(ScheduledRestartConfiguration cfg, float rt)
        {
            if (rt < _nextCommandPollAt) return;
            _nextCommandPollAt = rt + Mathf.Max(1, cfg.CommandPollSeconds);
            if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RestartDatabase d = Database;
                    if (d == null) return;
                    List<PendingCommand> cmds = d.PollCommands();
                    if (cmds.Count == 0) return;
                    Enqueue(() =>
                    {
                        foreach (PendingCommand pc in cmds)
                        {
                            if (string.Equals(pc.Command, "cancel", StringComparison.OrdinalIgnoreCase))
                                ApplyCancel();
                            else if (string.Equals(pc.Command, "restart", StringComparison.OrdinalIgnoreCase))
                                ApplyEmergency(pc.Arg);
                        }
                    });
                }
                catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] command poll"); }
                finally { Interlocked.Exchange(ref _pollInFlight, 0); }
            });
        }

        // ---- the schedule state machine (pure time math; main thread) ----
        private void TickSchedule(ScheduledRestartConfiguration cfg)
        {
            double secsLeft = (_targetUtc - DateTime.UtcNow).TotalSeconds;

            // lockdown window
            _lockedDown = cfg.LockdownEnabled && secsLeft <= cfg.LockdownSeconds && secsLeft > 0;

            // warnings
            if (cfg.WarnBeforeSeconds != null && _warnSent != null)
            {
                for (int i = 0; i < cfg.WarnBeforeSeconds.Length && i < _warnSent.Length; i++)
                {
                    if (_warnSent[i]) continue;
                    if (secsLeft <= cfg.WarnBeforeSeconds[i])
                    {
                        _warnSent[i] = true;
                        string human = Humanize(cfg.WarnBeforeSeconds[i]);
                        Message m = _isEmergency ? cfg.MsgEmergencyScheduled : cfg.MsgWarn;
                        Broadcast(m, "{time}", human, Color.yellow);
                        QueueEvent("warn", "⚠ รีสตาร์ทในอีก " + human);
                    }
                }
            }

            // final countdown
            if (secsLeft <= cfg.CountdownSeconds && secsLeft > 0)
            {
                int cur = Mathf.CeilToInt((float)secsLeft);
                if (cur >= 1 && cur <= cfg.CountdownSeconds && cur != _lastCountdownSecond)
                {
                    _lastCountdownSecond = cur;
                    Broadcast(cfg.MsgCountdown, "{seconds}", cur.ToString(), Color.red);
                }
            }

            // fire
            if (secsLeft <= 0) BeginShutdown(cfg);
        }

        // ---- periodic status write for the bot's embed ----
        private void TickStatusWrite(ScheduledRestartConfiguration cfg, float rt)
        {
            if (rt < _nextStatusWriteAt) return;
            _nextStatusWriteAt = rt + Mathf.Max(5, cfg.StatusWriteIntervalSeconds);
            if (Interlocked.CompareExchange(ref _statusInFlight, 1, 0) != 0) return;

            int players = Provider.clients != null ? Provider.clients.Count : 0;
            int max = Provider.maxPlayers;
            DateTime target = _targetUtc;
            string state = _lockedDown ? "lockdown" : (_isEmergency ? "emergency" : "scheduled");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RestartDatabase d = Database;
                    if (d != null) d.WriteStatus(true, players, max, target, state);
                }
                catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] status write"); }
                finally { Interlocked.Exchange(ref _statusInFlight, 0); }
            });
        }

        // ---------------------------------------------------------------------
        //  Shutdown sequence (main thread)
        // ---------------------------------------------------------------------
        private void BeginShutdown(ScheduledRestartConfiguration cfg)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            _lockedDown = true; // keep commands locked through the save

            try
            {
                BroadcastRaw(cfg.MsgSaving, Color.yellow);
                QueueEvent("restart", "🔄 กำลังบันทึกโลกและรีสตาร์ทเซิร์ฟเวอร์...");

                try { SaveManager.save(); }
                catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] SaveManager.save failed"); }

                BroadcastRaw(cfg.MsgRestartNow, Color.red);

                // best-effort: mark offline synchronously so the bot's embed flips fast
                try { Database?.MarkOffline(); } catch { /* ignore */ }
            }
            catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] BeginShutdown"); }

            Logger.Log("[ScheduledRestart] Saving complete - shutting down for restart.");
            try { Provider.shutdown(0); }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[ScheduledRestart] Provider.shutdown(0) failed - trying parameterless");
                try { Provider.shutdown(); } catch (Exception ex2) { Logger.LogException(ex2, "[ScheduledRestart] shutdown"); }
            }
        }

        // ---------------------------------------------------------------------
        //  Emergency control (called from Discord poll AND the in-game command)
        // ---------------------------------------------------------------------
        /// <summary>Schedule an emergency restart in <paramref name="minutes"/> (<=0 = as soon as possible).</summary>
        public void ApplyEmergency(int minutes)
        {
            if (_shuttingDown) return;
            int secs = minutes <= 0 ? 0 : minutes * 60;
            SetTarget(DateTime.UtcNow.AddSeconds(secs), true, announce: true);
            Logger.Log("[ScheduledRestart] Emergency restart scheduled in " + secs + "s.");
        }

        /// <summary>Cancel an emergency restart and revert to the normal daily schedule.</summary>
        public void ApplyCancel()
        {
            if (_shuttingDown || !_isEmergency) return;
            SetTarget(NextScheduledUtc(), false, announce: false);
            ScheduledRestartConfiguration cfg = Configuration.Instance;
            BroadcastRaw(cfg.MsgEmergencyCancelled, Color.green);
            QueueEvent("cancel", "✅ ยกเลิกการรีสตาร์ทฉุกเฉินแล้ว — รอบถัดไป " + DescribeNext());
            Logger.Log("[ScheduledRestart] Emergency restart cancelled.");
        }

        /// <summary>(Re)point the restart target and reset all per-target flags.</summary>
        private void SetTarget(DateTime utc, bool emergency, bool announce)
        {
            _targetUtc = utc;
            _isEmergency = emergency;
            _lastCountdownSecond = 0;
            _lockedDown = false;

            double secsLeft = (_targetUtc - DateTime.UtcNow).TotalSeconds;
            ScheduledRestartConfiguration cfg = Configuration.Instance;
            int n = cfg.WarnBeforeSeconds != null ? cfg.WarnBeforeSeconds.Length : 0;
            _warnSent = new bool[n];
            for (int i = 0; i < n; i++)
                _warnSent[i] = cfg.WarnBeforeSeconds[i] >= secsLeft; // skip warnings already in the past

            if (announce && emergency)
            {
                string human = Humanize((int)Math.Round(secsLeft));
                Broadcast(cfg.MsgEmergencyScheduled, "{time}", human, Color.yellow);
                QueueEvent("emergency", "⚠ แอดมินสั่งรีสตาร์ทฉุกเฉินในอีก " + human);
            }
        }

        // ---------------------------------------------------------------------
        //  Command lockdown hook helpers
        // ---------------------------------------------------------------------
        /// <summary>True if this player may run commands despite the lockdown (admins + bypass perm).</summary>
        public bool CanBypassLockdown(IRocketPlayer player)
        {
            if (player == null) return true;          // console / RCON
            if (player.IsAdmin) return true;
            string perm = Configuration?.Instance?.BypassPermission;
            if (!string.IsNullOrEmpty(perm) && R.Permissions != null)
            {
                try { foreach (Permission p in R.Permissions.GetPermissions(player)) if (p.Name == perm) return true; }
                catch { /* ignore */ }
            }
            return false;
        }

        /// <summary>Tell a blocked player why their command didn't run (rate-limited per player).</summary>
        public void NotifyBlocked(IRocketPlayer player)
        {
            UnturnedPlayer up = player as UnturnedPlayer;
            if (up?.Player == null) return;
            ulong sid = up.CSteamID.m_SteamID;
            float now = Time.realtimeSinceStartup;
            if (_lastBlockNotice.TryGetValue(sid, out float last) && now - last < 3f) return;
            _lastBlockNotice[sid] = now;

            Message m = Configuration.Instance.MsgLockdownBlocked;
            if (m == null || string.IsNullOrEmpty(m.Text)) return;
            Color color = UnturnedChat.GetColorFromName(m.Color, Color.red);
            ChatManager.serverSendMessage(m.Text, color, null, up.Player.channel.owner, EChatMode.SAY, null, true);
        }

        // ---------------------------------------------------------------------
        //  Time helpers
        // ---------------------------------------------------------------------
        /// <summary>Next future daily restart, as a UTC DateTime.</summary>
        private DateTime NextScheduledUtc()
        {
            ScheduledRestartConfiguration cfg = Configuration.Instance;
            double off = cfg.TimezoneOffsetHours;
            DateTime nowLocal = DateTime.UtcNow.AddHours(off);

            DateTime best = DateTime.MaxValue;
            if (cfg.DailyRestartTimes != null)
            {
                foreach (string hhmm in cfg.DailyRestartTimes)
                {
                    if (!TryParseHHmm(hhmm, out int h, out int m)) continue;
                    DateTime today = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, h, m, 0);
                    DateTime cand = today > nowLocal ? today : today.AddDays(1);
                    if (cand < best) best = cand;
                }
            }
            if (best == DateTime.MaxValue) best = nowLocal.AddHours(6); // no valid times -> safety net

            return best.AddHours(-off); // local wall time -> UTC ticks
        }

        private static bool TryParseHHmm(string s, out int h, out int m)
        {
            h = 0; m = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Trim().Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m)) return false;
            return h >= 0 && h < 24 && m >= 0 && m < 60;
        }

        /// <summary>Human description of the next restart, e.g. "06:00 (อีก 5 ชม. 23 นาที)".</summary>
        private string DescribeNext()
        {
            double off = Configuration.Instance.TimezoneOffsetHours;
            DateTime local = _targetUtc.AddHours(off);
            return local.ToString("HH:mm") + " (อีก " + Humanize((int)Math.Round((_targetUtc - DateTime.UtcNow).TotalSeconds)) + ")";
        }

        /// <summary>"10 นาที", "1 ชม. 5 นาที", "30 วินาที" — for chat + Discord.</summary>
        private static string Humanize(int seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return seconds + " วินาที";
            int mins = seconds / 60;
            if (mins < 60) return mins + " นาที";
            int h = mins / 60, m = mins % 60;
            return m == 0 ? h + " ชม." : h + " ชม. " + m + " นาที";
        }

        // ---------------------------------------------------------------------
        //  Chat + queue plumbing
        // ---------------------------------------------------------------------
        private static void Broadcast(Message msg, string token, string value, Color fallback)
        {
            if (msg == null || string.IsNullOrEmpty(msg.Text)) return;
            string text = msg.Text.Replace(token, value);
            UnturnedChat.Say(text, UnturnedChat.GetColorFromName(msg.Color, fallback));
        }

        private static void BroadcastRaw(Message msg, Color fallback)
        {
            if (msg == null || string.IsNullOrEmpty(msg.Text)) return;
            UnturnedChat.Say(msg.Text, UnturnedChat.GetColorFromName(msg.Color, fallback));
        }

        public void Enqueue(Action action)
        {
            if (action == null) return;
            lock (_lock) _main.Enqueue(action);
        }

        /// <summary>Queue a Discord announcement on a background thread (fire and forget).</summary>
        private void QueueEvent(string kind, string message)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Database?.AddEvent(kind, message); }
                catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] QueueEvent"); }
            });
        }
    }

    // -------------------------------------------------------------------------
    //  Command lockdown: a Harmony prefix on RocketCommandManager.Execute that
    //  drops non-admin command execution while a restart is imminent. Bound by
    //  name via AccessTools so we don't hard-reference the (single) overload.
    // -------------------------------------------------------------------------
    [HarmonyPatch]
    internal static class CommandLockPatch
    {
        private static MethodBase TargetMethod()
        {
            Type t = AccessTools.TypeByName("Rocket.Core.Commands.RocketCommandManager");
            return t == null ? null : AccessTools.Method(t, "Execute", new[] { typeof(IRocketPlayer), typeof(string) });
        }

        private static bool Prepare(MethodBase original)
        {
            // If the method couldn't be resolved, skip this patch (lockdown becomes advisory).
            return TargetMethod() != null;
        }

        [HarmonyPrefix]
        private static bool Prefix(IRocketPlayer player, ref bool __result)
        {
            ScheduledRestartPlugin p = ScheduledRestartPlugin.Instance;
            if (p == null || !p.IsLockedDown) return true;     // run command normally
            if (p.CanBypassLockdown(player)) return true;      // admins / bypass perm

            p.NotifyBlocked(player);
            __result = false;                                   // Execute returns bool in this version
            return false;                                       // skip the original
        }
    }
}
