using System;
using System.Collections.Generic;
using System.Text;
using MySql.Data.MySqlClient;
using Logger = Rocket.Core.Logging.Logger;

namespace ScheduledRestart
{
    /// <summary>One command the Discord bot queued for the server (restart / cancel).</summary>
    public sealed class PendingCommand
    {
        public long Id;
        public string Command;   // "restart" | "cancel"
        public int Arg;          // restart: minutes (0 = now)
    }

    /// <summary>
    /// MySQL coordination shared with the Discord bot. Mirrors SellVault's style: connections are
    /// opened per call and EVERY method here runs on a background thread — never touch the Unturned
    /// API from this class.
    ///   <p>sr_status   (id=1, online, players, max_players, next_restart[UTC], state, embed_msg_id)
    ///   <p>sr_commands (id, command, arg, requested_by, created_at)  -- bot -> server queue
    ///   <p>sr_events   (id, kind, message, posted, created_at)       -- server -> bot announcements
    /// </summary>
    public sealed class RestartDatabase
    {
        private readonly string _conn;
        private readonly string _status;
        private readonly string _commands;
        private readonly string _events;

        public RestartDatabase(string connectionString, string tablePrefix)
        {
            _conn = connectionString;
            string p = Sanitize(tablePrefix);
            _status = p + "status";
            _commands = p + "commands";
            _events = p + "events";
            EnsureSchema();
        }

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "sr_";
            StringBuilder sb = new StringBuilder();
            foreach (char c in raw) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            return sb.Length == 0 ? "sr_" : sb.ToString();
        }

        private void EnsureSchema()
        {
            try
            {
                using (MySqlConnection c = new MySqlConnection(_conn))
                {
                    c.Open();
                    Exec(c, "CREATE TABLE IF NOT EXISTS `" + _status + "` ("
                        + "`id` TINYINT PRIMARY KEY,"
                        + "`online` TINYINT NOT NULL DEFAULT 0,"
                        + "`players` INT NOT NULL DEFAULT 0,"
                        + "`max_players` INT NOT NULL DEFAULT 0,"
                        + "`next_restart` DATETIME NULL,"
                        + "`state` VARCHAR(16) NOT NULL DEFAULT 'idle',"
                        + "`embed_msg_id` BIGINT UNSIGNED NULL,"
                        + "`updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP"
                        + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
                    Exec(c, "CREATE TABLE IF NOT EXISTS `" + _commands + "` ("
                        + "`id` INT AUTO_INCREMENT PRIMARY KEY,"
                        + "`command` VARCHAR(16) NOT NULL,"
                        + "`arg` INT NOT NULL DEFAULT 0,"
                        + "`requested_by` VARCHAR(64) NULL,"
                        + "`created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP"
                        + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
                    Exec(c, "CREATE TABLE IF NOT EXISTS `" + _events + "` ("
                        + "`id` INT AUTO_INCREMENT PRIMARY KEY,"
                        + "`kind` VARCHAR(24) NOT NULL,"
                        + "`message` VARCHAR(512) NOT NULL,"
                        + "`posted` TINYINT NOT NULL DEFAULT 0,"
                        + "`created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,"
                        + "INDEX `idx_posted` (`posted`)"
                        + ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
                }
                Logger.Log("[ScheduledRestart] MySQL tables ready.");
            }
            catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] EnsureSchema failed"); }
        }

        private static void Exec(MySqlConnection c, string sql)
        {
            using (MySqlCommand cmd = new MySqlCommand(sql, c)) cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Upsert the single status row (id=1). Only plugin-owned columns are written; the bot's
        /// <c>embed_msg_id</c> is left untouched. <paramref name="nextRestartUtc"/> is stored as UTC.
        /// </summary>
        public void WriteStatus(bool online, int players, int maxPlayers, DateTime? nextRestartUtc, string state)
        {
            try
            {
                using (MySqlConnection c = new MySqlConnection(_conn))
                {
                    c.Open();
                    // NOTE: updated_at is set explicitly (NOT left to ON UPDATE CURRENT_TIMESTAMP).
                    // An upsert whose values all match the existing row is a MySQL no-op and would
                    // leave updated_at frozen, making the bot's freshness check flip the server to
                    // "offline" on an idle (0-player, unchanged) server. NOW() always differs from the
                    // stored value across writes, so this forces the timestamp to advance every time.
                    using (MySqlCommand cmd = new MySqlCommand(
                        "INSERT INTO `" + _status + "` (id, online, players, max_players, next_restart, state, updated_at) "
                        + "VALUES (1,@o,@p,@m,@n,@s, NOW()) "
                        + "ON DUPLICATE KEY UPDATE online=@o, players=@p, max_players=@m, next_restart=@n, state=@s, updated_at=NOW();", c))
                    {
                        cmd.Parameters.AddWithValue("@o", online ? 1 : 0);
                        cmd.Parameters.AddWithValue("@p", players);
                        cmd.Parameters.AddWithValue("@m", maxPlayers);
                        cmd.Parameters.AddWithValue("@n", (object)nextRestartUtc ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@s", state ?? "idle");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] WriteStatus failed"); }
        }

        /// <summary>Mark the server offline (best-effort, called as the restart begins).</summary>
        public void MarkOffline()
        {
            try
            {
                using (MySqlConnection c = new MySqlConnection(_conn))
                {
                    c.Open();
                    Exec(c, "UPDATE `" + _status + "` SET online=0, players=0, state='restarting' WHERE id=1;");
                }
            }
            catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] MarkOffline failed"); }
        }

        /// <summary>Read and consume all queued bot commands (oldest first). Rows are deleted once read.</summary>
        public List<PendingCommand> PollCommands()
        {
            List<PendingCommand> list = new List<PendingCommand>();
            try
            {
                using (MySqlConnection c = new MySqlConnection(_conn))
                {
                    c.Open();
                    using (MySqlCommand cmd = new MySqlCommand(
                        "SELECT `id`,`command`,`arg` FROM `" + _commands + "` ORDER BY `id` ASC;", c))
                    using (MySqlDataReader r = cmd.ExecuteReader())
                        while (r.Read())
                            list.Add(new PendingCommand
                            {
                                Id = Convert.ToInt64(r["id"]),
                                Command = Convert.ToString(r["command"]),
                                Arg = Convert.ToInt32(r["arg"])
                            });

                    if (list.Count > 0)
                    {
                        string[] ids = new string[list.Count];
                        for (int i = 0; i < list.Count; i++) ids[i] = list[i].Id.ToString();
                        Exec(c, "DELETE FROM `" + _commands + "` WHERE `id` IN (" + string.Join(",", ids) + ");");
                    }
                }
            }
            catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] PollCommands failed"); }
            return list;
        }

        /// <summary>Queue an announcement for the Discord bot to post (warnings, back-online, etc.).</summary>
        public void AddEvent(string kind, string message)
        {
            try
            {
                using (MySqlConnection c = new MySqlConnection(_conn))
                {
                    c.Open();
                    using (MySqlCommand cmd = new MySqlCommand(
                        "INSERT INTO `" + _events + "` (kind, message) VALUES (@k,@m);", c))
                    {
                        cmd.Parameters.AddWithValue("@k", kind ?? "info");
                        cmd.Parameters.AddWithValue("@m", message ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) { Logger.LogException(ex, "[ScheduledRestart] AddEvent failed"); }
        }
    }
}
