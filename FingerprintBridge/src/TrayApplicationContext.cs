using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace FingerprintBridge
{
    /// <summary>
    /// Provides a system tray icon with status and controls for the Fingerprint Bridge.
    /// Runs the bridge service without a visible window.
    /// </summary>
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly BridgeService _bridge;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _clientsItem;
        private readonly ToolStripMenuItem _startStopItem;

        public TrayApplicationContext()
        {
            _bridge = new BridgeService();

            // Build context menu
            _statusItem = new ToolStripMenuItem("Status: Starting...")
            {
                Enabled = false
            };

            _clientsItem = new ToolStripMenuItem("Clients: 0")
            {
                Enabled = false
            };

            _startStopItem = new ToolStripMenuItem("Stop Service", null, OnStartStopClicked);

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Fingerprint Bridge v1.0") { Enabled = false, Font = new Font(menu.Font, FontStyle.Bold) });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_statusItem);
            menu.Items.Add(_clientsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startStopItem);
            menu.Items.Add(new ToolStripMenuItem("Open Log File", null, OnOpenLogClicked));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExitClicked));

            // Create tray icon
            _trayIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Fingerprint Bridge",
                Visible = true,
                ContextMenuStrip = menu
            };

            _trayIcon.DoubleClick += (_, _) => ShowStatusBalloon();

            // Wire events
            _bridge.OnStatusChanged += (status) =>
            {
                InvokeOnUI(() =>
                {
                    _statusItem.Text = $"Status: {status}";
                    _trayIcon.Text = $"Fingerprint Bridge\n{status}";
                });
            };

            _bridge.OnClientCountChanged += (count) =>
            {
                InvokeOnUI(() => _clientsItem.Text = $"Clients: {count}");
            };

            // Start the bridge
            try
            {
                _bridge.Start();
                _trayIcon.ShowBalloonTip(
                    2000,
                    "Fingerprint Bridge",
                    $"Service started on ws://localhost:{_bridge.Port}",
                    ToolTipIcon.Info
                );
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(
                    5000,
                    "Fingerprint Bridge - Error",
                    $"Failed to start: {ex.Message}",
                    ToolTipIcon.Error
                );
                Logger.Error($"Startup error: {ex}");
            }
        }

        private void OnStartStopClicked(object? sender, EventArgs e)
        {
            if (_bridge.IsRunning)
            {
                _bridge.Stop();
                _startStopItem.Text = "Start Service";
                _trayIcon.ShowBalloonTip(2000, "Fingerprint Bridge", "Service stopped", ToolTipIcon.Info);
            }
            else
            {
                try
                {
                    _bridge.Start();
                    _startStopItem.Text = "Stop Service";
                    _trayIcon.ShowBalloonTip(2000, "Fingerprint Bridge", "Service started", ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    _trayIcon.ShowBalloonTip(5000, "Error", ex.Message, ToolTipIcon.Error);
                }
            }
        }

        private void OnOpenLogClicked(object? sender, EventArgs e)
        {
            try
            {
                var logPath = Logger.LogFilePath;
                if (System.IO.File.Exists(logPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = logPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show("Log file not found.", "Fingerprint Bridge");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open log: {ex.Message}", "Error");
            }
        }

        private void OnExitClicked(object? sender, EventArgs e)
        {
            _bridge.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }

        private void ShowStatusBalloon()
        {
            var status = _bridge.IsRunning ? "Running" : "Stopped";
            _trayIcon.ShowBalloonTip(
                3000,
                "Fingerprint Bridge",
                $"Status: {status}\nPort: {_bridge.Port}",
                ToolTipIcon.Info
            );
        }

        private static Icon LoadIcon()
        {
            try
            {
                // Try to load embedded icon
                var assembly = Assembly.GetExecutingAssembly();
                var stream = assembly.GetManifestResourceStream("FingerprintBridge.assets.fingerprint.ico");
                if (stream != null)
                {
                    return new Icon(stream);
                }
            }
            catch { }

            // Fallback: try file next to exe
            try
            {
                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var iconPath = System.IO.Path.Combine(exeDir, "assets", "fingerprint.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    return new Icon(iconPath);
                }
            }
            catch { }

            // Last resort: create a simple icon programmatically
            return CreateDefaultIcon();
        }

        private static Icon CreateDefaultIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);

            g.Clear(Color.Transparent);

            // Draw a simple fingerprint-like icon
            using var pen = new Pen(Color.FromArgb(0, 150, 136), 2);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawEllipse(pen, 6, 4, 20, 24);
            g.DrawEllipse(pen, 10, 8, 12, 16);
            g.DrawEllipse(pen, 13, 12, 6, 8);

            var iconHandle = bmp.GetHicon();
            return Icon.FromHandle(iconHandle);
        }

        private void InvokeOnUI(Action action)
        {
            // Use SynchronizationContext if available, otherwise just invoke
            try { action(); }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bridge.Stop();
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
