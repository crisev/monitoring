import discord
from discord.ext import commands

# Enable required intents
intents = discord.Intents.default()
intents.message_content = True
intents.guilds = True

bot = commands.Bot(command_prefix="!", intents=intents)


@bot.event
async def on_ready():
    print(f"Bot connected as: {bot.user.name} (ID: {bot.user.id})")
    print("Ready to process commands.")


@bot.command(name="purge")
@commands.has_permissions(manage_messages=True)
async def purge(ctx, limit: int = 100, user: discord.Member = None):
    """Purges messages in the current channel.

    Usage:
      !purge 50           -> Deletes last 50 messages in channel
      !purge 100 @User    -> Deletes up to 100 messages sent by @User
    """
    # Delete the command message itself so it doesn't clutter the channel
    try:
        await ctx.message.delete()
    except discord.HTTPException:
        pass

    if user:
        # Check function to filter messages by user
        def is_target_user(message):
            return message.author.id == user.id

        deleted = await ctx.channel.purge(limit=limit, check=is_target_user, oldest_first=True)
        confirm_msg = f"Deleted {len(deleted)} message(s) from {user.display_name}."
    else:
        deleted = await ctx.channel.purge(limit=limit, oldest_first=True)
        confirm_msg = f"Deleted {len(deleted)} message(s)."

    # Send a temporary status message that auto-deletes after 5 seconds
    await ctx.send(confirm_msg, delete_after=5)


@purge.error
async def purge_error(ctx, error):
    if isinstance(error, commands.MissingPermissions):
        await ctx.send(
            "You don't have permission to manage messages.", delete_after=5
        )
    elif isinstance(error, commands.BadArgument):
        await ctx.send(
            "Invalid argument. Example: `!purge 50` or `!purge 50 @User`",
            delete_after=5,
        )

@bot.event
async def on_message_delete(message):
    # This prints to your command prompt, not into the Discord chat
    
    # Get the author's name (fallback to 'Unknown' if not found)
    author = message.author.name if message.author else "Unknown"
    
    # Grab the first 50 characters of the message so the log doesn't get huge
    content = message.content[:50] if message.content else "[No Text/Attachment]"
    
    print(f"Deleted -> {author}: {content}")
    
    
import os

# Replace with your actual bot token or set DISCORD_BOT_TOKEN environment variable
TOKEN = os.getenv("DISCORD_BOT_TOKEN", "YOUR_DISCORD_BOT_TOKEN_HERE")

if __name__ == "__main__":
    if TOKEN == "YOUR_DISCORD_BOT_TOKEN_HERE":
        print("Please set your DISCORD_BOT_TOKEN environment variable or update clear_discord_channel.py with your token.")
    else:
        bot.run(TOKEN)