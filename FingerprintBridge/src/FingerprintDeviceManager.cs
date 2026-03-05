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
    ///   Start() must be called from the main UI thread (the one running
    ///   Application.Run).  ALL SDK calls happen on that thread via its
    ///   existing message pump — no separate threads needed.
    ///
    ///   CaptureAsync arms the reader; the SDK posts a message to the UI
    ///   thread's queue; Application.Run dispatches it → On_Captured fires
    ///   on the UI thread → we process the image and re-arm.
    ///
    ///   A WinForms Timer polls for new/removed readers every 2 seconds,
    ///   also on the UI thread.
    ///
    ///   Cross-thread calls (from WebSocket handlers) are marshalled to
    ///   the UI thread via the captured SynchronizationContext.
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
        private SynchronizationContext? _uiContext;
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
        /// Starts polling for readers and auto-capturing.
        /// MUST be called from the main UI thread (the one running Application.Run)
        /// so that CaptureAsync callbacks are dispatched by the main message pump.
        /// </summary>
        public void Start(CancellationToken ct)
        {
            _stopping = false;

            _uiContext = SynchronizationContext.Current;
            var threadState = Thread.CurrentThread.GetApartmentState();
            Logger.Info($"FingerprintDeviceManager.Start() on thread={Thread.CurrentThread.ManagedThreadId}, STA={threadState}, SyncCtx={_uiContext?.GetType().Name ?? "null"}");

            if (_uiContext == null)
            {
                Logger.Warn("No SynchronizationContext — On_Captured callbacks may not fire. Ensure Start() is called from the UI thread.");
            }

            // WinForms Timer — Tick fires on the UI thread's message loop
            _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _pollTimer.Tick += (_, _) => ScanAndOpenNewReaders();
            _pollTimer.Start();

            // Initial scan immediately
            ScanAndOpenNewReaders();

            Logger.Info("Fingerprint device manager started");
        }

        /// <summary>
        /// Stops all capture, disposes all readers.
        /// Safe to call from any thread — will marshal to UI thread if needed.
        /// </summary>
        public void Stop()
        {
            if (_stopping) return;
            _stopping = true;

            // If we're on the UI thread, stop directly. Otherwise marshal.
            if (_uiContext != null && SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Send(_ => StopInternal(), null);
            }
            else
            {
                StopInternal();
            }
        }

        private void StopInternal()
        {
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;

            foreach (var kv in _activeReaders)
            {
                try { kv.Value.Reader.CancelCapture(); } catch { }
                try { kv.Value.Reader.Dispose(); } catch { }
            }

            _activeReaders.Clear();
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
        /// Marshals to UI thread for the SDK call.
        /// </summary>
        public Protocol.DeviceInfo[] GetDevices()
        {
            if (_uiContext == null)
                return GetDevicesInternal();

            Protocol.DeviceInfo[]? result = null;
            _uiContext.Send(_ =>
            {
                result = GetDevicesInternal();
            }, null);
            return result ?? Array.Empty<Protocol.DeviceInfo>();
        }

        private Protocol.DeviceInfo[] GetDevicesInternal()
        {
            try
            {
                var readers = ReaderCollection.GetReaders();
                return readers
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
                return Array.Empty<Protocol.DeviceInfo>();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Device polling  (runs on UI thread via WinForms Timer)
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

                    // Cache resolution — we're on the UI thread, safe to access Capabilities
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
                    // The callback fires on the UI thread via the main message pump.
                    reader.On_Captured += new Reader.CaptureCallback(
                        captureResult => HandleCaptured(state, captureResult)
                    );

                    Logger.Info($"Reader opened: {deviceName} (ID: {deviceId}, res: {resolution})");
                    OnDeviceConnected?.Invoke(deviceId, deviceName);

                    // Arm async capture
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
        //  Async capture  (runs on UI thread)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Arms CaptureAsync on the reader. When a finger is placed, the SDK
        /// posts a message and On_Captured fires on the UI thread.
        /// SDK signature: CaptureAsync(format, processing, resolution) — no timeout.
        /// </summary>
        private void ArmCapture(ReaderState state)
        {
            if (_stopping) return;

            try
            {
                Logger.Info($"[{state.DeviceId}] Arming CaptureAsync on thread={Thread.CurrentThread.ManagedThreadId}...");

                var armResult = state.Reader.CaptureAsync(
                    Constants.Formats.Fid.ANSI,
                    Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
                    state.Resolution
                );

                if (armResult != Constants.ResultCode.DP_SUCCESS)
                {
                    Logger.Error($"[{state.DeviceId}] CaptureAsync FAILED: {armResult}");
                    state.CaptureArmed = false;
                    return;
                }

                state.CaptureArmed = true;
                Logger.Info($"[{state.DeviceId}] Capture armed (DP_SUCCESS), waiting for finger...");
            }
            catch (Exception ex)
            {
                Logger.Error($"[{state.DeviceId}] CaptureAsync threw: {ex.GetType().Name}: {ex.Message}");
                state.CaptureArmed = false;

                if (IsDeviceGoneException(ex))
                {
                    CleanupReader(state);
                }
            }
        }

        /// <summary>
        /// Callback fired by the SDK on the UI thread when a fingerprint
        /// image (or error) is available. Processes the result, fires events,
        /// and re-arms CaptureAsync for the next finger.
        /// </summary>
        private void HandleCaptured(ReaderState state, CaptureResult captureResult)
        {
            var deviceId = state.DeviceId;
            state.CaptureArmed = false;

            if (_stopping || !_activeReaders.ContainsKey(deviceId))
                return;

            try
            {
                Logger.Info($"[{deviceId}] On_Captured: Code={captureResult.ResultCode}, Quality={captureResult.Quality}");

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

                // ── No image data ──
                if (captureResult.Data == null || captureResult.Data.Views.Count == 0)
                {
                    Logger.Warn($"[{deviceId}] Capture returned no image data (quality={captureResult.Quality})");
                    OnCaptureFailed?.Invoke(deviceId, "no_data", $"No image data, quality: {captureResult.Quality}");
                    if (!_stopping)
                        ArmCapture(state);
                    return;
                }

                // ════ Have image data — send it regardless of quality ════
                var view = captureResult.Data.Views[0];
                int width = view.Width;
                int height = view.Height;
                int quality = MapCaptureQuality(captureResult.Quality);

                Logger.Info($"[{deviceId}] Fingerprint captured: {width}x{height} @ {state.Resolution}dpi, quality={quality} ({captureResult.Quality})");

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
