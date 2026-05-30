using System.Xml.Serialization;
using Rocket.API;

namespace ScheduledRestart
{
    /// <summary>A chat line: text + a named color (same shape SellVault uses).</summary>
    public sealed class Message
    {
        [XmlAttribute] public string Text;
        [XmlAttribute] public string Color;
        public Message() { }
        public Message(string text, string color) { Text = text; Color = color; }
    }

    /// <summary>MySQL connection shared with the Discord bot (sr_status / sr_commands / sr_events).</summary>
    public sealed class DatabaseSection
    {
        public string ConnectionString;
        public string TablePrefix;
    }

    public sealed class ScheduledRestartConfiguration : IRocketPluginConfiguration
    {
        public DatabaseSection Database;

        /// <summary>Master switch. When false the plugin loads but never restarts.</summary>
        public bool Enabled;

        /// <summary>
        /// Daily restart times, "HH:mm" in the timezone given by <see cref="TimezoneOffsetHours"/>.
        /// The plugin always targets the next future slot. Default: one restart at 06:00.
        /// </summary>
        [XmlArray("DailyRestartTimes")]
        [XmlArrayItem("Time")]
        public string[] DailyRestartTimes;

        /// <summary>Offset from UTC used to interpret <see cref="DailyRestartTimes"/>. Thailand = 7.</summary>
        public double TimezoneOffsetHours;

        /// <summary>
        /// Seconds-before-restart at which to broadcast a warning. Default 600 (10 min) and 60 (1 min).
        /// Warnings whose offset is already past when a target is set are skipped.
        /// </summary>
        [XmlArray("WarnBeforeSeconds")]
        [XmlArrayItem("Seconds")]
        public int[] WarnBeforeSeconds;

        /// <summary>Final per-second countdown length (broadcast "restarting in N..."). Default 10.</summary>
        public int CountdownSeconds;

        /// <summary>Block all non-admin commands this many seconds before restart. Default 600 (10 min).</summary>
        public int LockdownSeconds;

        /// <summary>Disable command lockdown entirely (still warns + restarts).</summary>
        public bool LockdownEnabled;

        /// <summary>How often (s) the live player count / next-restart row is written for the bot.</summary>
        public int StatusWriteIntervalSeconds;

        /// <summary>How often (s) the plugin polls sr_commands for a Discord emergency restart/cancel.</summary>
        public int CommandPollSeconds;

        /// <summary>RocketMod permission that bypasses the command lockdown (admins always bypass).</summary>
        public string BypassPermission;

        // ---- in-game broadcasts (Thai | English) ----
        public Message MsgWarn;        // {time} -> humanized, e.g. "10 นาที"
        public Message MsgCountdown;   // {seconds}
        public Message MsgSaving;
        public Message MsgRestartNow;
        public Message MsgLockdownBlocked;
        public Message MsgEmergencyScheduled; // {time}
        public Message MsgEmergencyCancelled;

        public void LoadDefaults()
        {
            Database = new DatabaseSection
            {
                ConnectionString = "SERVER=localhost;DATABASE=unturned;UID=root;PASSWORD=123456",
                TablePrefix = "sr_"
            };
            Enabled = true;
            DailyRestartTimes = new[] { "06:00" };
            TimezoneOffsetHours = 7.0;
            WarnBeforeSeconds = new[] { 600, 60 };
            CountdownSeconds = 10;
            LockdownSeconds = 600;
            LockdownEnabled = true;
            StatusWriteIntervalSeconds = 20;
            CommandPollSeconds = 3;
            BypassPermission = "scheduledrestart.bypass";

            MsgWarn = new Message(
                "⚠ เซิร์ฟเวอร์จะรีสตาร์ทในอีก {time} | Server restarting in {time}", "yellow");
            MsgCountdown = new Message(
                "🔄 รีสตาร์ทใน {seconds}... | Restarting in {seconds}...", "red");
            MsgSaving = new Message(
                "💾 กำลังบันทึกโลกก่อนรีสตาร์ท... | Saving the world before restart...", "yellow");
            MsgRestartNow = new Message(
                "🔄 เซิร์ฟเวอร์กำลังรีสตาร์ท! เดี๋ยวกลับมานะ | Server is restarting now! Back shortly.", "red");
            MsgLockdownBlocked = new Message(
                "⛔ เซิร์ฟเวอร์กำลังจะรีสตาร์ท ใช้คำสั่งไม่ได้ในช่วงนี้ | Commands are locked before restart", "red");
            MsgEmergencyScheduled = new Message(
                "⚠ แอดมินสั่งรีสตาร์ทฉุกเฉินในอีก {time} | Emergency restart in {time}", "yellow");
            MsgEmergencyCancelled = new Message(
                "✅ ยกเลิกการรีสตาร์ทแล้ว | Restart cancelled", "green");
        }
    }
}
