using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Monitor
{
    public class TrayService
    {
        private static TrayService instance;
        public static TrayService Instance => instance;

        private NotifyIcon notifyIcon;
        private ContextMenuStrip contextMenu;
        private ToolStripMenuItem computerTimeMenuItem;
        private ToolStripMenuItem intervalMenuItem;
        private ToolStripMenuItem gamingSummaryMenuItem;
        private ToolStripMenuItem modeStatusMenuItem;
        private ToolStripMenuItem gameToggleMenuItem;
        private ToolStripMenuItem viewStatsMenuItem;
        private ToolStripMenuItem refreshConfigMenuItem;

        private Form statsForm;
        private Control invokerControl;

        // Current state cache
        private string currentIntervalText = "Loading...";
        private int currentComputerSeconds = 0;
        private int currentScreenSeconds = 0;
        private int currentMaxScreenMinutes = 0;
        private int currentSpentSeconds = 0;
        private int currentAvailableSeconds = 0;
        private bool currentIsGamingMode = false;
        private DailyStatsData cachedStats = null;

        public static void Start()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    instance = new TrayService();
                    instance.Initialize();
                    Console.WriteLine("System Tray Service started successfully.");
                    Application.Run();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error starting TrayService: {ex.Message}\n{ex.StackTrace}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private void Initialize()
        {
            try
            {
                invokerControl = new Control();
                var handle = invokerControl.Handle; // Force handle creation for BeginInvoke

                contextMenu = new ContextMenuStrip();
                contextMenu.Font = new Font("Segoe UI", 9.5f);

                // 1. Total Computer On Time
                computerTimeMenuItem = new ToolStripMenuItem("💻 Total PC On Time: 0m")
                {
                    Enabled = false,
                    ForeColor = Color.DarkSlateGray
                };

                // 2. Computer / School Interval Header
                intervalMenuItem = new ToolStripMenuItem("⏰ Interval: Loading...")
                {
                    Enabled = false,
                    ForeColor = Color.DarkSlateGray,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
                };

                // 3. Gaming Quota Summary
                gamingSummaryMenuItem = new ToolStripMenuItem("🎮 Gaming: 0m / 0m (0m left)")
                {
                    Enabled = false,
                    ForeColor = Color.DarkSlateBlue
                };

                // 4. Current Mode Status
                modeStatusMenuItem = new ToolStripMenuItem("📌 Mode: School (Games Blocked)")
                {
                    Enabled = false,
                    ForeColor = Color.DimGray
                };

                // 5. Action Button: GAME ON / GAME OFF / GAME TIME IS DONE
                gameToggleMenuItem = new ToolStripMenuItem("▶️ GAME ON")
                {
                    Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                    ForeColor = Color.DarkGreen
                };
                gameToggleMenuItem.Click += (s, e) =>
                {
                    Program.ToggleGamingMode();
                };

                // 6. View Status Item
                viewStatsMenuItem = new ToolStripMenuItem("⏱️ View Status...")
                {
                    Font = new Font("Segoe UI", 9.5f)
                };
                viewStatsMenuItem.Click += (s, e) =>
                {
                    ShowStatsDialog();
                };

                // 7. Refresh Config Item
                refreshConfigMenuItem = new ToolStripMenuItem("🔄 Refresh Configuration")
                {
                    Font = new Font("Segoe UI", 9.5f)
                };
                refreshConfigMenuItem.Click += async (s, e) =>
                {
                    await Program.RefreshBlockListsAsync();
                    ShowNotification("Configuration Updated", "Blocklists and intervals refreshed successfully.", ToolTipIcon.Info);
                };

                contextMenu.Items.Add(computerTimeMenuItem);
                contextMenu.Items.Add(intervalMenuItem);
                contextMenu.Items.Add(gamingSummaryMenuItem);
                contextMenu.Items.Add(modeStatusMenuItem);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(gameToggleMenuItem);
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(viewStatsMenuItem);
                contextMenu.Items.Add(refreshConfigMenuItem);

                notifyIcon = new NotifyIcon
                {
                    Text = "Activity Monitor",
                    Visible = true,
                    ContextMenuStrip = contextMenu,
                    Icon = GenerateIcon(TrayIconState.SchoolMode)
                };

                notifyIcon.DoubleClick += (s, e) =>
                {
                    ShowStatsDialog();
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing NotifyIcon: {ex.Message}");
            }
        }

        private void SetTooltipSafe(string text)
        {
            try
            {
                if (notifyIcon == null) return;
                // WinForms NotifyIcon.Text cannot exceed 63 characters!
                if (text.Length > 63)
                {
                    text = text.Substring(0, 60) + "...";
                }
                notifyIcon.Text = text;
            }
            catch { }
        }

        public void UpdateStatus(string intervalText, int spentGamingSeconds, int availableGamingSeconds, int totalComputerSeconds, int totalScreenSeconds, int maxScreenMinutes, bool isGamingMode, DailyStatsData stats = null)
        {
            if (invokerControl == null || !invokerControl.IsHandleCreated) return;

            invokerControl.BeginInvoke(new Action(() =>
            {
                try
                {
                    currentIntervalText = intervalText;
                    currentSpentSeconds = spentGamingSeconds;
                    currentAvailableSeconds = availableGamingSeconds;
                    currentComputerSeconds = totalComputerSeconds;
                    currentScreenSeconds = totalScreenSeconds;
                    currentMaxScreenMinutes = maxScreenMinutes;
                    currentIsGamingMode = isGamingMode;
                    if (stats != null) cachedStats = stats;

                    int remainingSeconds = Math.Max(0, availableGamingSeconds - spentGamingSeconds);
                    int spentMinutes = spentGamingSeconds / 60;
                    int availableMinutes = availableGamingSeconds / 60;
                    int remainingMinutes = remainingSeconds / 60;

                    int screenHours = totalScreenSeconds / 3600;
                    int screenMinutes = (totalScreenSeconds % 3600) / 60;
                    string screenTimeStr = screenHours > 0 ? $"{screenHours}h {screenMinutes}m" : $"{screenMinutes}m";

                    int pcHours = totalComputerSeconds / 3600;
                    int pcMinutes = (totalComputerSeconds % 3600) / 60;
                    string pcTimeStr = pcHours > 0 ? $"{pcHours}h {pcMinutes}m" : $"{pcMinutes}m";

                    // Update Screen Time / PC Time display
                    if (maxScreenMinutes > 0)
                    {
                        computerTimeMenuItem.Text = $"🖥️ Screen Time: {screenTimeStr} / {maxScreenMinutes}m";
                    }
                    else
                    {
                        computerTimeMenuItem.Text = $"🖥️ Screen Time: {screenTimeStr} (PC On: {pcTimeStr})";
                    }

                    // Update Interval display
                    intervalMenuItem.Text = $"⏰ Computer Time: {intervalText}";

                    // Update Gaming Summary display
                    gamingSummaryMenuItem.Text = $"🎮 Gaming: {spentMinutes}m / {availableMinutes}m ({remainingMinutes}m left)";

                    // Update Mode status & Action Button
                    if (remainingSeconds <= 0 && availableGamingSeconds > 0)
                    {
                        modeStatusMenuItem.Text = "📌 Mode: School (Gaming Time Expired)";
                        modeStatusMenuItem.ForeColor = Color.Crimson;

                        gameToggleMenuItem.Text = "🚫 GAME TIME IS DONE";
                        gameToggleMenuItem.ForeColor = Color.Gray;
                        gameToggleMenuItem.Enabled = false;

                        notifyIcon.Icon = GenerateIcon(TrayIconState.Expired);
                        SetTooltipSafe($"Monitor: School (Expired {spentMinutes}m/{availableMinutes}m)");
                    }
                    else if (isGamingMode)
                    {
                        modeStatusMenuItem.Text = "📌 Mode: Gaming Active (Games Allowed)";
                        modeStatusMenuItem.ForeColor = Color.DarkGreen;

                        gameToggleMenuItem.Text = "⏹️ GAME OFF";
                        gameToggleMenuItem.ForeColor = Color.Crimson;
                        gameToggleMenuItem.Enabled = true;

                        notifyIcon.Icon = GenerateIcon(TrayIconState.GamingActive);
                        SetTooltipSafe($"Monitor: GAMING ON ({remainingMinutes}m left)");
                    }
                    else
                    {
                        modeStatusMenuItem.Text = "📌 Mode: School Mode (Games Blocked)";
                        modeStatusMenuItem.ForeColor = Color.DarkBlue;

                        gameToggleMenuItem.Text = "▶️ GAME ON";
                        gameToggleMenuItem.ForeColor = Color.DarkGreen;
                        gameToggleMenuItem.Enabled = true;

                        notifyIcon.Icon = GenerateIcon(TrayIconState.SchoolMode);
                        SetTooltipSafe($"Monitor: School Mode ({remainingMinutes}m game time left)");
                    }

                    // If stats dialog is open, refresh it
                    if (statsForm != null && !statsForm.IsDisposed && statsForm.Visible)
                    {
                        PopulateStatsView();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error updating tray status: {ex.Message}");
                }
            }));
        }

        public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            if (invokerControl == null || !invokerControl.IsHandleCreated) return;

            invokerControl.BeginInvoke(new Action(() =>
            {
                try
                {
                    notifyIcon?.ShowBalloonTip(3000, title, message, icon);
                }
                catch { }
            }));
        }

        private enum TrayIconState
        {
            SchoolMode,
            GamingActive,
            Expired
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private Icon GenerateIcon(TrayIconState state)
        {
            try
            {
                int size = 32;
                using (Bitmap bmp = new Bitmap(size, size))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);

                        Color mainColor;
                        string letter;

                        switch (state)
                        {
                            case TrayIconState.GamingActive:
                                mainColor = Color.FromArgb(46, 204, 113); // Emerald Green
                                letter = "G";
                                break;
                            case TrayIconState.Expired:
                                mainColor = Color.FromArgb(231, 76, 60);  // Red
                                letter = "X";
                                break;
                            case TrayIconState.SchoolMode:
                            default:
                                mainColor = Color.FromArgb(52, 152, 219); // Bright Blue
                                letter = "S";
                                break;
                        }

                        // Outer Circle / Rounded Shield
                        using (Brush brush = new SolidBrush(mainColor))
                        {
                            g.FillEllipse(brush, 2, 2, size - 4, size - 4);
                        }

                        // Inner Border
                        using (Pen pen = new Pen(Color.White, 2f))
                        {
                            g.DrawEllipse(pen, 3, 3, size - 6, size - 6);
                        }

                        // Text character in center
                        using (Font font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (Brush textBrush = new SolidBrush(Color.White))
                        {
                            StringFormat sf = new StringFormat
                            {
                                Alignment = StringAlignment.Center,
                                LineAlignment = StringAlignment.Center
                            };
                            g.DrawString(letter, font, textBrush, new RectangleF(0, 0, size, size), sf);
                        }
                    }

                    IntPtr hIcon = bmp.GetHicon();
                    Icon tempIcon = Icon.FromHandle(hIcon);
                    Icon clonedIcon = (Icon)tempIcon.Clone();
                    DestroyIcon(hIcon);
                    return clonedIcon;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Icon generation exception: {ex.Message}");
                return SystemIcons.Application;
            }
        }

        public void ShowStatsDialog()
        {
            if (invokerControl == null || !invokerControl.IsHandleCreated) return;

            invokerControl.BeginInvoke(new Action(() =>
            {
                if (statsForm != null && !statsForm.IsDisposed)
                {
                    statsForm.BringToFront();
                    statsForm.Focus();
                    return;
                }

                statsForm = new Form
                {
                    Text = "Activity Monitor Status",
                    Size = new Size(520, 360),
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    ShowInTaskbar = true,
                    BackColor = Color.FromArgb(248, 249, 250),
                    Font = new Font("Segoe UI", 9.5f)
                };

                PopulateStatsView();
                statsForm.Show();
            }));
        }

        private void PopulateStatsView()
        {
            if (statsForm == null || statsForm.IsDisposed) return;

            statsForm.SuspendLayout();
            statsForm.Controls.Clear();

            int spentMinutes = currentSpentSeconds / 60;
            int spentSec = currentSpentSeconds % 60;
            int availableMinutes = currentAvailableSeconds / 60;
            int remainingSeconds = Math.Max(0, currentAvailableSeconds - currentSpentSeconds);
            int remainingMinutes = remainingSeconds / 60;
            int remainingSec = remainingSeconds % 60;

            int screenHours = currentScreenSeconds / 3600;
            int screenMinutes = (currentScreenSeconds % 3600) / 60;
            int screenSec = currentScreenSeconds % 60;
            string screenTimeFormatted = screenHours > 0 ? $"{screenHours}h {screenMinutes}m {screenSec}s" : $"{screenMinutes}m {screenSec}s";

            int pcHours = currentComputerSeconds / 3600;
            int pcMinutes = (currentComputerSeconds % 3600) / 60;
            int pcSec = currentComputerSeconds % 60;
            string pcTimeFormatted = pcHours > 0 ? $"{pcHours}h {pcMinutes}m {pcSec}s" : $"{pcMinutes}m {pcSec}s";

            // Top Status Banner
            Panel headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 65,
                BackColor = currentIsGamingMode ? Color.FromArgb(232, 248, 245) : (remainingSeconds <= 0 && currentAvailableSeconds > 0 ? Color.FromArgb(254, 237, 236) : Color.FromArgb(235, 245, 255)),
                Padding = new Padding(16, 10, 16, 10)
            };

            string statusTitle;
            Color statusColor;
            if (remainingSeconds <= 0 && currentAvailableSeconds > 0)
            {
                statusTitle = "🔴 School Mode (Gaming Quota Expired)";
                statusColor = Color.Crimson;
            }
            else if (currentIsGamingMode)
            {
                statusTitle = "🟢 Gaming Mode ACTIVE (Games Allowed)";
                statusColor = Color.DarkGreen;
            }
            else
            {
                statusTitle = "🔵 School Mode (Games Blocked)";
                statusColor = Color.DarkBlue;
            }

            Label titleLabel = new Label
            {
                Text = statusTitle,
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                ForeColor = statusColor,
                AutoSize = true,
                Location = new Point(14, 10)
            };

            Label subtitleLabel = new Label
            {
                Text = currentIsGamingMode ? "Gaming session is currently active and counting down." : "Games and entertainment applications are restricted.",
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = Color.DimGray,
                AutoSize = true,
                Location = new Point(16, 36)
            };

            headerPanel.Controls.Add(titleLabel);
            headerPanel.Controls.Add(subtitleLabel);

            // Center Information Card
            Panel centerPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 14, 20, 12),
                BackColor = Color.White
            };

            Label screenTimeLabel;
            if (currentMaxScreenMinutes > 0)
            {
                int remScreenSec = Math.Max(0, currentMaxScreenMinutes * 60 - currentScreenSeconds);
                screenTimeLabel = new Label
                {
                    Text = $"🖥️ Active Screen Time:       {screenTimeFormatted} / {currentMaxScreenMinutes}m ({remScreenSec / 60}m left)",
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = Color.DarkSlateGray,
                    AutoSize = true,
                    Location = new Point(20, 12)
                };
            }
            else
            {
                screenTimeLabel = new Label
                {
                    Text = $"🖥️ Active Screen Time:       {screenTimeFormatted}",
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    ForeColor = Color.DarkSlateGray,
                    AutoSize = true,
                    Location = new Point(20, 12)
                };
            }

            Label pcTimeLabel = new Label
            {
                Text = $"💻 Total PC On Time:         {pcTimeFormatted}",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                ForeColor = Color.DimGray,
                AutoSize = true,
                Location = new Point(20, 36)
            };

            Label intervalLabel = new Label
            {
                Text = $"⏰ Allowed Computer Hours:   {currentIntervalText}",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                ForeColor = Color.DimGray,
                AutoSize = true,
                Location = new Point(20, 58)
            };

            Label quotaHeaderLabel = new Label
            {
                Text = "🎮 Gaming Time Bank:",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.DarkSlateBlue,
                AutoSize = true,
                Location = new Point(20, 84)
            };

            Label quotaDetailsLabel = new Label
            {
                Text = $"• Time Spent Today:  {spentMinutes}m {spentSec}s\n• Total Allowed:      {availableMinutes}m (including carry-over)\n• Remaining Time:   {remainingMinutes}m {remainingSec}s",
                Font = new Font("Consolas", 10f, FontStyle.Regular),
                ForeColor = remainingSeconds > 0 ? Color.FromArgb(40, 40, 40) : Color.Crimson,
                AutoSize = true,
                Location = new Point(36, 110)
            };

            centerPanel.Controls.Add(screenTimeLabel);
            centerPanel.Controls.Add(pcTimeLabel);
            centerPanel.Controls.Add(intervalLabel);
            centerPanel.Controls.Add(quotaHeaderLabel);
            centerPanel.Controls.Add(quotaDetailsLabel);

            // Bottom Action Panel
            Panel bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = Color.WhiteSmoke,
                Padding = new Padding(16, 10, 16, 10)
            };

            Button toggleBtn = new Button
            {
                Text = currentIsGamingMode ? "⏹️ Turn GAME OFF" : (remainingSeconds > 0 ? "▶️ Turn GAME ON" : "🚫 Game Time Done"),
                Enabled = currentIsGamingMode || remainingSeconds > 0,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = currentIsGamingMode ? Color.Crimson : (remainingSeconds > 0 ? Color.DarkGreen : Color.Gray),
                Size = new Size(175, 34),
                Location = new Point(16, 9),
                UseVisualStyleBackColor = true
            };
            toggleBtn.Click += (s, e) =>
            {
                Program.ToggleGamingMode();
            };

            Button closeBtn = new Button
            {
                Text = "Close",
                Font = new Font("Segoe UI", 9.5f),
                Size = new Size(85, 34),
                Location = new Point(statsForm.ClientSize.Width - 101, 9),
                UseVisualStyleBackColor = true
            };
            closeBtn.Click += (s, e) => statsForm.Close();

            bottomPanel.Controls.Add(toggleBtn);
            bottomPanel.Controls.Add(closeBtn);

            statsForm.Controls.Add(centerPanel);
            statsForm.Controls.Add(headerPanel);
            statsForm.Controls.Add(bottomPanel);
            statsForm.ResumeLayout();
        }
    }
}
