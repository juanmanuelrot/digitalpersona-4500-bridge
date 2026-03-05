using System;
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
    /// Manages the DigitalPersona fingerprint reader using the DPUruNet .NET SDK.
    /// Handles device discovery, capture lifecycle, and event forwarding.
    /// </summary>
    public class FingerprintDeviceManager
    {
        private ReaderCollection? _readers;
        private Reader? _currentReader;
        private CancellationTokenSource? _pollCts;
        private CancellationTokenSource? _captureCts;
        private Thread? _captureThread;
        private bool _isCapturing;
        private string? _currentDeviceId;

        // --- Events ---
        public event Action<string, string>? OnDeviceConnected;      // (deviceId, deviceName)
        public event Action? OnDeviceDisconnected;
        public event Action? OnCaptureStarted;
        public event Action<string, int, int, int, int>? OnCaptureCompleted;  // (base64Image, quality, width, height, dpi)
        public event Action<string, string>? OnCaptureFailed;         // (errorCode, errorMessage)
        public event Action? OnFingerDetected;
        public event Action? OnFingerRemoved;
        public event Action? OnReaderReady;
        public event Action<string, string>? OnError;                 // (errorCode, errorMessage)

        public bool IsConnected => _currentReader != null;
        public bool IsCapturing => _isCapturing;
        public string? CurrentDeviceId => _currentDeviceId;

        public void Start(CancellationToken ct)
        {
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task.Run(() => PollForDevices(_pollCts.Token), _pollCts.Token);
        }

        public void Stop()
        {
            StopCapture();
            _pollCts?.Cancel();
            CloseCurrentReader();

            try { _readers?.Dispose(); }
            catch { }
            _readers = null;
        }

        /// <summary>
        /// Start continuous capture mode. Each time a finger is placed, the reader
        /// will capture, and the event will fire. It will then immediately re-arm
        /// for the next capture until StopCapture() is called.
        /// </summary>
        public void StartCapture(string format = "raw", int timeout = -1)
        {
            if (_currentReader == null)
            {
                OnError?.Invoke("no_device", "No fingerprint reader connected");
                return;
            }

            if (_isCapturing)
            {
                Logger.Debug("Capture already active, ignoring start_capture");
                return;
            }

            _captureCts = new CancellationTokenSource();
            _isCapturing = true;

            _captureThread = new Thread(() => CaptureLoop(format, timeout, _captureCts.Token))
            {
                IsBackground = true,
                Name = "FingerprintCaptureThread"
            };
            _captureThread.Start();

            OnCaptureStarted?.Invoke();
            Logger.Info("Capture started");
        }

        public void StopCapture()
        {
            if (!_isCapturing) return;

            _isCapturing = false;
            _captureCts?.Cancel();

            // CancelCapture unblocks the blocking Reader.Capture() call
            try { _currentReader?.CancelCapture(); }
            catch { }

            _captureThread?.Join(TimeSpan.FromSeconds(3));
            _captureThread = null;

            Logger.Info("Capture stopped");
        }

        public Protocol.OutboundMessage GetStatus()
        {
            string? readerStatus = null;
            if (_currentReader != null)
            {
                try
                {
                    var result = _currentReader.GetStatus();
                    if (result == Constants.ResultCode.DP_SUCCESS)
                    {
                        readerStatus = _currentReader.Status.Status.ToString();
                    }
                    else
                    {
                        readerStatus = "error";
                    }
                }
                catch { readerStatus = "unknown"; }
            }

            return new Protocol.OutboundMessage
            {
                Event = "status",
                DeviceConnected = IsConnected,
                Capturing = IsCapturing,
                DeviceId = _currentDeviceId,
                ReaderStatus = readerStatus
            };
        }

        public Protocol.DeviceInfo[] GetDevices()
        {
            try
            {
                _readers = ReaderCollection.GetReaders();
                return _readers
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

        public void SelectDevice(string deviceId)
        {
            StopCapture();
            CloseCurrentReader();

            try
            {
                _readers = ReaderCollection.GetReaders();
                foreach (var reader in _readers)
                {
                    var desc = reader.Description;
                    var id = desc.SerialNumber ?? desc.Name;
                    if (id == deviceId || desc.Name == deviceId)
                    {
                        OpenReader(reader);
                        return;
                    }
                }

                OnError?.Invoke("device_not_found", $"Device '{deviceId}' not found");
            }
            catch (Exception ex)
            {
                OnError?.Invoke("select_failed", ex.Message);
            }
        }

        // -------------------------------------------------------------------
        //  Private methods
        // -------------------------------------------------------------------

        private void PollForDevices(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _readers = ReaderCollection.GetReaders();

                    if (_currentReader == null && _readers.Count > 0)
                    {
                        // Auto-connect to the first available reader
                        OpenReader(_readers[0]);
                    }
                    else if (_currentReader != null)
                    {
                        // Check if current reader is still present
                        try
                        {
                            var result = _currentReader.GetStatus();
                            if (result != Constants.ResultCode.DP_SUCCESS ||
                                _currentReader.Status.Status == Constants.ReaderStatuses.DP_STATUS_FAILURE)
                            {
                                Logger.Warn("Reader reported failure status");
                                HandleDeviceDisconnect();
                            }
                        }
                        catch
                        {
                            HandleDeviceDisconnect();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Device poll error: {ex.Message}");
                }

                try { Thread.Sleep(2000); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void OpenReader(Reader reader)
        {
            try
            {
                var result = reader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);
                if (result != Constants.ResultCode.DP_SUCCESS)
                {
                    // Try exclusive mode
                    result = reader.Open(Constants.CapturePriority.DP_PRIORITY_EXCLUSIVE);
                }

                if (result != Constants.ResultCode.DP_SUCCESS)
                {
                    Logger.Error($"Failed to open reader: {result}");
                    OnError?.Invoke("open_failed", $"Could not open reader: {result}");
                    return;
                }

                _currentReader = reader;
                var desc = reader.Description;
                _currentDeviceId = desc.SerialNumber ?? desc.Name;

                Logger.Info($"Reader opened: {desc.Name} (SN: {desc.SerialNumber})");
                OnDeviceConnected?.Invoke(_currentDeviceId, desc.Name);
                OnReaderReady?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error($"OpenReader error: {ex.Message}");
                OnError?.Invoke("open_error", ex.Message);
            }
        }

        private void CloseCurrentReader()
        {
            if (_currentReader != null)
            {
                try { _currentReader.Dispose(); }
                catch { }
                _currentReader = null;
                _currentDeviceId = null;
            }
        }

        private void HandleDeviceDisconnect()
        {
            StopCapture();
            CloseCurrentReader();
            OnDeviceDisconnected?.Invoke();
        }

        /// <summary>
        /// Blocking capture loop that runs on a dedicated thread.
        /// Continuously captures fingerprints until cancelled.
        /// </summary>
        private void CaptureLoop(string format, int timeout, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isCapturing && _currentReader != null)
            {
                try
                {
                    // Check reader status before capture
                    var statusResult = _currentReader.GetStatus();
                    if (statusResult != Constants.ResultCode.DP_SUCCESS)
                    {
                        Logger.Warn($"GetStatus failed: {statusResult}. Waiting...");
                        Thread.Sleep(500);
                        continue;
                    }
                    var readerStatus = _currentReader.Status.Status;
                    if (readerStatus == Constants.ReaderStatuses.DP_STATUS_BUSY ||
                        readerStatus == Constants.ReaderStatuses.DP_STATUS_FAILURE ||
                        readerStatus == Constants.ReaderStatuses.DP_STATUS_NEED_CALIBRATION)
                    {
                        Logger.Warn($"Reader not ready: {readerStatus}. Waiting...");
                        Thread.Sleep(500);
                        continue;
                    }

                    OnReaderReady?.Invoke();

                    // Determine FID format based on requested format
                    var fidFormat = Constants.Formats.Fid.ANSI;

                    // Capture timeout: -1 = no timeout (blocks until finger placed)
                    int captureTimeout = timeout > 0 ? timeout : -1;

                    // This is the blocking call - waits for a finger to be placed
                    // Signature: Capture(Fid.Format, CaptureProcessing, timeout_ms, resolution)
                    CaptureResult captureResult = _currentReader.Capture(
                        fidFormat,
                        Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
                        captureTimeout,
                        _currentReader.Capabilities.Resolutions[0]
                    );

                    if (ct.IsCancellationRequested || !_isCapturing)
                        break;

                    if (captureResult == null)
                    {
                        Logger.Warn("Capture returned null");
                        continue;
                    }

                    if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_CANCELED)
                    {
                        Logger.Debug("Capture was cancelled");
                        break;
                    }

                    if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
                    {
                        OnCaptureFailed?.Invoke(
                            captureResult.ResultCode.ToString(),
                            $"Capture failed: {captureResult.ResultCode}"
                        );
                        Thread.Sleep(200);
                        continue;
                    }

                    if (captureResult.Data == null || captureResult.Data.Views.Count == 0)
                    {
                        OnCaptureFailed?.Invoke("no_data", "Capture returned no image data");
                        continue;
                    }

                    OnFingerDetected?.Invoke();

                    // Extract the image from the first view
                    var view = captureResult.Data.Views[0];
                    int width = view.Width;
                    int height = view.Height;
                    // Resolution from the capture capabilities (DPI)
                    int resolution = _currentReader.Capabilities.Resolutions[0];

                    // Calculate NFIQ quality score (1=best, 5=unusable)
                    int quality = 5; // Worst by default
                    try
                    {
                        // Use captureResult.Quality if available from the SDK
                        if (captureResult.Quality != Constants.CaptureQuality.DP_QUALITY_CANCELED)
                        {
                            // Map SDK quality enum to a 1-5 scale
                            quality = MapCaptureQuality(captureResult.Quality);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Quality assessment failed: {ex.Message}");
                    }

                    // Encode the image
                    string imageBase64;
                    if (format == "png")
                    {
                        imageBase64 = ConvertRawToPngBase64(view.RawImage, width, height);
                    }
                    else
                    {
                        // Send raw grayscale bytes
                        imageBase64 = Convert.ToBase64String(view.RawImage);
                    }

                    OnCaptureCompleted?.Invoke(imageBase64, quality, width, height, resolution);
                    OnFingerRemoved?.Invoke();

                    // Brief pause before re-arming capture
                    Thread.Sleep(100);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Logger.Error($"Capture loop error: {ex.Message}");
                        OnCaptureFailed?.Invoke("capture_exception", ex.Message);
                        Thread.Sleep(1000); // Backoff on error
                    }
                }
            }

            _isCapturing = false;
            Logger.Debug("Capture loop exited");
        }

        /// <summary>
        /// Maps the DPUruNet CaptureQuality enum to an NFIQ-like 1-5 score.
        /// </summary>
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
                _ => 2 // Unknown but captured = assume decent
            };
        }

        /// <summary>
        /// Converts raw 8-bit grayscale image bytes to a Base64-encoded PNG.
        /// </summary>
        private static string ConvertRawToPngBase64(byte[] rawImage, int width, int height)
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);

            // Set up grayscale palette
            var palette = bmp.Palette;
            for (int i = 0; i < 256; i++)
            {
                palette.Entries[i] = Color.FromArgb(i, i, i);
            }
            bmp.Palette = palette;

            // Copy raw pixel data
            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format8bppIndexed
            );

            try
            {
                // Handle stride padding
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

            // Encode to PNG and convert to base64
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
    }
}
