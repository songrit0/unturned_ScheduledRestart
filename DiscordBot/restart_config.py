"""Configuration for the ScheduledRestart Discord integration (loads .env)."""
import os
from dotenv import load_dotenv

load_dotenv()


def _int(name: str, default: int = 0) -> int:
    try:
        return int(os.getenv(name, default))
    except (TypeError, ValueError):
        return default


DISCORD_TOKEN = os.getenv("DISCORD_TOKEN", "")
GUILD_ID = _int("GUILD_ID")
ADMIN_ROLE_ID = _int("ADMIN_ROLE_ID")

# Channel that shows the live, auto-updating status embed.
STATUS_CHANNEL_ID = _int("STATUS_CHANNEL_ID")
# Channel that receives restart announcements (warnings, back-online, ...).
# Falls back to STATUS_CHANNEL_ID when unset.
ANNOUNCE_CHANNEL_ID = _int("ANNOUNCE_CHANNEL_ID") or STATUS_CHANNEL_ID

# Shared MySQL (same database the Unturned plugins use).
DB = dict(
    host=os.getenv("DB_HOST", "localhost"),
    port=_int("DB_PORT", 3306),
    db=os.getenv("DB_NAME", "unturned"),
    user=os.getenv("DB_USER", "root"),
    password=os.getenv("DB_PASSWORD", ""),
)

# Must match the plugin's Database.TablePrefix.
SR_PREFIX = os.getenv("SR_PREFIX", "sr_")

# Timezone offset used to display next_restart (stored as UTC). Thailand = 7.
TZ_OFFSET_HOURS = _int("TZ_OFFSET_HOURS", 7)

SERVER_NAME = os.getenv("SERVER_NAME", "Unturned Server")

# How often (seconds) to refresh the status embed and drain the announcement queue.
EMBED_REFRESH_SECONDS = _int("EMBED_REFRESH_SECONDS", 30)
EVENT_POLL_SECONDS = _int("EVENT_POLL_SECONDS", 10)

# A status row older than this many seconds means the server is down / not reporting.
STALE_AFTER_SECONDS = _int("STALE_AFTER_SECONDS", 90)

# Bot member-list status (the text under the bot's name). Refreshed every EMBED_REFRESH_SECONDS.
#   PRESENCE_TYPE: custom | watching | playing | listening  (custom = plain text, like Mimu)
#   {players}/{max} are filled with the live counts.
PRESENCE_TYPE = os.getenv("PRESENCE_TYPE", "custom")
PRESENCE_TEMPLATE = os.getenv("PRESENCE_TEMPLATE", "🟢 Players {players}/{max}")
PRESENCE_OFFLINE = os.getenv("PRESENCE_OFFLINE", "🔴 Server offline")
