using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DPUruNet;

namespace FingerprintBridge
{
    /// <summary>
    /// Multi-reader DigitalPersona fingerprint manager.
    ///
    /// Architecture:
    ///   A single dedicated STA thread runs a hidden Form + Application.Run()
    ///   which provides a Windows message pump.  ALL SDK calls happen on this
    ///   thread — GetReaders, Open, CaptureAsync, Dispose — so no locks are
    ///   needed.  CaptureAsync arms the reader; when a finger is placed the SDK
    ///   posts a message and the pump dispatches On_Captured on the same thread.
    ///   The callback processes the image and re-arms CaptureAsync.
    ///
    ///   A WinForms Timer (fires on the pump thread) polls for new/removed
    ///   readers every 2 seconds.
    ///
    ///   The only cross-thread calls are:
    ///     • GetStatus()  — reads ConcurrentDictionary (thread-safe)
    ///     • GetDevices() — Invokes to pump thread for SDK access
    ///     • SetFormat()  — sets a volatile field
    ///     • Stop()       — Invokes to pump thread for cleanup
    /// </summary>
    public class FingerprintDeviceManager
    {
        // ── Per-reader state ────────────────────────────────────────────
        private class ReaderState
        {
            public required Reader Reader;
            public required string DeviceId;
            public required string DeviceName;
            public int Resolution;
            public bool CaptureArmed;
        }

        // ── Fields ──────────────────────────────────────────────────────
        private readonly ConcurrentDictionary<string, ReaderState> _activeReaders = new();
        private readonly ConcurrentDictionary<string, DateTime> _openFailCooldowns = new();
        private Form? _pumpForm;
        private Thread? _pumpThread;
        private System.Windows.Forms.Timer? _pollTimer;
        private volatile bool _stopping;
        private volatile string _captureFormat = "raw";
        private static readonly TimeSpan OpenFailCooldown = TimeSpan.FromSeconds(10);

        // ── Events ──────────────────────────────────────────────────────
        public event Action<string, string>? OnDeviceConnected;               // (deviceId, deviceName)
        public event Action<string>? OnDeviceDisconnected;                    // (deviceId)
        public event Action<string, string, int, int, int, int>? OnCaptureCompleted;  // (deviceId, base64, quality, w, h, dpi)
        public event Action<string, string, string>? OnCaptureFailed;         // (deviceId, errorCode, errorMessage)
        public event Action<string, string>? OnError;                         // (errorCode, errorMessage)

        // ── Public properties ───────────────────────────────────────────
        public bool IsConnected => !_activeReaders.IsEmpty;
        public int ReaderCount => _activeReaders.Count;

        // ═══════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a dedicated STA thread with a Windows message pump,
        /// then starts polling for fingerprint readers.
        /// </summary>
        public void Start(CancellationToken ct)
        {
            _stopping = false;
            var ready = new ManualResetEventSlim(false);

            _pumpThread = new Thread(() => RunMessagePump(ready))
            {
                IsBackground = true,
                Name = "FP-SDK-Pump"
            };
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();

            ready.Wait();
            Logger.Info("Fingerprint SDK message pump started");

            ct.Register(() => Stop());
        }

        /// <summary>
        /// The message-pump thread entry point.
        /// Creates a hidden Form, starts a poll timer, and runs Application.Run
        /// which blocks until the form is closed.
        /// </summary>
        private void RunMessagePump(ManualResetEventSlim ready)
        {
            _pumpForm = new Form
            {
                Text = "FP-SDK-Pump",
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000),
                Size = new Size(1, 1)
            };

            _pumpForm.Load += (_, _) =>
            {
                _pumpForm.Visible = false;

                // WinForms timer: Tick fires on the pump thread's message loop
                _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                _pollTimer.Tick += (_, _) => ScanAndOpenNewReaders();
                _pollTimer.Start();

                // Initial scan right away
                ScanAndOpenNewReaders();

                ready.Set();
            };

