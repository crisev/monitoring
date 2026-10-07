using System.Drawing;
using Monitor.Core;

namespace Monitor.Agent;

/// <summary>The status dialog: mode, game time, screen time, connection, and the GAME ON/OFF button.</summary>
internal sealed class StatusForm : Form
{
    private readonly TrayApp app;
    private readonly Panel header;
    private readonly Label title, subtitle, gameLeft, gameToday, screenLeft, connection, notice;
    private readonly Button toggle;

    public StatusForm(TrayApp app)
    {
        this.app = app;
        Text = "Monitor";
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 330);
        BackColor = Color.White;

        header = new Panel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(18, 12, 18, 8) };
        title = new Label { AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold), Location = new Point(16, 10) };
        subtitle = new Label { AutoSize = true, ForeColor = Color.DimGray, Location = new Point(18, 42) };
        header.Controls.AddRange([title, subtitle]);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18, 14, 18, 6),
            AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        gameLeft = AddRow(grid, "🎮 Game time left");
        gameToday = AddRow(grid, "Played today");
        screenLeft = AddRow(grid, "🖥️ Screen time left today");
        connection = AddRow(grid, "Server");
        notice = new Label { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.Firebrick, Margin = new Padding(3, 10, 3, 3) };
        grid.Controls.Add(notice, 0, grid.RowCount);
        grid.SetColumnSpan(notice, 2);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Color.FromArgb(245, 246, 248),
        };
        var close = new Button { Text = "Close", AutoSize = true, Height = 34 };
        close.Click += (_, _) => Close();
        toggle = new Button { AutoSize = true, Height = 34, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), MinimumSize = new Size(170, 34) };
        toggle.Click += (_, _) => app.ToggleGame();
        bottom.Controls.AddRange([close, toggle]);

        Controls.Add(grid);
        Controls.Add(bottom);
        Controls.Add(header);
        CancelButton = close;
        UpdateView();
    }

    private static Label AddRow(TableLayoutPanel grid, string caption)
    {
        int row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 6) }, 0, row);
        var value = new Label { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(3, 6, 3, 6) };
        grid.Controls.Add(value, 1, row);
        return value;
    }

    public void UpdateView()
    {
        if (IsDisposed) return;
        var s = app.Status;
        if (s is null)
        {
            header.BackColor = Color.FromArgb(240, 240, 240);
            title.Text = "Connecting…";
            title.ForeColor = Color.DimGray;
            subtitle.Text = "Waiting for the Monitor service.";
            gameLeft.Text = gameToday.Text = screenLeft.Text = connection.Text = "–";
            notice.Text = "";
            toggle.Text = "▶️ GAME ON";
            toggle.Enabled = false;
            return;
        }

        if (s.IsGaming)
        {
            header.BackColor = Color.FromArgb(232, 248, 240);
            title.Text = "🟢 Gaming";
            title.ForeColor = Color.DarkGreen;
            subtitle.Text = "Game time is counting down. Turn it off to save the rest.";
        }
        else
        {
            header.BackColor = Color.FromArgb(235, 245, 255);
            title.Text = "🔵 School mode";
            title.ForeColor = Color.FromArgb(20, 70, 150);
            subtitle.Text = "Only school apps and websites are allowed.";
        }

        gameLeft.Text = s.IsGaming ? Format.Countdown(s.GameBalanceSeconds) : Format.Duration(s.GameBalanceSeconds);
        gameLeft.ForeColor = s.GameBalanceSeconds > 0 ? Color.Black : Color.Crimson;
        gameToday.Text = Format.Duration(s.GameUsedTodaySeconds);
        screenLeft.Text = s.ScreenRemainingSeconds is int left
            ? (left < 3600 ? Format.Countdown(left) : Format.Duration(left))
            : "no limit";
        screenLeft.ForeColor = s.ScreenRemainingSeconds is int l && l <= 600 ? Color.Crimson : Color.Black;
        connection.Text = !s.Enrolled ? "not set up"
            : s.Online ? "connected"
            : s.OfflineRemainingSeconds is int o ? $"offline ({Format.Duration(o)} left)" : "offline";
        connection.ForeColor = s.Online ? Color.DarkGreen : Color.Crimson;
        notice.Text = s.Notice ?? "";

        if (s.IsGaming)
        {
            toggle.Text = "⏹️ GAME OFF";
            toggle.ForeColor = Color.Crimson;
            toggle.Enabled = !s.Busy;
        }
        else
        {
            toggle.Text = s.Busy ? "⏳ Starting…" : s.GameBalanceSeconds > 0 ? "▶️ GAME ON" : "🚫 No game time";
            toggle.ForeColor = Color.DarkGreen;
            toggle.Enabled = TrayApp.CanStart(s);
        }
    }
}
