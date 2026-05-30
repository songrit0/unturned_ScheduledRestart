"""
ScheduledRestart Discord cog.

Drop this into your existing Unturned economy bot (recommended) or run it standalone
with standalone_bot.py. It talks to the same MySQL database as the ScheduledRestart
plugin via the sr_* tables.

Provides:
  * a live status embed in STATUS_CHANNEL_ID, auto-updated every EMBED_REFRESH_SECONDS
    (Players n/max + next restart countdown)
  * announcements (warnings, back-online, ...) posted to ANNOUNCE_CHANNEL_ID
  * /status                 -> show the status embed on demand
  * /restart in <minutes>   -> emergency restart in N minutes (with in-game countdown)
  * /restart now            -> save + restart immediately
  * /restart cancel         -> cancel an emergency restart
All /restart subcommands require the admin role (ADMIN_ROLE_ID) or guild admin perms.

Usage in an existing bot:
    from restart_cog import RestartCog
    await bot.add_cog(RestartCog(bot))
"""
import asyncio
from datetime import datetime, timedelta

import discord
from discord import app_commands
from discord.ext import commands, tasks

import restart_config as cfg
import restart_db as db


def _humanize(seconds: int) -> str:
    """'10 min', '1h 5m', '30s' — matches the plugin's style closely enough for Discord."""
    if seconds < 0:
        seconds = 0
    if seconds < 60:
        return f"{seconds}s"
    mins = seconds // 60
    if mins < 60:
        return f"{mins} min"
    h, m = divmod(mins, 60)
    return f"{h}h" if m == 0 else f"{h}h {m}m"


def _is_admin(interaction: discord.Interaction) -> bool:
    user = interaction.user
    if isinstance(user, discord.Member):
        if user.guild_permissions.administrator:
            return True
        if cfg.ADMIN_ROLE_ID and any(r.id == cfg.ADMIN_ROLE_ID for r in user.roles):
            return True
    return False


