using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DPUruNet;

namespace FingerprintBridge
{
    /// <summary>
    /// Multi-reader DigitalPersona fingerprint manager.
    /// Auto-connects to ALL readers found.
    /// Each reader captures continuously on its own dedicated thread.
    /// Every finger press is reported with its deviceId so the frontend
    /// knows which reader produced it.
    ///
    /// Thread safety:
    ///   _sdkLock serialises every fast SDK call (GetReaders, Open, Dispose, Description).
    ///   Capture() runs lock-free on per-reader threads (it blocks until finger placed).
    ///   CancelCapture() is the only cross-thread same-reader call — the SDK supports this.
    /// </summary>
    public class FingerprintDeviceManager
    {
        // ── Per-reader state ────────────────────────────────────────────
        private class ReaderState
        {
            public required Reader Reader;
            public required string DeviceId;
            public required string DeviceName;
            public Thread? CaptureThread;
            public CancellationTokenSource? CaptureCts;
            public volatile bool Capturing;
        }

        // ── Fields ──────────────────────────────────────────────────────
        private readonly ConcurrentDictionary<string, ReaderState> _activeReaders = new();
        private readonly object _sdkLock = new();
        private CancellationTokenSource? _masterCts;
        private string _captureFormat = "raw";

        // ── Events ──────────────────────────────────────────────────────
        public event Action<string, string>? OnDeviceConnected;               // (deviceId, deviceName)
        public event Action<string>? OnDeviceDisconnected;                    // (deviceId)
        public event Action<string, string, int, int, int, int>? OnCaptureCompleted;  // (deviceId, base64, quality, w, h, dpi)
        public event Action<string, string, string>? OnCaptureFailed;         // (deviceId, errorCode, errorMessage)
        public event Action<string, string>? OnError;                         // (errorCode, errorMessage)

        // ── Public properties ───────────────────────────────────────────
        public bool IsConnected => !_activeReaders.IsEmpty;
        public int ReaderCount => _activeReaders.Count;

        // ── Lifecycle ───────────────────────────────────────────────────

        /// <summary>
        /// Starts the poll thread that scans for readers every 2 seconds.
        /// Each reader found is opened and gets a dedicated capture thread.
        /// </summary>
        public void Start(CancellationToken ct)
        {
            _masterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task.Run(() => PollForDevices(_masterCts.Token), _masterCts.Token);
        }

        /// <summary>
        /// Stops all capture threads and closes all readers.
        /// </summary>
        public void Stop()
        {
            _masterCts?.Cancel();

            // Phase 1: Cancel all capture tokens + CancelCapture to unblock Capture()
            // CancelCapture is safe to call from any thread (SDK-supported).
            foreach (var kv in _activeReaders)
            {
                var state = kv.Value;
                state.Capturing = false;
                state.CaptureCts?.Cancel();
                try { state.Reader.CancelCapture(); } catch { }
            }

            // Phase 2: Wait for capture threads to finish
            foreach (var kv in _activeReaders)
            {
                var state = kv.Value;
                if (state.CaptureThread is { IsAlive: true })
                {
                    state.CaptureThread.Join(TimeSpan.FromSeconds(3));
                }
            }

            // Phase 3: Dispose all readers under SDK lock
            lock (_sdkLock)
            {
                foreach (var kv in _activeReaders)
                {
                    try { kv.Value.Reader.Dispose(); } catch { }
                }
            }

            _activeReaders.Clear();
            Logger.Info("All readers stopped and closed");
        }

