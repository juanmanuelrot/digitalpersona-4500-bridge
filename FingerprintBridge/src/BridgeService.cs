using System;
using System.Threading;

namespace FingerprintBridge
{
    /// <summary>
    /// Core service that coordinates the WebSocket server and fingerprint device manager.
    /// Can be started from the tray app or from a Windows Service host.
    /// </summary>
    public class BridgeService
    {
        private readonly WebSocketServer _wsServer;
        private readonly FingerprintDeviceManager _deviceManager;
        private readonly CancellationTokenSource _cts;

        public event Action<string>? OnStatusChanged;
        public event Action<int>? OnClientCountChanged;

        public bool IsRunning { get; private set; }
        public int Port => _wsServer.Port;

        public BridgeService(int port = 27015)
        {
            _cts = new CancellationTokenSource();
            _wsServer = new WebSocketServer(port);
            _deviceManager = new FingerprintDeviceManager();

            // Wire device events -> WebSocket broadcasts
            _deviceManager.OnDeviceConnected += (deviceId, deviceName) =>
            {
                OnStatusChanged?.Invoke($"Reader connected: {deviceName}");
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "device_connected",
                    DeviceId = deviceId,
                    DeviceName = deviceName
                }).ConfigureAwait(false);
            };

            _deviceManager.OnDeviceDisconnected += () =>
            {
                OnStatusChanged?.Invoke("Reader disconnected");
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "device_disconnected"
                }).ConfigureAwait(false);
            };

            _deviceManager.OnCaptureStarted += () =>
            {
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "capture_started"
                }).ConfigureAwait(false);
            };

            _deviceManager.OnCaptureCompleted += (imageBase64, quality, width, height, resolution) =>
            {
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "capture_completed",
                    ImageData = imageBase64,
                    Quality = quality,
                    ImageWidth = width,
                    ImageHeight = height,
                    ImageResolution = resolution
                }).ConfigureAwait(false);
            };

            _deviceManager.OnCaptureFailed += (errorCode, errorMessage) =>
            {
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "capture_failed",
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage
                }).ConfigureAwait(false);
            };

            _deviceManager.OnFingerDetected += () =>
            {
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "finger_detected"
                }).ConfigureAwait(false);
            };

            _deviceManager.OnFingerRemoved += () =>
            {
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "finger_removed"
                }).ConfigureAwait(false);
            };

            _deviceManager.OnReaderReady += () =>
            {
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "reader_ready"
                }).ConfigureAwait(false);
            };

            _deviceManager.OnError += (code, message) =>
            {
                OnStatusChanged?.Invoke($"Error: {message}");
                _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                {
                    Event = "error",
                    ErrorCode = code,
                    ErrorMessage = message
                }).ConfigureAwait(false);
            };

            // Wire incoming WebSocket commands -> device manager
            _wsServer.OnCommandReceived += async (command) =>
            {
                switch (command.Command?.ToLowerInvariant())
                {
                    case "start_capture":
                        _deviceManager.StartCapture(
                            command.Format ?? "raw",
                            command.Timeout ?? -1
                        );
                        break;

                    case "stop_capture":
                        _deviceManager.StopCapture();
                        break;

                    case "get_status":
                        var status = _deviceManager.GetStatus();
                        await _wsServer.BroadcastAsync(status);
                        break;

                    case "get_devices":
                        var devices = _deviceManager.GetDevices();
                        await _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                        {
                            Event = "device_list",
                            Devices = devices
                        });
                        break;

                    case "select_device":
                        if (command.DeviceId != null)
                        {
                            _deviceManager.SelectDevice(command.DeviceId);
                        }
                        break;

                    default:
                        await _wsServer.BroadcastAsync(new Protocol.OutboundMessage
                        {
                            Event = "error",
                            ErrorCode = "unknown_command",
                            ErrorMessage = $"Unknown command: {command.Command}"
                        });
                        break;
                }
            };
        }

        public void Start()
        {
            if (IsRunning) return;

            Logger.Info("Starting Fingerprint Bridge...");

            _wsServer.OnClientCountChanged += (count) => OnClientCountChanged?.Invoke(count);
            _wsServer.Start(_cts.Token);
            _deviceManager.Start(_cts.Token);

            IsRunning = true;
            OnStatusChanged?.Invoke($"Running on ws://localhost:{Port}");
            Logger.Info($"Fingerprint Bridge started on ws://localhost:{Port}");
        }

        public void Stop()
        {
            if (!IsRunning) return;

            Logger.Info("Stopping Fingerprint Bridge...");

            _cts.Cancel();
            _deviceManager.Stop();
            _wsServer.Stop();

            IsRunning = false;
            OnStatusChanged?.Invoke("Stopped");
            Logger.Info("Fingerprint Bridge stopped");
        }
    }
}
