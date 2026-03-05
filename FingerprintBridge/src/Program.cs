using System;
using System.Threading;
using System.Windows.Forms;
using FingerprintBridge;

namespace FingerprintBridge
{
    static class Program
    {
        private static Mutex? _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            // Single instance check
            const string mutexName = "Global\\FingerprintBridge_SingleInstance";
            _mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "Fingerprint Bridge is already running.\nCheck the system tray.",
                    "Fingerprint Bridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            // Run as Windows Service if launched with --service flag
            if (args.Length > 0 && args[0] == "--service")
            {
                RunAsService();
                return;
            }

            // Otherwise run as tray application
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }

        private static void RunAsService()
        {
            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var bridge = new BridgeService();
            bridge.Start();

            // Block until cancellation
            try { cts.Token.WaitHandle.WaitOne(); }
            catch { }

            bridge.Stop();
        }
    }
}