            Application.Run(_pumpForm);
            Logger.Info("Message pump thread exited");
        }

        /// <summary>
        /// Stops all capture, disposes all readers, and shuts down the message pump.
        /// Safe to call from any thread.
        /// </summary>
        public void Stop()
        {
            if (_stopping) return;
            _stopping = true;

            if (_pumpForm != null && _pumpForm.IsHandleCreated && !_pumpForm.IsDisposed)
            {
                try
                {
                    _pumpForm.Invoke(new Action(() =>
                    {
                        // Stop polling
                        _pollTimer?.Stop();
                        _pollTimer?.Dispose();
                        _pollTimer = null;

                        // Cancel + dispose all readers
                        foreach (var kv in _activeReaders)
                        {
                            try { kv.Value.Reader.CancelCapture(); } catch { }
                            try { kv.Value.Reader.Dispose(); } catch { }
                        }
                        _activeReaders.Clear();

                        // Exit Application.Run → unblocks pump thread
                        _pumpForm.Close();
                    }));
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Stop invoke error (safe to ignore): {ex.Message}");
                }
            }

            _pumpThread?.Join(TimeSpan.FromSeconds(3));
            _openFailCooldowns.Clear();
            Logger.Info("All readers stopped and closed");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Commands (called from WebSocket handler threads)
        // ═══════════════════════════════════════════════════════════════

        public void SetFormat(string format)
        {
            _captureFormat = format == "png" ? "png" : "raw";
            Logger.Info($"Capture format set to: {_captureFormat}");
        }

        public Protocol.OutboundMessage GetStatus()
        {
            var devices = _activeReaders.Values
                .Select(s => new Protocol.DeviceInfo
                {
                    Id = s.DeviceId,
                    Name = s.DeviceName
                })
                .ToArray();

            return new Protocol.OutboundMessage
            {
                Event = "status",
                DeviceConnected = !_activeReaders.IsEmpty,
                Capturing = _activeReaders.Values.Any(s => s.CaptureArmed),
                Devices = devices.Length > 0 ? devices : null
            };
        }

        /// <summary>
        /// Returns all readers the SDK can see.
        /// Invokes to the pump thread so the SDK call is on the right thread.
        /// </summary>
        public Protocol.DeviceInfo[] GetDevices()
        {
            if (_pumpForm == null || !_pumpForm.IsHandleCreated || _pumpForm.IsDisposed)
                return Array.Empty<Protocol.DeviceInfo>();

            Protocol.DeviceInfo[]? result = null;
            try
            {
                _pumpForm.Invoke(new Action(() =>
                {
                    try
                    {
                        var readers = ReaderCollection.GetReaders();
                        result = readers
                            .Select(r =>
                            {
                                var desc = r.Description;
                                return new Protocol.DeviceInfo
                                {
                                    Id = desc.SerialNumber ?? desc.Name,
                                    Name = desc.Name,
                                    SerialNumber = desc.SerialNumber,
                                    ProductName = desc.Id?.ProductName,
                                    Vendor = desc.Id?.VendorName
                                };
                            })
                            .ToArray();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"GetDevices error: {ex.Message}");
                        result = Array.Empty<Protocol.DeviceInfo>();
                    }
                }));
            }
            catch (Exception)
            {
                return Array.Empty<Protocol.DeviceInfo>();
            }

            return result ?? Array.Empty<Protocol.DeviceInfo>();
        }

        // ═══════════════════════════════════════════════════════════════
        //  Device polling  (runs on pump thread via WinForms Timer)
        // ═══════════════════════════════════════════════════════════════

