# Fingerprint Bridge

A Windows tray application that bridges DigitalPersona 4500 fingerprint readers to web frontends via localhost WebSocket.

## Architecture

```
DigitalPersona 4500 USB Reader(s)
         ↓
   Windows HID/WinUSB Driver
         ↓
   DPUruNet.dll (.NET SDK)
         ↓
   FingerprintBridge.exe (this project)
         ↓
   ws://127.0.0.1:27015
         ↓
   Your Frontend (via client-sdk)
```

**No Chrome device permission prompts. No DP Lite Client. No repair loops.**

## How It Works

The bridge **auto-captures continuously on all connected readers**. Each reader gets its own dedicated capture thread. Every finger press is captured and broadcast to all connected WebSocket clients with the `deviceId` that produced it. There are no start/stop capture commands — the frontend simply listens for `capture_completed` events and decides what to do with the data.

Readers can be hot-plugged: the bridge detects new readers every 2 seconds and starts capturing immediately. If a reader is unplugged, the bridge cleans up and fires a `device_disconnected` event.

## Quick Start

### Prerequisites

1. **Windows 10/11 (x64)**
2. **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/8.0)
3. **DigitalPersona Biometric SDK 3.6.1** — install the RTE (runtime) on the target machine
4. **DPUruNet.dll** — copy from your SDK installation to `FingerprintBridge/lib/`

### Build

```bat
cd FingerprintBridge
build.bat
```

### Run (development)

```bat
bin\Release\net8.0-windows\win-x64\publish\FingerprintBridge.exe
```

The app appears as a system tray icon. Right-click for status and controls.

### Create Installer

1. Install [Inno Setup 6](https://jrsoftware.org/isdl.php)
2. Open `FingerprintBridge/installer/setup.iss`
3. Compile → outputs `FingerprintBridge-Setup-1.0.0.exe`

## Frontend Integration

### Install the client SDK

Copy `client-sdk/fingerprint-bridge.ts` into your frontend project.

### Basic usage

```typescript
import { FingerprintBridge } from './fingerprint-bridge';

const bridge = new FingerprintBridge();

// Listen for events — each capture includes the deviceId
bridge.on('device_connected', (data) => {
  console.log('Reader connected:', data.deviceName, data.deviceId);
});

bridge.on('capture_completed', (data) => {
  console.log(`[${data.deviceId}] Quality: ${data.quality}/5`);
  // Display the fingerprint (auto-detects raw vs PNG format)
  const img = document.getElementById('fingerprint') as HTMLImageElement;
  img.src = FingerprintBridge.toDataUrl(data.imageData!, data.imageWidth!, data.imageHeight!);
});

bridge.on('device_disconnected', (data) => {
  console.log('Reader unplugged:', data.deviceId);
});

// Connect — captures start automatically on all connected readers
await bridge.connect();

// Optionally switch to PNG format (default is raw grayscale)
bridge.setFormat('png');

// Or wait for a single capture with a promise
const result = await bridge.waitForCapture();
console.log(result.deviceId, result.quality);
```

## WebSocket Protocol

### Commands (Frontend → Bridge)

| Command | Fields | Description |
|---------|--------|-------------|
| `get_status` | | Get current status of all readers |
| `get_devices` | | List connected readers |
| `set_format` | `format: "raw"\|"png"` | Set capture image format |

### Events (Bridge → Frontend)

| Event | Key Fields | Description |
|-------|-----------|-------------|
| `device_connected` | `deviceId`, `deviceName` | Reader plugged in |
| `device_disconnected` | `deviceId` | Reader unplugged |
| `capture_completed` | `deviceId`, `imageData`, `quality`, `imageWidth`, `imageHeight`, `imageResolution` | Fingerprint captured |
| `capture_failed` | `deviceId`, `errorCode`, `errorMessage` | Capture error |
| `status` | `deviceConnected`, `capturing`, `devices[]` | Status response |
| `device_list` | `devices[]` | List of readers |
| `error` | `errorCode`, `errorMessage` | General error |

### Quality Scores (NFIQ)

| Score | Meaning |
|-------|---------|
| 1 | Excellent |
| 2 | Good |
| 3 | Fair |
| 4 | Poor |
| 5 | Unusable |

## Configuration

The service runs on port **27015** by default. Change it in `BridgeService.cs` constructor.

Logs are written to `%LOCALAPPDATA%\FingerprintBridge\bridge.log`.

## Troubleshooting

| Problem | Solution |
|---------|----------|
| WebSocket connection refused | Check tray icon is running, check port 27015 isn't blocked |
| No reader detected | Ensure DP RTE is installed, reader is plugged in, driver is loaded |
| Capture returns quality 5 | Finger too dry/wet, not enough contact area, clean the sensor |
| `open_failed` error | Another app has exclusive access to the reader — close DP Lite Client |

## License

Your project license here.
