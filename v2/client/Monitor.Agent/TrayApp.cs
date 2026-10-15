using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Monitor.Core;
using Monitor.Core.Pipe;

namespace Monitor.Agent;

/// <summary>
/// Tray icon with the status menu and the GAME ON/OFF button, plus notifications. It only displays what
/// the service sends and passes the button on; all decisions are made by the service and the server.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly SynchronizationContext ui;
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem gameLine, screenLine, modeLine, noticeLine, toggleItem;
    private readonly ServiceLink link;
    private readonly CancellationTokenSource cts = new();
    private readonly Dictionary<IconState, Icon> icons = [];
    private StatusForm? form;
    private AgentStatus? status;
    private bool connected;

    public TrayApp()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var font = new Font("Segoe UI", 9.5f);
        gameLine = new ToolStripMenuItem { Enabled = false };
        screenLine = new ToolStripMenuItem { Enabled = false };
        modeLine = new ToolStripMenuItem { Enabled = false };
        noticeLine = new ToolStripMenuItem { Enabled = false, Visible = false };
        toggleItem = new ToolStripMenuItem { Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
        toggleItem.Click += (_, _) => ToggleGame();
        var statusItem = new ToolStripMenuItem("Status…");
        statusItem.Click += (_, _) => ShowStatus();

        var menu = new ContextMenuStrip { Font = font };
        menu.Items.AddRange([modeLine, gameLine, screenLine, noticeLine, new ToolStripSeparator(), toggleItem, new ToolStripSeparator(), statusItem]);

        icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Monitor" };
        icon.DoubleClick += (_, _) => ShowStatus();

        link = new ServiceLink(
            msg => ui.Post(_ => OnMessage(msg), null),
            up => ui.Post(_ => { connected = up; Refresh(); }, null));
        link.Start();
        // Logoff or shutdown: tell the service, so the agent closing is not reported as tampering.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) =>
            link.SendAsync(new PipeMessage { Type = MessageTypes.Bye }).Wait(500);

        var sampler = new Sampler(link.SendAsync);
        new Thread(() => sampler.Run(cts.Token)) { IsBackground = true, Name = "Sampler" }.Start();

        var show = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        new Thread(() =>
        {
            while (WaitHandle.WaitAny([show, cts.Token.WaitHandle]) == 0) ui.Post(_ => ShowStatus(), null);
            show.Dispose();
        }) { IsBackground = true, Name = "ShowStatus" }.Start();

        Refresh();
    }

    public AgentStatus? Status => connected ? status : null;

    public void ToggleGame()
    {
        if (Status is not { } s || s.Busy) return;
        _ = link.SendAsync(new PipeMessage { Type = MessageTypes.Game, On = !s.IsGaming });
    }

    /// <summary>GAME ON is possible: connected, online, game time left, nothing pending.</summary>
    public static bool CanStart(AgentStatus s) => !s.IsGaming && !s.Busy && s.Online && s.Enrolled && s.GameBalanceSeconds > 0;

    private void OnMessage(PipeMessage msg)
    {
        switch (msg.Type)
        {
            case MessageTypes.Status when msg.Status is not null:
                status = msg.Status;
                Refresh();
                break;
            case MessageTypes.Toast:
                icon.ShowBalloonTip(6_000, msg.Title ?? "Monitor", msg.Text ?? "", msg.Warning == true ? ToolTipIcon.Warning : ToolTipIcon.Info);
                break;
            case MessageTypes.Capture:
                _ = Task.Run(async () =>
                {
                    if (Screenshot.CaptureJpeg() is { } jpeg)
                        await link.SendAsync(new PipeMessage { Type = MessageTypes.Screenshot, Jpeg = Convert.ToBase64String(jpeg) });
                });
                break;
            case MessageTypes.CloseBrowsers:
                _ = Task.Run(CloseEdgeWindows);
                break;
        }
    }

    private void Refresh()
    {
        var s = Status;
        if (s is null)
        {
            modeLine.Text = "Monitor";
            gameLine.Text = "Connecting to the Monitor service…";
            screenLine.Visible = false;
            noticeLine.Visible = false;
            toggleItem.Text = "▶️ GAME ON";
            toggleItem.Enabled = false;
            SetIcon(IconState.Offline, "Monitor: connecting…");
        }
        else
        {
            modeLine.Text = s.IsGaming ? "📌 Gaming mode" : "📌 School mode";
            gameLine.Text = s.IsGaming
                ? $"🎮 Gaming: {Format.Duration(s.GameBalanceSeconds)} left"
                : $"🎮 Game time: {Format.Duration(s.GameBalanceSeconds)}";
            screenLine.Visible = true;
            screenLine.Text = s.ScreenRemainingSeconds is int left
                ? $"🖥️ Screen time left today: {Format.Duration(left)}"
                : "🖥️ Screen time: no daily limit";
            noticeLine.Visible = s.Notice is not null;
            noticeLine.Text = s.Notice ?? "";
            if (s.IsGaming)
            {
                toggleItem.Text = "⏹️ GAME OFF";
                toggleItem.ForeColor = Color.Crimson;
                toggleItem.Enabled = !s.Busy;
            }
            else
            {
                toggleItem.Text = s.Busy ? "⏳ Starting…" : s.GameBalanceSeconds > 0 ? "▶️ GAME ON" : "🚫 No game time left";
                toggleItem.ForeColor = Color.DarkGreen;
                toggleItem.Enabled = CanStart(s);
            }
            var state = !s.Online ? IconState.Offline
                : s.IsGaming ? IconState.Gaming
                : s.GameBalanceSeconds <= 0 ? IconState.NoGameTime
                : IconState.School;
            SetIcon(state, s.IsGaming
                ? $"Monitor: GAMING ({Format.Duration(s.GameBalanceSeconds)} left)"
                : $"Monitor: School mode ({Format.Duration(s.GameBalanceSeconds)} game time)");
        }
        form?.UpdateView();
    }

    private void ShowStatus()
    {
        if (form is { IsDisposed: false })
        {
            form.Activate();
            return;
        }
        form = new StatusForm(this);
        form.FormClosed += (_, _) => form = null;
        form.Show();
        form.Activate();
    }

    /// <summary>Asks Edge to close (so streams and preloaded pages stop) when School mode starts; the service closes what remains.</summary>
    private static void CloseEdgeWindows()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == session && p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                }
                catch
                {
                }
            }
        }
    }

    private enum IconState { School, Gaming, NoGameTime, Offline }

    private void SetIcon(IconState state, string tooltip)
    {
        if (!icons.TryGetValue(state, out var ico))
        {
            ico = state switch
            {
                IconState.Gaming => DrawIcon(Color.FromArgb(46, 204, 113), "G"),
                IconState.NoGameTime => DrawIcon(Color.FromArgb(231, 76, 60), "S"),
                IconState.Offline => DrawIcon(Color.FromArgb(127, 140, 141), "!"),
                _ => DrawIcon(Color.FromArgb(52, 152, 219), "S"),
            };
            icons[state] = ico;
        }
        icon.Icon = ico;
        icon.Text = tooltip.Length > 63 ? tooltip[..60] + "…" : tooltip;
    }

    private static Icon DrawIcon(Color color, string letter)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 2, 2, size - 4, size - 4);
            using (var pen = new Pen(Color.White, 2f)) g.DrawEllipse(pen, 3, 3, size - 6, size - 6);
            using var font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(letter, font, Brushes.White, new RectangleF(0, 0, size, size), format);
        }
        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            cts.Cancel();
            link.Dispose();
            icon.Visible = false;
            icon.Dispose();
            foreach (var i in icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }
}
