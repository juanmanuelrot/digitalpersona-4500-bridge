using System.Text.Json.Serialization;

namespace FingerprintBridge.Protocol
{
    /// <summary>
    /// Commands sent from the frontend to the bridge.
    /// </summary>
    public class InboundMessage
    {
        /// <summary>
        /// The command to execute.
        /// Values: "start_capture", "stop_capture", "get_status", "get_devices", "select_device"
        /// </summary>
        public string? Command { get; set; }

        /// <summary>
        /// Capture format: "raw" (default), "intermediate", "png"
        /// Only used with "start_capture" command.
        /// </summary>
        public string? Format { get; set; }

        /// <summary>
        /// Capture timeout in milliseconds. -1 for no timeout.
        /// Only used with "start_capture" command.
        /// </summary>
        public int? Timeout { get; set; }

        /// <summary>
        /// Device identifier for "select_device" command.
        /// </summary>
        public string? DeviceId { get; set; }
    }

    /// <summary>
    /// Events sent from the bridge to the frontend.
    /// </summary>
    public class OutboundMessage
    {
        /// <summary>
        /// Event type. Values:
        /// "device_connected", "device_disconnected", "device_list",
        /// "capture_started", "capture_completed", "capture_failed",
        /// "finger_detected", "finger_removed", "reader_ready",
        /// "status", "error"
        /// </summary>
        public string? Event { get; set; }

        // --- Device info ---

        /// <summary>Device UID string</summary>
        public string? DeviceId { get; set; }

        /// <summary>Human-readable device name</summary>
        public string? DeviceName { get; set; }

        /// <summary>List of connected devices (for "device_list" event)</summary>
        public DeviceInfo[]? Devices { get; set; }

        // --- Capture data ---

        /// <summary>
        /// Base64-encoded fingerprint image data (raw grayscale or PNG).
        /// Sent with "capture_completed" event.
        /// </summary>
        public string? ImageData { get; set; }

        /// <summary>
        /// NFIQ quality score (1 = best, 5 = unusable).
        /// Sent with "capture_completed" event.
        /// </summary>
        public int? Quality { get; set; }

        /// <summary>Image width in pixels</summary>
        public int? ImageWidth { get; set; }

        /// <summary>Image height in pixels</summary>
        public int? ImageHeight { get; set; }

        /// <summary>Image resolution in DPI</summary>
        public int? ImageResolution { get; set; }

        // --- Status ---

        /// <summary>Whether a device is currently connected</summary>
        public bool? DeviceConnected { get; set; }

        /// <summary>Whether capture is currently active</summary>
        public bool? Capturing { get; set; }

        /// <summary>Device status string from the SDK</summary>
        public string? ReaderStatus { get; set; }

        // --- Errors ---

        /// <summary>Machine-readable error code</summary>
        public string? ErrorCode { get; set; }

        /// <summary>Human-readable error message</summary>
        public string? ErrorMessage { get; set; }
    }

    public class DeviceInfo
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? SerialNumber { get; set; }
        public string? ProductName { get; set; }
        public string? Vendor { get; set; }
    }
}
