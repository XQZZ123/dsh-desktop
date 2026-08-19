using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshDesktop
{
    /// <summary>
    /// The desktop shell window. On show it makes sure the DSH backend is up
    /// (starts it if needed), then hosts the UI in an embedded WebView2. If
    /// WebView2 is unavailable, it falls back to opening Edge in --app mode so
    /// the user still gets a dedicated desktop window.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly Config _cfg;
        private readonly Backend _backend;

        private WebView2 _web;
        private Panel _webHost;
        private Label _statusLabel;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _statusText;
        private NotifyIcon _tray;
        private Icon _trayIcon;
        private bool _edgeMode;

        public MainForm()
        {
            _cfg = Config.Instance;
            _backend = new Backend(_cfg);

            Text = _cfg.Title;
            // 配合 PerMonitorV2 DPI 感知：控件随显示器缩放，避免模糊
            AutoScaleMode = AutoScaleMode.Dpi;
            Width = 1280;
            Height = 860;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = LoadAppIcon(); }
            catch { }

            _statusStrip = new StatusStrip();
            _statusStrip.Dock = DockStyle.Bottom;
            _statusText = new ToolStripStatusLabel("正在启动…");
            _statusStrip.Items.Add(_statusText);
            Controls.Add(_statusStrip);
            _statusStrip.BringToFront();

            _statusLabel = new Label();
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
            _statusLabel.ForeColor = Color.FromArgb(120, 120, 120);
            try { _statusLabel.Font = new Font("Microsoft YaHei", 12f); }
            catch { }
            _statusLabel.Text = "正在准备 DSH 桌面版…";
            Controls.Add(_statusLabel);

            // WebView2 放进独立的 Fill 容器，StatusStrip 保持 Bottom 且 z 序最上层，
            // 这样状态栏固定在底部，WebView2 自动让出底部空间，不会遮挡页面内容。
            _webHost = new Panel();
            _webHost.Dock = DockStyle.Fill;
            _web = new WebView2();
            _web.Dock = DockStyle.Fill;
            _web.Visible = false;
            _webHost.Controls.Add(_web);
            Controls.Add(_webHost);
            _webHost.BringToFront();

            BuildTray();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                await StartupAsync();
            }
            catch (Exception ex)
            {
                HandleShellFailure(ex.Message);
            }
        }

        private async Task StartupAsync()
        {
            if (_backend.IsUp())
            {
                SetStatus("检测到后端已在运行(" + _cfg.Host + ":" + _cfg.Port + ")，直接连接…");
            }
            else
            {
                SetStatus("正在启动 DSH 后端…");
                string err = await Task.Run(delegate
                {
                    return _backend.StartAndWait(SetStatus, _cfg.StartTimeoutSeconds);
                });
                if (err != null) { HandleShellFailure(err); return; }
            }

            await InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            SetStatus("正在初始化桌面视图…");
            try
            {
                CoreWebView2Environment env = null;
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(null, _cfg.EffectiveUserDataFolder());
                }
                catch { /* fall through: try the default environment */ }

                await _web.EnsureCoreWebView2Async(env);
                try { _web.CoreWebView2.Settings.AreDevToolsEnabled = false; }
                catch { }

                _web.Source = new Uri(_backend.Url);
                _web.Visible = true;
                _statusLabel.Visible = false;
                _edgeMode = false;
                SetStatus("已连接 " + _cfg.Host + ":" + _cfg.Port);
                // 强制容器重排：确保 WebView2 占满状态栏以上区域，不被状态栏遮挡。
                try
                {
                    _webHost.BringToFront();
                    _webHost.PerformLayout();
                    _web.BringToFront();
                }
                catch { }
                try
                {
                    _tray.ShowBalloonTip(3000, "DSH 桌面版", "已连接 " + _backend.Url, ToolTipIcon.Info);
                }
                catch { }
            }
            catch (Exception ex)
            {
                _web.Visible = false;
                EdgeFallback("WebView2 初始化失败: " + ex.Message);
            }
        }

        private void EdgeFallback(string reason)
        {
            _edgeMode = true;
            if (!_cfg.EdgeFallback)
            {
                HandleShellFailure(reason);
                return;
            }
            string edge = Locator.FindEdge();
            if (edge == "")
            {
                HandleShellFailure(reason + "\n未找到 Edge，无法回退。");
                return;
            }
            try
            {
                string udp = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DshDesktop", "EdgeProfile");
                ProcessStartInfo psi = new ProcessStartInfo(edge,
                    "--app=" + _backend.Url
                    + " --user-data-dir=\"" + udp + "\""
                    + " --no-first-run --no-default-browser-check");
                psi.UseShellExecute = true;
                Process.Start(psi);
                SetStatus("已用 Edge 桌面模式打开（WebView2 不可用）");
            }
            catch (Exception ex)
            {
                HandleShellFailure(reason + "\nEdge 回退失败: " + ex.Message);
            }
        }

        private void HandleShellFailure(string message)
        {
            SetStatus("启动失败");
            try
            {
                MessageBox.Show(this, message + "\n\n后端日志: " + _backend.LogPath,
                    _cfg.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            if (_backend.StartedByUs && !_edgeMode)
            {
                try
                {
                    DialogResult r = MessageBox.Show(this,
                        "是否同时关闭 DSH 后端？\n（选择“否”则后端继续运行，稍后可再次连接；选择“取消”则不退出）",
                        _cfg.Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                    if (r == DialogResult.Yes) _backend.Stop();
                }
                catch { }
            }
            try
            {
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_trayIcon != null) _trayIcon.Dispose();
            }
            catch { }
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            try { _trayIcon = LoadAppIcon(); _tray.Icon = _trayIcon; }
            catch { }
            _tray.Text = _cfg.Title;
            _tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem show = new ToolStripMenuItem("显示窗口");
            show.Click += delegate
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            };
            menu.Items.Add(show);
            ToolStripMenuItem quit = new ToolStripMenuItem("退出");
            quit.Click += delegate { Close(); };
            menu.Items.Add(quit);
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            };
        }

        private void SetStatus(string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(SetStatus), text); }
                catch { }
                return;
            }
            _statusText.Text = text;
        }

        private static Icon LoadAppIcon()
        {
            // 优先使用编译进 EXE 的 DeepSeek 鲸鱼图标；取不到时退回运行时绘制。
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) return icon;
            }
            catch { }
            return RenderIcon();
        }

        private static Icon RenderIcon()
        {
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(30, 144, 255)))
                        g.FillRectangle(b, 0, 0, 16, 16);
                    using (Font f = new Font("Arial", 9f, FontStyle.Bold))
                    using (SolidBrush w = new SolidBrush(Color.White))
                        g.DrawString("D", f, w, 3f, 1f);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
