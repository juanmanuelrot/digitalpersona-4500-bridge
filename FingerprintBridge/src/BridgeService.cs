using System;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintBridge
{
    /// <summary>
    /// Core service: wires the fingerprint device manager to the WebSocket server.
    /// All connected readers capture continuously — every finger press is broadcast
    /// to all connected WebSocket clients with the deviceId that produced it.
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

            // ── Device events → WebSocket broadcasts ──

            _deviceManager.OnDeviceConnected += (deviceId, deviceName) =>
            {
                OnStatusChanged?.Invoke($"Reader connected: {deviceName} ({deviceId})");
                _ = BroadcastSafe(new Protocol.OutboundMessage
                {
                    Event = "device_connected",
                    DeviceId = deviceId,
                    DeviceName = deviceName
                });
            };

            _deviceManager.OnDeviceDisconnected += (deviceId) =>
            {
                OnStatusChanged?.Invoke($"Reader disconnected: {deviceId}");
                _ = BroadcastSafe(new Protocol.OutboundMessage
                {
                    Event = "device_disconnected",
                    DeviceId = deviceId
                });
            };

            _deviceManager.OnCaptureCompleted += (deviceId, imageBase64, quality, width, height, resolution) =>
            {
                _ = BroadcastSafe(new Protocol.OutboundMessage
                {
                    Event = "capture_completed",
                    DeviceId = deviceId,
                    ImageData = imageBase64,
                    Quality = quality,
                    ImageWidth = width,
                    ImageHeight = height,
                    ImageResolution = resolution
                });
            };

            _deviceManager.OnCaptureFailed += (deviceId, errorCode, errorMessage) =>
            {
                _ = BroadcastSafe(new Protocol.OutboundMessage
                {
                    Event = "capture_failed",
                    DeviceId = deviceId,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage
                });
            };

            _deviceManager.OnError += (code, message) =>
            {
                OnStatusChanged?.Invoke($"Error: {message}");
                _ = BroadcastSafe(new Protocol.OutboundMessage
                {
                    Event = "error",
                    ErrorCode = code,
                    ErrorMessage = message
                });
            };

            // ── Incoming WebSocket commands ──

            _wsServer.OnCommandReceived += async (command) =>
            {
                try
                {
                    switch (command.Command?.ToLowerInvariant())
                    {
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

                        case "set_format":
                            _deviceManager.SetFormat(command.Format ?? "raw");
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
                }
                catch (Exception ex)
                {
                    Logger.Error($"Command handler error: {ex.Message}");
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

        /// <summary>
        /// Fire-and-forget broadcast that never throws or deadlocks.
        /// Safe to call from any thread (SDK callbacks, poll thread, etc.)
        /// </summary>
        private async Task BroadcastSafe(Protocol.OutboundMessage msg)
        {
            try
            {
                await _wsServer.BroadcastAsync(msg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"Broadcast error: {ex.Message}");
            }
        }
    }
}
