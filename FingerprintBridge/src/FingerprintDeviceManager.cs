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
    /// Simplified DigitalPersona fingerprint reader manager.
    /// Auto-connects to the first reader found.
    /// Auto-captures continuously — every finger press is reported.
    /// The frontend decides what to do with the data.
    /// </summary>
    public class FingerprintDeviceManager
    {
        private ReaderCollection? _readers;
        private Reader? _currentReader;
        private CancellationTokenSource? _pollCts;
        private string? _currentDeviceId;
        private string _captureFormat = "raw";
        private volatile bool _capturing;

        // --- Events (always fire, frontend decides what to do) ---
        public event Action<string, string>? OnDeviceConnected;      // (deviceId, deviceName)
        public event Action? OnDeviceDisconnected;
        public event Action<string, int, int, int, int>? OnCaptureCompleted;  // (base64Image, quality, width, height, dpi)
        public event Action<string, string>? OnCaptureFailed;         // (errorCode, errorMessage)
        public event Action<string, string>? OnError;                 // (errorCode, errorMessage)

        public bool IsConnected => _currentReader != null;
        public string? CurrentDeviceId => _currentDeviceId;

        /// <summary>
        /// Starts device polling. When a reader is found, it opens it and
        /// immediately begins continuous async capture.
        /// </summary>
        public void Start(CancellationToken ct)
        {
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task.Run(() => PollForDevices(_pollCts.Token), _pollCts.Token);
        }

        public void Stop()
        {
            _pollCts?.Cancel();
            StopCapturing();
            CloseCurrentReader();
            try { _readers?.Dispose(); } catch { }
            _readers = null;
        }

        public void SetFormat(string format)
        {
            _captureFormat = format == "png" ? "png" : "raw";
        }

        public Protocol.OutboundMessage GetStatus()
        {
            return new Protocol.OutboundMessage
            {
                Event = "status",
                DeviceConnected = IsConnected,
                Capturing = _capturing,
                DeviceId = _currentDeviceId
            };
        }

        public Protocol.DeviceInfo[] GetDevices()
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

        public void SelectDevice(string deviceId)
        {
            StopCapturing();
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
                        OpenAndCapture(reader);
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
        //  Private
        // -------------------------------------------------------------------

        private void PollForDevices(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_currentReader == null)
                    {
                        // No reader — try to find one
                        _readers = ReaderCollection.GetReaders();
                        if (_readers.Count > 0)
                        {
                            OpenAndCapture(_readers[0]);
                        }
                    }
                    // When _currentReader != null, we do NOT poll status.
                    // The async capture callback handles errors/disconnects.
                }
                catch (Exception ex)
                {
                    Logger.Error($"Device poll error: {ex.Message}");
                }

                try { Thread.Sleep(2000); }
                catch (OperationCanceledException) { break; }
            }
        }

        private void OpenAndCapture(Reader reader)
        {
            try
            {
                var result = reader.Open(Constants.CapturePriority.DP_PRIORITY_COOPERATIVE);
                if (result != Constants.ResultCode.DP_SUCCESS)
                {
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

                // Subscribe to async capture callback and start capturing
                _currentReader.On_Captured += OnCapturedCallback;
                ArmCapture();
            }
            catch (Exception ex)
            {
                Logger.Error($"OpenAndCapture error: {ex.Message}");
                OnError?.Invoke("open_error", ex.Message);
            }
        }

        /// <summary>
        /// Arms the reader for async capture. When a finger is placed,
        /// OnCapturedCallback fires automatically.
        /// </summary>
        private void ArmCapture()
        {
            if (_currentReader == null) return;

            try
            {
                _capturing = true;
                _currentReader.CaptureAsync(
                    Constants.Formats.Fid.ANSI,
                    Constants.CaptureProcessing.DP_IMG_PROC_DEFAULT,
                    _currentReader.Capabilities.Resolutions[0]
                );
                Logger.Info("Capture armed — waiting for finger...");
            }
            catch (Exception ex)
            {
                Logger.Error($"ArmCapture error: {ex.Message}");
                _capturing = false;
                HandleDeviceDisconnect();
            }
        }

        /// <summary>
        /// SDK callback — fires on the SDK's internal thread whenever a
        /// capture completes (finger placed) or fails.
        /// </summary>
        private void OnCapturedCallback(CaptureResult captureResult)
        {
            try
            {
                if (_currentReader == null)
                    return;

                Logger.Info($"Captured: ResultCode={captureResult.ResultCode}, Quality={captureResult.Quality}");

                // Cancelled (we called CancelCapture)
                if (captureResult.Quality == Constants.CaptureQuality.DP_QUALITY_CANCELED)
                {
                    Logger.Info("Capture was cancelled");
                    _capturing = false;
                    return;
                }

                // Reader error — likely unplugged
                if (captureResult.ResultCode == Constants.ResultCode.DP_DEVICE_FAILURE ||
                    captureResult.ResultCode == Constants.ResultCode.DP_INVALID_DEVICE)
                {
                    Logger.Warn($"Reader error: {captureResult.ResultCode}");
                    HandleDeviceDisconnect();
                    return;
                }

                // Non-success capture
                if (captureResult.ResultCode != Constants.ResultCode.DP_SUCCESS)
                {
                    OnCaptureFailed?.Invoke(
                        captureResult.ResultCode.ToString(),
                        $"Capture failed: {captureResult.ResultCode}"
                    );
                    // Re-arm for next capture
                    ArmCapture();
                    return;
                }

                // No image data
                if (captureResult.Data == null || captureResult.Data.Views.Count == 0)
                {
                    OnCaptureFailed?.Invoke("no_data", "Capture returned no image data");
                    ArmCapture();
                    return;
                }

                // === Successful capture ===
                var view = captureResult.Data.Views[0];
                int width = view.Width;
                int height = view.Height;
                int resolution = _currentReader!.Capabilities.Resolutions[0];
                int quality = MapCaptureQuality(captureResult.Quality);

                Logger.Info($"Fingerprint: {width}x{height} @ {resolution}dpi, quality={quality}");

                string imageBase64;
                if (_captureFormat == "png")
                {
                    imageBase64 = ConvertRawToPngBase64(view.RawImage, width, height);
                }
                else
                {
                    imageBase64 = Convert.ToBase64String(view.RawImage);
                }

                OnCaptureCompleted?.Invoke(imageBase64, quality, width, height, resolution);
            }
            catch (Exception ex)
            {
                Logger.Error($"OnCapturedCallback error: {ex.GetType().Name}: {ex.Message}");
                OnCaptureFailed?.Invoke("capture_exception", ex.Message);
            }
            finally
            {
                // Always re-arm for the next capture (unless disconnected)
                if (_currentReader != null && _capturing)
                {
                    try
                    {
                        Thread.Sleep(50); // Brief pause
                        ArmCapture();
                    }
                    catch { }
                }
            }
        }

        private void StopCapturing()
        {
            _capturing = false;
            try { _currentReader?.CancelCapture(); } catch { }
        }

        private void CloseCurrentReader()
        {
            if (_currentReader != null)
            {
                try { _currentReader.On_Captured -= OnCapturedCallback; } catch { }
                try { _currentReader.Dispose(); } catch { }
                _currentReader = null;
                _currentDeviceId = null;
            }
        }

        private void HandleDeviceDisconnect()
        {
            _capturing = false;
            CloseCurrentReader();
            OnDeviceDisconnected?.Invoke();
        }

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
