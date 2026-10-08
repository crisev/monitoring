# Monitor v2 — Windows client

The PC side of v2 (Phase 2 of [`docs/REDESIGN_PLAN.md`](../../docs/REDESIGN_PLAN.md)). The server decides how much time exists; this client asks for it, counts it down on a monotonic clock, and enforces the result. The original app at the repository root is not used or changed.

| Project | Runs as | Does |
|---|---|---|
| `Monitor.Service` | Windows service, **LocalSystem** | Server calls (device token), game lease and screen-time countdowns, offline budget, School/Gaming switch, closes programs that aren't allowed, Edge policies (HKLM), warnings, shutdown, starts the agent in each child session |
| `Monitor.Agent` | the child, in their session | Tray icon and status window (GAME ON/OFF), notifications, foreground app/title/Edge site and audio sampling every 5 s, screenshots |
| `Monitor.Core` | (library) | API client, `ModeClock` (time keeping), `ProcessRules` (whitelist), `EdgePolicies`, `ActivityBuffer`, pipe protocol |
| `Monitor.Core.Tests` | (xUnit) | Lease, screen time, offline budget, warnings, whitelist, Edge patterns, activity batches |

The agent and the service talk over the named pipe `\\.\pipe\MonitorV2`. Users can connect to it but not create it, and the service only accepts the agent executable from its own install folder. The agent only displays what the service sends and passes on the GAME ON/OFF button.

## Build

Needs the .NET 10 SDK on Windows.

```powershell
cd v2\client
dotnet test                 # unit tests
.\publish.ps1               # -> dist\MonitorV2\ (app\ + install.ps1 + uninstall.ps1), self-contained, ~155 MB
```

`dist\MonitorV2` is everything the PC needs; it doesn't need .NET installed.

## Install on a PC

