# Window Killer Gist Setup Guide

## How to Create and Use a GitHub Gist for Block Lists

### Step 1: Create a GitHub Gist
1. Go to https://gist.github.com
2. Create a new gist with the filename: `block_lists.json`
3. Copy the JSON content from [GIST_EXAMPLE.json](GIST_EXAMPLE.json) into the gist
4. Make it public (so it can be accessed without authentication)
5. Click "Create public gist"

### Step 2: Get the Raw URL
1. In the gist page, click the "Raw" button
2. Copy the URL from your browser's address bar
3. It should look like: `https://gist.githubusercontent.com/YOUR_USERNAME/GIST_ID/raw/HASH/block_lists.json`

### Step 3: Configure window_killer.ps1
1. Open `window_killer.ps1`
2. Find the line with `$BlockListGistUrl = ""`
3. Paste your raw gist URL between the quotes:
   ```powershell
   $BlockListGistUrl = "https://gist.githubusercontent.com/YOUR_USERNAME/GIST_ID/raw/HASH/block_lists.json"
   ```

### Step 4: Test
- Run `sys_monitor.ps1`
- You should see "Updated blocked process names from gist." and "Updated blocked page titles from gist." in the console

## Updating Block Lists
Simply edit your GitHub gist and save. The next time the monitoring script runs, it will fetch the updated lists automatically.

## JSON Format
The gist must contain valid JSON with two arrays:
- `blockedProcessNames`: Process executable names (without .exe)
- `blockedPageTitles`: Substrings to match in window titles

**Example:**
```json
{
  "blockedProcessNames": [
    "steam",
    "minecraft"
  ],
  "blockedPageTitles": [
    "YouTube",
    "TikTok"
  ]
}
```

## Default Block Lists
If the gist URL is not set or cannot be fetched, the script will use the default lists defined at the top of `window_killer.ps1`.