        private void ScanAndOpenNewReaders()
        {
            if (_stopping) return;

            ReaderCollection readers;
            try
            {
                readers = ReaderCollection.GetReaders();
            }
            catch (Exception ex)
            {
                Logger.Error($"GetReaders error: {ex.Message}");
                return;
            }

            if (readers.Count == 0)
                return;

            foreach (var reader in readers)
            {
                string deviceId;
                string deviceName;
                try
                {
                    var desc = reader.Description;
                    deviceId = desc.SerialNumber ?? desc.Name;
                    deviceName = desc.Name;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error reading reader description: {ex.Message}");
                    continue;
                }

                // Already tracking this reader
                if (_activeReaders.ContainsKey(deviceId))
                    continue;

                // Cooldown: skip readers that recently failed
                if (_openFailCooldowns.TryGetValue(deviceId, out var cooldownUntil)
                    && DateTime.UtcNow < cooldownUntil)
                    continue;

                // Try to open
                try
                {
                    Logger.Info($"Opening reader: {deviceName} ({deviceId})...");

                    var result = reader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);
                    if (result != Constants.ResultCode.DP_SUCCESS)
                    {
                        Logger.Warn($"Cooperative open failed ({result}), trying exclusive...");
                        result = reader.Open(Constants.CapturePriority.DP_PRIORITY_EXCLUSIVE);
                    }

                    if (result != Constants.ResultCode.DP_SUCCESS)
                    {
                        Logger.Error($"Failed to open reader {deviceId}: {result}");
                        _openFailCooldowns[deviceId] = DateTime.UtcNow + OpenFailCooldown;
                        OnError?.Invoke("open_failed", $"Could not open reader {deviceName}: {result}");
                        continue;
                    }

                    _openFailCooldowns.TryRemove(deviceId, out _);

                    // Cache resolution now — same thread, safe to access Capabilities
                    int resolution = 0;
                    try
                    {
                        var caps = reader.Capabilities;
                        if (caps?.Resolutions != null && caps.Resolutions.Length > 0)
                            resolution = caps.Resolutions[0];
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not read capabilities for {deviceId}: {ex.Message}");
                    }

                    if (resolution == 0)
                    {
                        resolution = 500; // Default DPI for DP 4500
                        Logger.Info($"Using default resolution {resolution} for {deviceId}");
                    }

                    var state = new ReaderState
                    {
                        Reader = reader,
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        Resolution = resolution
                    };

                    if (!_activeReaders.TryAdd(deviceId, state))
                    {
                        try { reader.Dispose(); } catch { }
                        continue;
                    }

                    // Subscribe to capture callback ONCE per reader.
                    // The callback fires on this (pump) thread because CaptureAsync
                    // posts completion to the calling thread's message queue.
                    reader.On_Captured += new Reader.CaptureCallback(
                        captureResult => HandleCaptured(state, captureResult)
                    );

                    Logger.Info($"Reader opened: {deviceName} (ID: {deviceId}, res: {resolution})");
                    OnDeviceConnected?.Invoke(deviceId, deviceName);

                    // Arm async capture — SDK monitors the device in the background;
                    // On_Captured fires when a finger is placed.
                    ArmCapture(state);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error opening reader {deviceId}: {ex.Message}");
                    OnError?.Invoke("open_error", $"{deviceName}: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Async capture  (runs on pump thread)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Arms CaptureAsync on the reader. When a finger is placed, the SDK
        /// posts a message and On_Captured fires on the pump thread.
        /// SDK signature: CaptureAsync(format, processing, resolution) — no timeout.
        /// </summary>
        private void ArmCapture(ReaderState state)
        {
            if (_stopping) return;

            try
            {
                var threadState = Thread.CurrentThread.GetApartmentState();
                var syncCtx = SynchronizationContext.Current;
                Logger.Info($"[{state.DeviceId}] Arming CaptureAsync (thread={Thread.CurrentThread.Name}, STA={threadState}, SyncCtx={syncCtx?.GetType().Name ?? "null"})");

                // CaptureAsync returns Constants.ResultCode.
                // The SDK monitors the sensor in the background and fires On_Captured
                // when a finger is detected (or on error/cancel).
                var armResult = state.Reader.CaptureAsync(
                    Constants.Formats.Fid.ANSI,
                    Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
                    state.Resolution
                );

                Logger.Info($"[{state.DeviceId}] CaptureAsync returned: {armResult}");

                if (armResult != Constants.ResultCode.DP_SUCCESS)
                {
                    Logger.Error($"[{state.DeviceId}] CaptureAsync FAILED: {armResult}");
                    state.CaptureArmed = false;
                    return;
                }

                state.CaptureArmed = true;
                Logger.Info($"[{state.DeviceId}] Capture armed, waiting for finger...");
            }
            catch (Exception ex)
            {
                Logger.Error($"[{state.DeviceId}] CaptureAsync threw: {ex.GetType().Name}: {ex.Message}");
                state.CaptureArmed = false;

                // If the device is gone, clean up
                if (IsDeviceGoneException(ex))
                {
                    CleanupReader(state);
                }
            }
        }

        /// <summary>
        /// Callback fired by the SDK on the pump thread when a fingerprint
        /// image (or error) is available. Processes the result, fires events,
        /// and re-arms CaptureAsync for the next finger.
        /// </summary>
        private void HandleCaptured(ReaderState state, CaptureResult captureResult)
        {
            var deviceId = state.DeviceId;
            state.CaptureArmed = false;

            // Already cleaned up or shutting down
            if (_stopping || !_activeReaders.ContainsKey(deviceId))
                return;

            try
            {
                Logger.Info($"[{deviceId}] Capture callback: Code={captureResult.ResultCode}, Quality={captureResult.Quality}");

                // ── Device failure — reader likely unplugged ──
                if (captureResult.ResultCode == Constants.ResultCode.DP_DEVICE_FAILURE ||
                    captureResult.ResultCode == Constants.ResultCode.DP_INVALID_DEVICE)
                {
                    Logger.Warn($"[{deviceId}] Device failure: {captureResult.ResultCode}");
                    CleanupReader(state);
                    return;
                }

                // ── Cancelled (Stop() called CancelCapture, or spurious) ──
                if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_CANCELED)
                {
                    Logger.Info($"[{deviceId}] Capture cancelled");
                    if (!_stopping)
                        ArmCapture(state);
                    return;
                }

                // ── Non-success result code ──
                if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
                {
                    Logger.Warn($"[{deviceId}] Capture non-success: {captureResult.ResultCode}");
                    OnCaptureFailed?.Invoke(
                        deviceId,
                        captureResult.ResultCode.ToString(),
                        $"Capture failed: {captureResult.ResultCode}"
                    );
                    if (!_stopping)
                        ArmCapture(state);
                    return;
                }

                // ── Poor quality — report and re-arm ──
                if (captureResult.Quality != Constants.CaptureQuality.DP_QUALITY_GOOD)
                {
                    Logger.Warn($"[{deviceId}] Poor quality: {captureResult.Quality}");
                    // Still try to use the data if we have it, otherwise re-arm
                    if (captureResult.Data == null || captureResult.Data.Views.Count == 0)
                    {
                        OnCaptureFailed?.Invoke(deviceId, "quality", captureResult.Quality.ToString());
                        if (!_stopping)
                            ArmCapture(state);
                        return;
                    }
                    // Fall through — we have data, send it even with imperfect quality
                }

                // ── No image data ──
                if (captureResult.Data == null || captureResult.Data.Views.Count == 0)
                {
                    Logger.Warn($"[{deviceId}] Capture returned no image data");
                    OnCaptureFailed?.Invoke(deviceId, "no_data", "Capture returned no image data");
                    if (!_stopping)
                        ArmCapture(state);
                    return;
                }

                // ════ Successful capture ════
                var view = captureResult.Data.Views[0];
                int width = view.Width;
                int height = view.Height;
                int quality = MapCaptureQuality(captureResult.Quality);

                Logger.Info($"[{deviceId}] Fingerprint captured: {width}x{height} @ {state.Resolution}dpi, quality={quality}");

                string imageBase64;
                if (_captureFormat == "png")
                {
                    imageBase64 = ConvertRawToPngBase64(view.RawImage, width, height);
                }
                else
                {
                    imageBase64 = Convert.ToBase64String(view.RawImage);
                }

                OnCaptureCompleted?.Invoke(deviceId, imageBase64, quality, width, height, state.Resolution);
            }
            catch (Exception ex)
            {
                Logger.Error($"[{deviceId}] Error in capture callback: {ex.GetType().Name}: {ex.Message}");
                OnCaptureFailed?.Invoke(deviceId, "process_error", ex.Message);
            }

            // Re-arm for next capture
            if (!_stopping && _activeReaders.ContainsKey(deviceId))
            {
                ArmCapture(state);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Cleanup
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Removes a reader from tracking, disposes it, and fires OnDeviceDisconnected.
        /// Must be called on the pump thread.
        /// </summary>
        private void CleanupReader(ReaderState state)
        {
            var deviceId = state.DeviceId;
            _activeReaders.TryRemove(deviceId, out _);
            _openFailCooldowns[deviceId] = DateTime.UtcNow + OpenFailCooldown;

            try { state.Reader.Dispose(); }
            catch (Exception ex)
            {
                Logger.Debug($"[{deviceId}] Dispose error (safe to ignore): {ex.Message}");
            }

            Logger.Info($"[{deviceId}] Reader cleaned up");
            OnDeviceDisconnected?.Invoke(deviceId);
        }

        private static bool IsDeviceGoneException(Exception ex)
        {
            var msg = ex.Message.ToLowerInvariant();
            return msg.Contains("device") ||
                   msg.Contains("disposed") ||
                   msg.Contains("handle") ||
                   msg.Contains("disconnected") ||
                   msg.Contains("removed") ||
                   ex is ObjectDisposedException;
        }

        // ═══════════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════════

        private static int MapCaptureQuality(Constants.CaptureQuality q)
        {
            return q switch
            {
                Constants.CaptureQuality.DP_QUALITY_GOOD => 1,
                Constants.CaptureQuality.DP_QUALITY_TIMED_OUT => 5,
                Constants.CaptureQuality.DP_QUALITY_CANCELED => 5,
                Constants.CaptureQuality.DP_QUALITY_NO_FINGER => 5,
                Constants.CaptureQuality.DP_QUALITY_FAKE_FINGER => 5,
                Constants.CaptureQuality.DP_QUALITY_FINGER_TOO_LEFT => 3,
                Constants.CaptureQuality.DP_QUALITY_FINGER_TOO_RIGHT => 3,
                Constants.CaptureQuality.DP_QUALITY_FINGER_TOO_HIGH => 3,
                Constants.CaptureQuality.DP_QUALITY_FINGER_TOO_LOW => 3,
                Constants.CaptureQuality.DP_QUALITY_FINGER_OFF_CENTER => 3,
                Constants.CaptureQuality.DP_QUALITY_SCAN_SKEWED => 4,
                Constants.CaptureQuality.DP_QUALITY_SCAN_TOO_SHORT => 4,
                Constants.CaptureQuality.DP_QUALITY_SCAN_TOO_LONG => 4,
                Constants.CaptureQuality.DP_QUALITY_SCAN_TOO_SLOW => 4,
                Constants.CaptureQuality.DP_QUALITY_SCAN_TOO_FAST => 4,
                Constants.CaptureQuality.DP_QUALITY_SCAN_WRONG_DIRECTION => 4,
                Constants.CaptureQuality.DP_QUALITY_READER_DIRTY => 4,
                _ => 2
            };
        }

        private static string ConvertRawToPngBase64(byte[] rawImage, int width, int height)
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);

            var palette = bmp.Palette;
            for (int i = 0; i < 256; i++)
                palette.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = palette;

            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format8bppIndexed
            );

            try
            {
                if (bmpData.Stride == width)
                {
                    Marshal.Copy(rawImage, 0, bmpData.Scan0, rawImage.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(
                            rawImage, y * width,
                            bmpData.Scan0 + y * bmpData.Stride,
                            width
                        );
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
    }
}