1. In the web app: **PCs → Add PC** and copy the code (valid 30 minutes).
2. Copy `dist\MonitorV2` to the PC. In an **administrator** PowerShell there:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -ServerUrl https://monitor-device.<your-subdomain>.workers.dev -Code <CODE>
   ```
3. Log on as the child (a **standard** account). The tray icon appears within a few seconds.

What the script does:
- copies the program to `C:\Program Files\MonitorV2` (users can run it, not change it)
- enrolls the PC; the device token goes to `C:\ProgramData\MonitorV2`, readable only by SYSTEM and Administrators
- registers the `MonitorV2` service: automatic start, restarted after 5 s if it stops

**Update:** run `.\install.ps1` again from a newer `dist\MonitorV2`, without `-Code`. The enrollment is kept. (Automatic updates are Phase 3.)

**Uninstall:** `.\uninstall.ps1` stops and removes the service, removes the Edge policies, and deletes the program and `C:\ProgramData\MonitorV2`. Add `-KeepData` to keep the enrollment.

**Logs:** `C:\ProgramData\MonitorV2\logs\service-*.log` (service) and `%LOCALAPPDATA%\MonitorV2\agent-*.log` (agent, per user). `"C:\Program Files\MonitorV2\Monitor.Service.exe" status` shows the enrollment and saved counters.

### Test mode

`install.ps1 -DryRun` installs the service with `--dry-run`. It does everything (server calls, Gaming/School, Edge policies, screen time, activity, screenshots) but **only logs** the programs it would close and the shutdown. Use it first, look at the log for "Would close …", and add anything legitimate to **Settings → Allowed apps**. Then reinstall without `-DryRun`.

Test on a separate **standard** account. Administrator accounts are not monitored: no programs closed, no time counted, and Edge policies are removed while an administrator is at the keyboard. As the plan says, the original app must not run on that account.

## How it behaves

- **Clock:** only `Environment.TickCount64` (milliseconds since boot) is used. Changing the date, time or time zone has no effect. A gap of more than 5 s between ticks (sleep, hibernation) counts as 5 s at most.
- **Gaming:** GAME ON calls `/game/start`. The service plays until `request time + leaseRemainingSeconds` and renews with every heartbeat (every 30 s). If a renewal doesn't arrive (offline, asleep, server says no), it goes back to School mode when the lease runs out. Only a `/game/start` reply can turn Gaming on; a heartbeat can only keep or end it. Nothing is blocked in Gaming mode; Edge's site lists are removed.
- **School mode:** Edge allows only the parent's sites (plus Edge's own pages), InPrivate and extension installs stay off, and Edge opens a clean New Tab page. Every 5 s the service closes programs in the child's session that are not:
  - Windows shell programs or console tools
  - on the **Allowed apps** list
  - compilers and toolchains
  - **console programs** started by an IDE (Code::Blocks, VS Code, Visual Studio, up to 4 levels down): his own compiled exercises. Windowed programs (browsers, games) are closed even when an IDE starts them, and script runtimes (`java`, `python`, `node`, `dotnet`, …) only run if they are on the Allowed apps list. Each process is judged by its own `.exe`, so a program of his that launches Chrome does not make Chrome allowed.
  - in a trusted folder: `C:\Windows` (only files owned by the system), driver/vendor folders, Edge's helper folders, Windows shell packages, the install folder

  A window whose title contains a **blocked title** is closed. Closing Edge's window closes the whole browser, as in the old app. When Gaming ends, Edge is closed so videos and preloaded pages stop.
- **Screen time:** counted while a child's session is active and unlocked (Windows session state, not anything the agent says). Reported in each heartbeat; warnings at 10, 5 and 1 minutes; at 0 the PC shuts down.
- **Offline:** School mode keeps working with the last config. Screen time keeps counting down from the last server value. Unlocked offline time is limited by the **offline budget** (default 60 min). That budget resets only on a successful server call, so a reboot doesn't reset it; when it runs out, the PC shuts down. After a restart without network, saved limits apply only after 90 s, to give the network a chance.
- **Agent closed:** the service starts it again within about 10 s and reports `agent_missing` (posted to Discord). Gaming ends if the agent is missing for 20 s.
- **Not seen:** the service also deletes Edge policies left in the child's own registry hive by the original app (it gave Users full control of that key, and those leftovers would block sites in Gaming mode).

## Acceptance tests (plan §9, Phase 2)

On the test account, with test mode off. Each case is passed when game time used never exceeds the server balance, the Edge allowlist is still in place in School mode, and History/Discord show what happened.

| # | Do | Expect |
|---|---|---|
| 1 | Change date, time or time zone (as admin) during Gaming and School | Countdowns unaffected; heartbeats continue |
| 2 | `reg add HKCU\...` anything, e.g. `HKCU\Software\Policies\Microsoft\Edge\URLAllowlist` | No effect: the service reads nothing from HKCU, and Edge's HKLM policies win |
| 3 | Unplug the network mid-game | School mode at most 90 s after the last successful heartbeat; tray shows "offline (… left)"; GAME ON refused |
| 4 | Sleep or hibernate mid-game, wake up | School mode right away (or after the next heartbeat); no extra game time on the server |
| 5 | Reboot repeatedly (online and offline) | Starts in School mode; offline budget keeps counting across reboots |
| 6 | Kill `Monitor.Agent` in Task Manager | Back within ~10 s; `agent_missing` event; Gaming ends |
| 7 | cmd/PowerShell: `sc stop MonitorV2`, `taskkill /f /im Monitor.Service.exe`, delete files in `Program Files\MonitorV2` or `ProgramData\MonitorV2` | Access denied |
| 8 | Run a game copied to `C:\Windows\Temp` or `C:\WindowsGames` | Closed |

## Known gaps

- **Console programs started from an IDE are allowed**, so he can run his own exercises. A console game started from Code::Blocks or VS Code's terminal would therefore run (rare: browsers and games are windowed). Windowed programs he writes himself (SFML, raylib) are closed; if he needs them, they would need an extra rule (e.g. unsigned windowed programs from his projects folder).
- **A new day while offline:** screen time counts down from the last server value (yesterday's) until the PC reaches the server.
- **Apps installed per user** (OneDrive, Discord, Spotify, Logitech tools in `AppData`) are closed in School mode unless they are on the Allowed apps list. Check the test-mode log.
- No automatic updates yet (Phase 3: `Monitor.Updater`).