        // ── Commands ────────────────────────────────────────────────────

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
                Capturing = _activeReaders.Values.Any(s => s.Capturing),
                Devices = devices.Length > 0 ? devices : null
            };
        }

        /// <summary>
        /// Returns all readers the SDK can see (connected or not yet opened).
        /// </summary>
        public Protocol.DeviceInfo[] GetDevices()
        {
            try
            {
                lock (_sdkLock)
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
            }
            catch (Exception ex)
            {
                Logger.Error($"GetDevices error: {ex.Message}");
                return Array.Empty<Protocol.DeviceInfo>();
            }
        }

        // ── Poll thread ─────────────────────────────────────────────────

        private void PollForDevices(CancellationToken ct)
        {
            Logger.Info("Device poll thread started");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ScanAndOpenNewReaders();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Device poll error: {ex.Message}");
                }

                try { Thread.Sleep(2000); }
                catch (OperationCanceledException) { break; }
            }

            Logger.Info("Device poll thread exiting");
        }

        /// <summary>
        /// Scans for new readers and opens any that are not already active.
        /// Entire scan is under _sdkLock to prevent overlap with Dispose calls.
        /// </summary>
        private void ScanAndOpenNewReaders()
        {
            lock (_sdkLock)
            {
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
                            OnError?.Invoke("open_failed", $"Could not open reader {deviceName}: {result}");
                            continue;
                        }

                        var state = new ReaderState
                        {
                            Reader = reader,
                            DeviceId = deviceId,
                            DeviceName = deviceName
                        };

                        if (!_activeReaders.TryAdd(deviceId, state))
                        {
                            // Race: another thread added it already (shouldn't happen, but safe)
                            try { reader.Dispose(); } catch { }
                            continue;
                        }

                        Logger.Info($"Reader opened: {deviceName} (ID: {deviceId})");
                        OnDeviceConnected?.Invoke(deviceId, deviceName);

                        // Start capture thread (released from _sdkLock — Capture() runs lock-free)
                        var cts = new CancellationTokenSource();
                        state.CaptureCts = cts;
                        state.CaptureThread = new Thread(() => CaptureLoop(state, cts.Token))
                        {
                            Name = $"FP-Capture-{deviceId}",
                            IsBackground = true
                        };
                        state.CaptureThread.Start();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error opening reader {deviceId}: {ex.Message}");
                        OnError?.Invoke("open_error", $"{deviceName}: {ex.Message}");
                    }
                }
            }
        }

        // ── Per-reader capture loop ─────────────────────────────────────

        /// <summary>
        /// Dedicated capture thread for one reader. Blocks on Capture() without
        /// holding any locks. On disconnect or fatal error, removes itself from
        /// _activeReaders, disposes the reader, and exits.
        /// </summary>
        private void CaptureLoop(ReaderState state, CancellationToken ct)
        {
            var deviceId = state.DeviceId;
            var deviceName = state.DeviceName;
            Logger.Info($"[{deviceId}] Capture loop started for {deviceName}");
            state.Capturing = true;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    CaptureResult captureResult;

                    try
                    {
                        Logger.Debug($"[{deviceId}] Waiting for finger...");

                        // ── Blocking call — NO lock held ──
                        captureResult = state.Reader.Capture(
                            Constants.Formats.Fid.ANSI,
                            Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
                            state.Reader.Capabilities.Resolutions[0],
                            -1  // infinite timeout
                        );
                    }
                    catch (ObjectDisposedException)
                    {
                        // Reader was disposed (USB unplug or Stop())
                        Logger.Warn($"[{deviceId}] Reader disposed during capture");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Catch-all for unexpected SDK errors during capture
                        Logger.Error($"[{deviceId}] Capture() threw: {ex.GetType().Name}: {ex.Message}");

                        if (IsDeviceGoneException(ex))
                            break;

                        OnCaptureFailed?.Invoke(deviceId, "capture_exception", ex.Message);
                        Thread.Sleep(200); // Avoid tight error loops
                        continue;
                    }

                    if (ct.IsCancellationRequested)
                        break;

                    // ── Process result ──
                    try
                    {
                        Logger.Info($"[{deviceId}] Capture: Code={captureResult.ResultCode}, Quality={captureResult.Quality}");

                        // Cancelled (CancelCapture was called)
                        if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_CANCELED)
                        {
                            Logger.Info($"[{deviceId}] Capture cancelled");
                            break;
                        }

                        // Device failure — reader likely unplugged
                        if (captureResult.ResultCode == Constants.ResultCode.DP_DEVICE_FAILURE ||
                            captureResult.ResultCode == Constants.ResultCode.DP_INVALID_DEVICE)
                        {
                            Logger.Warn($"[{deviceId}] Device failure: {captureResult.ResultCode}");
                            break;
                        }

                        // Non-success (bad scan quality, etc.)
                        if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
                        {
                            Logger.Warn($"[{deviceId}] Capture non-success: {captureResult.ResultCode}");
                            OnCaptureFailed?.Invoke(
                                deviceId,
                                captureResult.ResultCode.ToString(),
                                $"Capture failed: {captureResult.ResultCode}"
                            );
                            Thread.Sleep(50);
                            continue;
                        }

                        // No image data
                        if (captureResult.Data == null || captureResult.Data.Views.Count == 0)
                        {
                            Logger.Warn($"[{deviceId}] Capture returned no image data");
                            OnCaptureFailed?.Invoke(deviceId, "no_data", "Capture returned no image data");
                            Thread.Sleep(50);
                            continue;
                        }

                        // ════ Successful capture ════
                        var view = captureResult.Data.Views[0];
                        int width = view.Width;
                        int height = view.Height;
                        int resolution = state.Reader.Capabilities.Resolutions[0];
                        int quality = MapCaptureQuality(captureResult.Quality);

                        Logger.Info($"[{deviceId}] Fingerprint: {width}x{height} @ {resolution}dpi, quality={quality}");

                        string imageBase64;
                        if (_captureFormat == "png")
                        {
                            imageBase64 = ConvertRawToPngBase64(view.RawImage, width, height);
                        }
                        else
                        {
                            imageBase64 = Convert.ToBase64String(view.RawImage);
                        }

                        OnCaptureCompleted?.Invoke(deviceId, imageBase64, quality, width, height, resolution);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[{deviceId}] Error processing capture result: {ex.GetType().Name}: {ex.Message}");
                        OnCaptureFailed?.Invoke(deviceId, "process_error", ex.Message);
                    }

                    Thread.Sleep(50); // Brief pause before re-arming
                }
            }
            finally
            {
                state.Capturing = false;
                CleanupReader(state);
                Logger.Info($"[{deviceId}] Capture loop exited");
            }
        }

        // ── Cleanup ─────────────────────────────────────────────────────

        /// <summary>
        /// Removes the reader from tracking, disposes it under _sdkLock,
        /// and fires OnDeviceDisconnected. Safe to call from any thread.
        /// </summary>
        private void CleanupReader(ReaderState state)
        {
            var deviceId = state.DeviceId;

            // Remove from dictionary first (so poll thread doesn't see it as active)
            _activeReaders.TryRemove(deviceId, out _);

            // Dispose under SDK lock
            lock (_sdkLock)
            {
                try { state.Reader.Dispose(); }
                catch (Exception ex)
                {
                    Logger.Debug($"[{deviceId}] Dispose error (safe to ignore): {ex.Message}");
                }
            }

            OnDeviceDisconnected?.Invoke(deviceId);
        }

        /// <summary>
        /// Heuristic: does this exception look like the USB device was yanked?
        /// </summary>
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

        // ── Helpers ─────────────────────────────────────────────────────

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
