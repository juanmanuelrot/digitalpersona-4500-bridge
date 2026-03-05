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
    /// Architecture (matching the official SDK sample):
    ///
    ///   The official sample ALWAYS runs within a Form context.  The SDK uses
    ///   Windows messages internally — CaptureAsync posts a completion message
    ///   to a window handle (HWND) on the calling thread.  Without a Form,
    ///   there's no HWND, so On_Captured never fires.
    ///
    ///   We create a hidden Form (SdkHostForm) on the UI thread.  This gives
    ///   the SDK a valid HWND.  All SDK calls are issued from this Form's
    ///   thread, and On_Captured callbacks are marshalled back to it via
    ///   Invoke/BeginInvoke — exactly like the official sample's
    ///   InvokeRequired/Invoke pattern.
    ///
    ///   Before each CaptureAsync we call GetStatus() to verify readiness
    ///   and calibrate if needed (official sample pattern).
    ///   After Open() we call SetPAD(true) (official sample pattern).
    /// </summary>
    public class FingerprintDeviceManager
    {
        // ── Hidden form for SDK HWND ──────────────────────────────────
        /// <summary>
        /// Invisible form that provides a window handle for the SDK.
        /// The SDK posts Windows messages to this HWND to dispatch On_Captured.
        /// </summary>
        private class SdkHostForm : Form
        {
            public SdkHostForm()
            {
                Text = "FingerprintBridge_SdkHost";
                ShowInTaskbar = false;
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                Size = new Size(1, 1);
            }

            protected override void SetVisibleCore(bool value)
            {
                // Force handle creation on first Show(), but stay invisible
                if (!IsHandleCreated)
                    CreateHandle();
                base.SetVisibleCore(false);
            }
        }

        // ── Per-reader state ────────────────────────────────────────────
        private class ReaderState
        {
            public required Reader Reader;
            public required string DeviceId;
            public required string DeviceName;
            public int Resolution;
            public volatile bool CaptureArmed;
        }

        // ── Fields ──────────────────────────────────────────────────────
        private readonly ConcurrentDictionary<string, ReaderState> _activeReaders = new();
        private readonly ConcurrentDictionary<string, DateTime> _openFailCooldowns = new();
        private SdkHostForm? _sdkForm;
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
        /// MUST be called from the UI thread (the one running Application.Run).
        /// </summary>
        public void Start(CancellationToken ct)
        {
            _stopping = false;

            Logger.Info($"FingerprintDeviceManager.Start() on thread={Thread.CurrentThread.ManagedThreadId}, STA={Thread.CurrentThread.GetApartmentState()}");

            // ── Create hidden form → gives SDK a window handle ──
            _sdkForm = new SdkHostForm();
            _sdkForm.Show(); // Triggers SetVisibleCore → CreateHandle, stays invisible
            Logger.Info($"SdkHostForm created, Handle={_sdkForm.Handle}, IsHandleCreated={_sdkForm.IsHandleCreated}");

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
        /// </summary>
        public void Stop()
        {
            if (_stopping) return;
            _stopping = true;

            Logger.Info("Stopping fingerprint device manager...");

            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;

            foreach (var kv in _activeReaders)
            {
                try { kv.Value.Reader.CancelCapture(); } catch { }
            }

            // Give callbacks a moment to finish after CancelCapture
            Thread.Sleep(100);

            foreach (var kv in _activeReaders)
            {
                try { kv.Value.Reader.Dispose(); } catch { }
            }

            _activeReaders.Clear();
            _openFailCooldowns.Clear();

            _sdkForm?.Close();
            _sdkForm?.Dispose();
            _sdkForm = null;

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

        public Protocol.DeviceInfo[] GetDevices()
        {
            return GetDevicesInternal();
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

                    // Official sample uses ONLY DP_PRIORITY_COOPERATIVE
                    var result = reader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);

                    if (result != Constants.ResultCode.DP_SUCCESS)
                    {
                        Logger.Error($"Failed to open reader {deviceId}: {result}");
                        _openFailCooldowns[deviceId] = DateTime.UtcNow + OpenFailCooldown;
                        OnError?.Invoke("open_failed", $"Could not open reader {deviceName}: {result}");
                        continue;
                    }

                    _openFailCooldowns.TryRemove(deviceId, out _);

                    // Official sample: SetPAD(true) right after Open
                    try
                    {
                        var padResult = reader.SetPAD(true);
                        Logger.Info($"[{deviceId}] SetPAD(true) result: {padResult}");
                    }
                    catch (Exception padEx)
                    {
                        Logger.Warn($"[{deviceId}] SetPAD failed (non-fatal): {padEx.Message}");
                    }

                    // Cache resolution
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
                    // The callback may fire on a native thread (the official sample
                    // checks InvokeRequired), so we marshal back to the form thread.
                    reader.On_Captured += new Reader.CaptureCallback(
                        captureResult => OnCapturedNative(state, captureResult)
                    );

                    Logger.Info($"Reader opened: {deviceName} (ID: {deviceId}, res: {resolution})");
                    OnDeviceConnected?.Invoke(deviceId, deviceName);

                    // Check status + arm async capture
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
        //  Reader status check  (from official SDK sample pattern)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks reader status before capture — mirrors the official SDK sample's
        /// GetStatus() method.  Handles calibration, busy state, etc.
        /// Returns true if reader is ready to capture.
        /// </summary>
        private bool CheckReaderStatus(ReaderState state)
        {
            try
            {
                var result = state.Reader.GetStatus();

                if (result != Constants.ResultCode.DP_SUCCESS)
                {
                    Logger.Error($"[{state.DeviceId}] GetStatus() returned: {result}");
                    return false;
                }

                var status = state.Reader.Status.Status;
                Logger.Debug($"[{state.DeviceId}] Reader status: {status}");

                if (status == Constants.ReaderStatuses.DP_STATUS_BUSY)
                {
                    Logger.Info($"[{state.DeviceId}] Reader busy, waiting 50ms...");
                    Thread.Sleep(50);
                    return true;
                }
                else if (status == Constants.ReaderStatuses.DP_STATUS_NEED_CALIBRATION)
                {
                    Logger.Info($"[{state.DeviceId}] Reader needs calibration, calibrating...");
                    state.Reader.Calibrate();
                    return true;
                }
                else if (status == Constants.ReaderStatuses.DP_STATUS_READY)
                {
                    return true;
                }
                else
                {
                    Logger.Warn($"[{state.DeviceId}] Unexpected reader status: {status}, attempting capture anyway");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[{state.DeviceId}] CheckReaderStatus exception: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Async capture
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Arms CaptureAsync — official SDK sample pattern:
        ///   1. GetStatus() to verify ready / calibrate
        ///   2. CaptureAsync(format, processing, resolution)
        /// Must run on the UI/form thread.
        /// </summary>
        private void ArmCapture(ReaderState state)
        {
            if (_stopping) return;

            try
            {
                // ── Step 1: Check reader status ──
                if (!CheckReaderStatus(state))
                {
                    Logger.Warn($"[{state.DeviceId}] Reader not ready, will retry on next poll");
                    state.CaptureArmed = false;
                    return;
                }

                // ── Step 2: Arm CaptureAsync ──
                Logger.Info($"[{state.DeviceId}] Arming CaptureAsync (thread={Thread.CurrentThread.ManagedThreadId})...");

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
                Logger.Error($"[{state.DeviceId}] ArmCapture threw: {ex.GetType().Name}: {ex.Message}");
                state.CaptureArmed = false;

                if (IsDeviceGoneException(ex))
                {
                    CleanupReader(state);
                }
            }
        }

        /// <summary>
        /// Raw SDK callback — fires on a native/background thread.
        /// Mirrors the official sample's InvokeRequired pattern:
        /// if we're not on the form thread, marshal to it.
        /// </summary>
        private void OnCapturedNative(ReaderState state, CaptureResult captureResult)
        {
            Logger.Info($"[{state.DeviceId}] On_Captured raw callback, thread={Thread.CurrentThread.ManagedThreadId}");

            if (_sdkForm != null && _sdkForm.IsHandleCreated && _sdkForm.InvokeRequired)
            {
                // Marshal to form thread — exactly like the official sample
                _sdkForm.BeginInvoke(() => HandleCaptured(state, captureResult));
            }
            else
            {
                // Already on form thread (or form is gone)
                HandleCaptured(state, captureResult);
            }
        }

        /// <summary>
        /// Processes capture result on the form/UI thread.
        /// Fires events and re-arms CaptureAsync.
        /// </summary>
        private void HandleCaptured(ReaderState state, CaptureResult captureResult)
        {
            var deviceId = state.DeviceId;
            state.CaptureArmed = false;

            if (_stopping || !_activeReaders.ContainsKey(deviceId))
                return;

            try
            {
                Logger.Info($"[{deviceId}] HandleCaptured: thread={Thread.CurrentThread.ManagedThreadId}, Code={captureResult.ResultCode}, Quality={captureResult.Quality}");

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

                Logger.Info($"[{deviceId}] Fingerprint captured! {width}x{height} @ {state.Resolution}dpi, quality={quality} ({captureResult.Quality})");

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

            // Re-arm for next capture (we're on the form thread)
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

            try { state.Reader.CancelCapture(); } catch { }
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
