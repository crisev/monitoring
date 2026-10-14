# Install Monitor v2 on a new PC

Step by step, for a Windows 10/11 PC. It takes about 15 minutes. You need:

- **The server already running** ([`SETUP.md`](SETUP.md), steps 1–7) and the web app open on your phone or computer.
- **An administrator account** on the PC (yours), to install.
- **A standard (non-administrator) account for the child.** Administrator accounts are never monitored. Check it in *Settings → Accounts → Other users → (the child) → Change account type → Standard User*.
- **The original Monitor app not running** for the child's account. The two would fight over programs and Edge settings; the installer warns if it sees it.

---

## 1. Get the install package

Use **one** of these. The result is a folder `MonitorV2` containing `install.ps1`, `uninstall.ps1` and `app\`.

**A. GitHub Release (recommended).**
1. Open <https://github.com/crisev/monitoring/releases>.
2. Find the newest **PC client** release (`client-v0.1.N`) and download `MonitorV2-0.1.N.zip`.
3. Releases are made from every client change merged to `main`.

**B. Build artifact.** Use this when there's no release yet, e.g. for a change only on `develop`:
1. Open GitHub → **Actions → v2 client**.
2. Open the newest green run for your branch.
3. Under **Artifacts**, download `MonitorV2-0.1.N`.

**C. Build it yourself.** On a computer with the .NET 10 SDK, run:

```powershell
cd v2\client
.\publish.ps1          # -> v2\client\dist\MonitorV2
```

With A or B, unblock the zip before unpacking it, so Windows doesn't flag every file. In PowerShell:

```powershell
Unblock-File "$HOME\Downloads\MonitorV2-0.1.N.zip"
```

Then right-click it → **Extract All…** and copy the `MonitorV2` folder to the PC, e.g. to `C:\Temp\MonitorV2`. The artifact (B) has no `MonitorV2` folder inside: extract it straight into `C:\Temp\MonitorV2`.

## 2. Get an enrollment code

In the web app: **PCs → Add PC**.
1. Type a name (e.g. `Desktop`) and press **Add PC**.
2. Note the code (e.g. `ABCDE-23456`). It works **once** and is valid for **30 minutes**.

## 3. Install in test mode

On the PC, logged in with **your administrator account**:

1. Start → type `PowerShell` → right-click **Windows PowerShell** → **Run as administrator**.
2. Run, with your device address and the code from step 2:

```powershell
cd C:\Temp\MonitorV2
powershell -ExecutionPolicy Bypass -File .\install.ps1 `
  -ServerUrl https://monitor-device.<your-subdomain>.workers.dev `
  -Code ABCDE23456 `
  -DryRun
```

It ends with `Installed in TEST MODE` and a line `Device "Desktop" (…), server …, token present.`

What it did:
- copied the program to `C:\Program Files\MonitorV2`
- saved the PC's login to `C:\ProgramData\MonitorV2` (only SYSTEM and administrators can read it)
- registered and started the Windows service **Monitor v2**

`-DryRun` means **nothing is closed and the PC is never shut down**; the service only writes down what it *would* do.

## 4. Check that it works

1. **Web app → PCs:** the PC shows **Online** with a version number within a minute.
2. **Log on as the child** (log off yours, or use *Switch user*). Within ~10 seconds a round **S** icon appears in the taskbar's notification area (it may be under the **^** arrow). Double-click it: the status window shows the game time and screen time from the server.
3. **Web app → Today → +15** game minutes. On the PC, the icon's **GAME ON** should now work and turn the icon green (**G**). **GAME OFF** returns to School mode.
4. Use the PC normally as the child for 10–15 minutes: open the programs and sites they need for school.

## 5. Review what would be closed, then turn enforcement on

1. Back on your administrator account, open the newest service log:

   ```powershell
   notepad (Get-ChildItem C:\ProgramData\MonitorV2\logs\service-*.log | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
   ```

2. Look for lines `[test mode] Would close <name> (<path>)`.
3. For each legitimate program (e.g. OneDrive, a driver tool, a school app), add the name before the brackets (without `.exe`) to **web app → Settings → Allowed apps**. Add school websites to **Allowed sites**.
4. Install again **without** `-DryRun`. No code is needed, the PC stays enrolled:

   ```powershell
   cd C:\Temp\MonitorV2
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

   It ends with `Installed.` (no "TEST MODE").

From now on, in School mode, other programs are closed within 5 seconds and Edge only opens the allowed sites. When today's screen time is used up, the PC shuts down after warnings at 10, 5 and 1 minutes.

You can delete `C:\Temp\MonitorV2` now.

## 6. Later: updates

- **Automatic (normal):** when a new PC version is released, Discord says "New PC version … available". Then **web app → PCs → Update to …**. The PC installs it within a minute or two (never while gaming), and puts the old version back by itself if the new one fails.
- **By hand:** get a newer package (step 1), and run `install.ps1` as administrator without `-Code`.

---

## Troubleshooting

| Problem | What to do |
|---|---|
| `install.ps1 cannot be loaded … running scripts is disabled` | Start it exactly as shown: `powershell -ExecutionPolicy Bypass -File .\install.ps1 …` |
| `The script … requires … run as Administrator` | Open PowerShell with **Run as administrator** (step 3). |
| `Enrollment failed: … invalid_code` | The code was used already or is older than 30 minutes: **PCs → New enrollment code**, then run step 3 again. |
| PC stays **Offline** on the PCs page | Check the address in `-ServerUrl` (`monitor-device…`, not `monitor-parent…`). Read the newest `C:\ProgramData\MonitorV2\logs\service-*.log`. |
| No tray icon for the child | Is the account really a standard user? Administrators get no icon. Is the service running? Check with `Get-Service MonitorV2`. |
| A needed program keeps closing | Add its name to **Settings → Allowed apps** (the log line `Closed <name> …` shows the exact name). |
| Show the current state | `& "C:\Program Files\MonitorV2\Monitor.Service.exe" status` (as administrator) |

## Uninstall

As administrator, from any copy of the package:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

It removes the service, the Edge restrictions, the program and `C:\ProgramData\MonitorV2`. Add `-KeepData` to keep the enrollment for a reinstall. Afterwards remove the PC in **web app → PCs → Remove**.