class RestartCog(commands.Cog):
    def __init__(self, bot: commands.Bot):
        self.bot = bot
        self._status_msg: discord.Message | None = None

    async def cog_load(self):
        try:
            await asyncio.to_thread(db.ensure_schema)
        except Exception as e:  # noqa: BLE001
            print(f"[ScheduledRestart] ensure_schema failed: {e}")
        self.refresh_embed.change_interval(seconds=max(10, cfg.EMBED_REFRESH_SECONDS))
        self.drain_events.change_interval(seconds=max(5, cfg.EVENT_POLL_SECONDS))
        self.refresh_embed.start()
        self.drain_events.start()

    async def cog_unload(self):
        self.refresh_embed.cancel()
        self.drain_events.cancel()

    # ---------------------------------------------------------------- embed --
    def _build_embed(self, status: dict | None) -> discord.Embed:
        now_utc = datetime.utcnow()
        online = False
        players = max_players = 0
        next_line = "—"

        if status:
            age = status.get("age_seconds")
            fresh = age is not None and age <= cfg.STALE_AFTER_SECONDS
            online = bool(status.get("online")) and fresh
            players = int(status.get("players") or 0)
            max_players = int(status.get("max_players") or 0)
            nr = status.get("next_restart")
            if nr is not None:
                remaining = int((nr - now_utc).total_seconds())
                local = nr + timedelta(hours=cfg.TZ_OFFSET_HOURS)
                next_line = f"{local.strftime('%H:%M')} (in {_humanize(remaining)})"

        color = discord.Color.green() if online else discord.Color.red()
        title = f"🟢 {cfg.SERVER_NAME}" if online else f"🔴 {cfg.SERVER_NAME}"
        embed = discord.Embed(title=title, color=color)
        embed.add_field(name="Players", value=f"{players}/{max_players}", inline=True)
        embed.add_field(name="Status", value="Online" if online else "Offline / restarting", inline=True)
        embed.add_field(name="Next restart", value=next_line, inline=False)
        embed.set_footer(text="updated")
        embed.timestamp = discord.utils.utcnow()
        return embed

    async def _find_or_create_status_message(self, channel: discord.abc.Messageable, status: dict | None):
        """Reuse the embed message stored in sr_status.embed_msg_id; create + persist one otherwise."""
        if self._status_msg is not None:
            return self._status_msg

        msg_id = status.get("embed_msg_id") if status else None
        if msg_id:
            try:
                self._status_msg = await channel.fetch_message(int(msg_id))
                return self._status_msg
            except (discord.NotFound, discord.HTTPException):
                self._status_msg = None

        self._status_msg = await channel.send(embed=self._build_embed(status))
        try:
            await asyncio.to_thread(db.set_embed_msg_id, self._status_msg.id)
        except Exception as e:  # noqa: BLE001
            print(f"[ScheduledRestart] set_embed_msg_id failed: {e}")
        return self._status_msg

    @tasks.loop(seconds=30)
    async def refresh_embed(self):
        if not cfg.STATUS_CHANNEL_ID:
            return
        channel = self.bot.get_channel(cfg.STATUS_CHANNEL_ID)
        if channel is None:
            return
        try:
            status = await asyncio.to_thread(db.get_status)
            msg = await self._find_or_create_status_message(channel, status)
            await msg.edit(embed=self._build_embed(status))
        except discord.NotFound:
            self._status_msg = None  # message deleted; recreate next tick
        except Exception as e:  # noqa: BLE001
            print(f"[ScheduledRestart] refresh_embed failed: {e}")

    @refresh_embed.before_loop
    async def _before_refresh(self):
        await self.bot.wait_until_ready()

    # ----------------------------------------------------------- announcements
    @tasks.loop(seconds=10)
    async def drain_events(self):
        channel_id = cfg.ANNOUNCE_CHANNEL_ID
        if not channel_id:
            return
        channel = self.bot.get_channel(channel_id)
        if channel is None:
            return
        try:
            events = await asyncio.to_thread(db.fetch_unposted_events)
            posted = []
            for ev in events:
                try:
                    await channel.send(ev["message"])
                    posted.append(ev["id"])
                except discord.HTTPException as e:
                    print(f"[ScheduledRestart] post event {ev['id']} failed: {e}")
            if posted:
                await asyncio.to_thread(db.mark_events_posted, posted)
        except Exception as e:  # noqa: BLE001
            print(f"[ScheduledRestart] drain_events failed: {e}")

    @drain_events.before_loop
    async def _before_drain(self):
        await self.bot.wait_until_ready()

    # ------------------------------------------------------------- commands --
    @app_commands.command(name="status", description="Show the Unturned server status")
    async def status_cmd(self, interaction: discord.Interaction):
        status = await asyncio.to_thread(db.get_status)
        await interaction.response.send_message(embed=self._build_embed(status), ephemeral=True)

    restart = app_commands.Group(name="restart", description="Emergency server restart controls (admin)")

    @restart.command(name="in", description="Restart the server in N minutes")
    @app_commands.describe(minutes="Minutes until restart (1-180)")
    async def restart_in(self, interaction: discord.Interaction, minutes: app_commands.Range[int, 1, 180]):
        if not _is_admin(interaction):
            await interaction.response.send_message("❌ ต้องเป็นแอดมินเท่านั้น", ephemeral=True)
            return
        await asyncio.to_thread(db.queue_command, "restart", int(minutes), str(interaction.user))
        await interaction.response.send_message(
            f"⚠ สั่งรีสตาร์ทในอีก {minutes} นาทีแล้ว (จะมี countdown ในเกม)", ephemeral=False
        )

    @restart.command(name="now", description="Save and restart the server immediately")
    async def restart_now(self, interaction: discord.Interaction):
        if not _is_admin(interaction):
            await interaction.response.send_message("❌ ต้องเป็นแอดมินเท่านั้น", ephemeral=True)
            return
        await asyncio.to_thread(db.queue_command, "restart", 0, str(interaction.user))
        await interaction.response.send_message("🔄 สั่งรีสตาร์ททันที (บันทึกโลกก่อน)", ephemeral=False)

    @restart.command(name="cancel", description="Cancel a pending emergency restart")
    async def restart_cancel(self, interaction: discord.Interaction):
        if not _is_admin(interaction):
            await interaction.response.send_message("❌ ต้องเป็นแอดมินเท่านั้น", ephemeral=True)
            return
        await asyncio.to_thread(db.queue_command, "cancel", 0, str(interaction.user))
        await interaction.response.send_message("✅ ส่งคำสั่งยกเลิกการรีสตาร์ทแล้ว", ephemeral=False)
