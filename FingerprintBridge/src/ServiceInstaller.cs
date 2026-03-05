using System;
using System.Diagnostics;

namespace FingerprintBridge
{
    /// <summary>
    /// Helper to install/uninstall the application as a Windows Service using sc.exe.
    /// Also handles adding startup registry entry for tray mode.
    /// </summary>
    public static class ServiceInstaller
    {
        private const string ServiceName = "FingerprintBridge";
        private const string DisplayName = "Fingerprint Bridge";
        private const string Description = "WebSocket bridge for DigitalPersona fingerprint readers";

        /// <summary>
        /// Install as a Windows Service that runs the bridge in --service mode.
        /// </summary>
        public static bool InstallService(string exePath)
        {
            try
            {
                // Create the service
                RunSc($"create \"{ServiceName}\" binPath= \"\\\"{exePath}\\\" --service\" start= auto DisplayName= \"{DisplayName}\"");

                // Set description
                RunSc($"description \"{ServiceName}\" \"{Description}\"");

                // Configure auto-restart on failure
                RunSc($"failure \"{ServiceName}\" reset= 60 actions= restart/5000/restart/10000/restart/30000");

                Logger.Info("Windows Service installed successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to install service: {ex.Message}");
                return false;
            }
        }

        public static bool UninstallService()
        {
            try
            {
                RunSc($"stop \"{ServiceName}\"");
                System.Threading.Thread.Sleep(2000);
                RunSc($"delete \"{ServiceName}\"");
                Logger.Info("Windows Service uninstalled");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to uninstall service: {ex.Message}");
                return false;
            }
        }

        public static bool StartService()
        {
            try
            {
                RunSc($"start \"{ServiceName}\"");
                return true;
            }
            catch { return false; }
        }

        public static bool StopService()
        {
            try
            {
                RunSc($"stop \"{ServiceName}\"");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Add the tray application to Windows startup.
        /// </summary>
        public static void AddToStartup(string exePath)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("FingerprintBridge", $"\"{exePath}\"");
                Logger.Info("Added to Windows startup");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to add to startup: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove the tray application from Windows startup.
        /// </summary>
        public static void RemoveFromStartup()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue("FingerprintBridge", false);
                Logger.Info("Removed from Windows startup");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to remove from startup: {ex.Message}");
            }
        }

        private static void RunSc(string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);

            var output = proc?.StandardOutput.ReadToEnd();
            var error = proc?.StandardError.ReadToEnd();

            if (proc?.ExitCode != 0)
            {
                Logger.Warn($"sc.exe {arguments} -> exit {proc?.ExitCode}: {output} {error}");
            }
        }
    }
}
