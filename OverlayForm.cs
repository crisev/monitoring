using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Monitor
{
    public class OverlayForm : Form
    {
        private Label countdownLabel;

        // Constants for window styles
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Make the form click-through and non-focusable
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public OverlayForm()
        {
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            
            // This combination makes the background 100% invisible
            this.BackColor = Color.Magenta;
            this.TransparencyKey = Color.Magenta; 
            
            // This makes whatever is left (the red digits) 30% translucent!
            this.Opacity = 0.30;
            
            this.Size = new Size(400, 150);
            this.StartPosition = FormStartPosition.Manual;
            
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(workingArea.Right - this.Width - 20, 20);

            countdownLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Arial", 48, FontStyle.Bold),
                ForeColor = Color.DodgerBlue,
                BackColor = Color.Transparent,
                Text = "10:00"
            };

            this.Controls.Add(countdownLabel);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        public void UpdateCountdown(TimeSpan remaining)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateCountdown(remaining)));
                return;
            }
            
            countdownLabel.Text = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
            
            if (!this.Visible)
            {
                NativeMethods.ShowWindow(this.Handle, NativeMethods.SW_SHOWNOACTIVATE);
            }
        }

        public void HideOverlay()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(HideOverlay));
                return;
            }

            if (this.Visible)
            {
                this.Hide();
            }
        }
    }

    internal static class NativeMethods
    {
        public const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
