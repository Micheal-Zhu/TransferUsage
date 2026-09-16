using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BalanceDock.Services
{
    public sealed class TrayIconService : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _topmostItem;
        private readonly ToolStripMenuItem _startupItem;

        public event EventHandler ShowRequested;
        public event EventHandler ExitRequested;
        public event EventHandler TopmostChanged;
        public event EventHandler StartupChanged;

        public bool TopmostChecked { get { return _topmostItem.Checked; } set { _topmostItem.Checked = value; } }
        public bool StartupChecked { get { return _startupItem.Checked; } set { _startupItem.Checked = value; } }

        public TrayIconService(bool topmost, bool startup)
        {
            var menu = new ContextMenuStrip();
            var showItem = new ToolStripMenuItem("显示余额窗口");
            _topmostItem = new ToolStripMenuItem("始终置顶") { CheckOnClick = true, Checked = topmost };
            _startupItem = new ToolStripMenuItem("开机启动") { CheckOnClick = true, Checked = startup };
            var exitItem = new ToolStripMenuItem("退出");
            menu.Items.Add(showItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_topmostItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            showItem.Click += delegate { Raise(ShowRequested); };
            exitItem.Click += delegate { Raise(ExitRequested); };
            _topmostItem.CheckedChanged += delegate { Raise(TopmostChanged); };
            _startupItem.CheckedChanged += delegate { Raise(StartupChanged); };

            _notifyIcon = new NotifyIcon
            {
                Icon = CreateIcon(),
                Text = "Balance Dock",
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += delegate { Raise(ShowRequested); };
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static Icon CreateIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (var path = RoundedRectangle(new RectangleF(2, 2, 28, 28), 7f))
                using (var brush = new SolidBrush(Color.FromArgb(16, 163, 127)))
                    graphics.FillPath(brush, path);
                using (var pen = new Pen(Color.White, 2.4f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    graphics.DrawLine(pen, 8, 12, 23, 12);
                    graphics.DrawLine(pen, 8, 19, 18, 19);
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
