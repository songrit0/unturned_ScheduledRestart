"""
Standalone runner for the ScheduledRestart Discord cog.

Use this if you DON'T want to merge the cog into your existing economy bot. It just
boots a minimal bot, loads RestartCog, and syncs the slash commands.

    pip install -r requirements.txt
    copy .env.example .env   # then edit it
    python standalone_bot.py

To merge into the existing bot instead, see README.md (add_cog one-liner).
"""
import discord
from discord.ext import commands

import restart_config as cfg
from restart_cog import RestartCog


class RestartBot(commands.Bot):
    def __init__(self):
        intents = discord.Intents.default()
        super().__init__(command_prefix="!", intents=intents)

    async def setup_hook(self):
        await self.add_cog(RestartCog(self))
        if cfg.GUILD_ID:
            guild = discord.Object(id=cfg.GUILD_ID)
            self.tree.copy_global_to(guild=guild)
            await self.tree.sync(guild=guild)  # instant for one guild
        else:
            await self.tree.sync()              # global (can take ~1h to propagate)

    async def on_ready(self):
        print(f"[ScheduledRestart] logged in as {self.user} (id={self.user.id})")


def main():
    if not cfg.DISCORD_TOKEN:
        raise SystemExit("DISCORD_TOKEN is not set (see .env).")
    RestartBot().run(cfg.DISCORD_TOKEN)


if __name__ == "__main__":
    main()
