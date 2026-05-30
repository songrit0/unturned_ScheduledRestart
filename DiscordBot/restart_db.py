"""
MySQL data layer for the ScheduledRestart Discord integration.

Shares the same database as the ScheduledRestart Unturned plugin (sr_* tables).
Each call opens a short-lived connection. All functions are synchronous — call them
via asyncio.to_thread(...) from the bot so the event loop never blocks.

Tables (created by the plugin, but ensure_schema() here is safe to run too):
  sr_status   (id=1, online, players, max_players, next_restart[UTC], state, embed_msg_id)
  sr_commands (id, command, arg, requested_by, created_at)   -- bot -> server
  sr_events   (id, kind, message, posted, created_at)        -- server -> bot
"""
import pymysql

import restart_config as cfg

P = cfg.SR_PREFIX
STATUS = f"{P}status"
COMMANDS = f"{P}commands"
EVENTS = f"{P}events"


def _conn():
    return pymysql.connect(
        host=cfg.DB["host"], port=cfg.DB["port"], db=cfg.DB["db"],
        user=cfg.DB["user"], password=cfg.DB["password"],
        charset="utf8mb4", autocommit=True, cursorclass=pymysql.cursors.DictCursor,
    )


def ensure_schema():
    """Create the sr_* tables if the plugin hasn't yet. Mirrors the C# schema."""
    stmts = [
        f"""CREATE TABLE IF NOT EXISTS `{STATUS}` (
            `id` TINYINT PRIMARY KEY,
            `online` TINYINT NOT NULL DEFAULT 0,
            `players` INT NOT NULL DEFAULT 0,
            `max_players` INT NOT NULL DEFAULT 0,
            `next_restart` DATETIME NULL,
            `state` VARCHAR(16) NOT NULL DEFAULT 'idle',
            `embed_msg_id` BIGINT UNSIGNED NULL,
            `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;""",
        f"""CREATE TABLE IF NOT EXISTS `{COMMANDS}` (
            `id` INT AUTO_INCREMENT PRIMARY KEY,
            `command` VARCHAR(16) NOT NULL,
            `arg` INT NOT NULL DEFAULT 0,
            `requested_by` VARCHAR(64) NULL,
            `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;""",
        f"""CREATE TABLE IF NOT EXISTS `{EVENTS}` (
            `id` INT AUTO_INCREMENT PRIMARY KEY,
            `kind` VARCHAR(24) NOT NULL,
            `message` VARCHAR(512) NOT NULL,
            `posted` TINYINT NOT NULL DEFAULT 0,
            `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
            INDEX `idx_posted` (`posted`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;""",
    ]
    with _conn() as c, c.cursor() as cur:
        for s in stmts:
            cur.execute(s)


def get_status() -> dict | None:
    """Latest server status row, or None if the plugin has never written one."""
    with _conn() as c, c.cursor() as cur:
        cur.execute(
            f"SELECT online, players, max_players, next_restart, state, embed_msg_id, updated_at, "
            f"TIMESTAMPDIFF(SECOND, updated_at, NOW()) AS age_seconds "
            f"FROM `{STATUS}` WHERE id=1 LIMIT 1;"
        )
        return cur.fetchone()


def set_embed_msg_id(msg_id: int) -> None:
    """Remember which channel message holds the live status embed (bot-owned column)."""
    with _conn() as c, c.cursor() as cur:
        cur.execute(
            f"INSERT INTO `{STATUS}` (id, embed_msg_id) VALUES (1, %s) "
            f"ON DUPLICATE KEY UPDATE embed_msg_id=%s;",
            (msg_id, msg_id),
        )


def queue_command(command: str, arg: int, requested_by: str) -> None:
    """Queue an emergency 'restart' (arg = minutes, 0 = now) or 'cancel' for the server to pick up."""
    with _conn() as c, c.cursor() as cur:
        cur.execute(
            f"INSERT INTO `{COMMANDS}` (command, arg, requested_by) VALUES (%s, %s, %s);",
            (command, arg, requested_by[:64]),
        )


def fetch_unposted_events(limit: int = 20) -> list[dict]:
    """Announcements the plugin queued that the bot hasn't posted yet (oldest first)."""
    with _conn() as c, c.cursor() as cur:
        cur.execute(
            f"SELECT id, kind, message, created_at FROM `{EVENTS}` "
            f"WHERE posted=0 ORDER BY id ASC LIMIT %s;",
            (limit,),
        )
        return list(cur.fetchall())


def mark_events_posted(ids: list[int]) -> None:
    if not ids:
        return
    placeholders = ",".join(["%s"] * len(ids))
    with _conn() as c, c.cursor() as cur:
        cur.execute(f"UPDATE `{EVENTS}` SET posted=1 WHERE id IN ({placeholders});", tuple(ids))
