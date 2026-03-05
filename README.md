# Fingerprint Bridge

A Windows tray application that bridges DigitalPersona 4500 fingerprint readers to web frontends via localhost WebSocket.

## Architecture

```
DigitalPersona 4500 USB Reader
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

## Quick Start

### Prerequisites

1. **Windows 10/11 (x64)**
2. **.NET 8 SDK** — [download](https://dotnet.microsoft.com/download/dotnet/8.0)
3. **DigitalPersona Biometric SDK 3.6.1** — install the RTE (runtime) on the target machine
4. **DPUruNet.dll** — copy from your SDK installation to `FingerprintBridge/lib/`

### Build

```bat
cd FingerprintBridge
mkdir lib
copy "C:\Program Files\DigitalPersona\Bin\DotNetApi\DPUruNet.dll" lib\
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

// Listen for events
bridge.on('device_connected', (data) => {
  console.log('Reader connected:', data.deviceName);
});

bridge.on('capture_completed', (data) => {
  console.log(`Quality: ${data.quality}/5`);
  // Display the fingerprint
  const img = document.getElementById('fingerprint') as HTMLImageElement;
  img.src = FingerprintBridge.rawToDataUrl(data.imageData!, data.imageWidth!, data.imageHeight!);
});

bridge.on('finger_detected', () => console.log('Finger placed on reader'));
bridge.on('finger_removed', () => console.log('Finger removed'));

// Connect and start
await bridge.connect();
bridge.startCapture('raw');

// Or capture a single fingerprint
const result = await bridge.captureOnce('png');
```

## WebSocket Protocol

### Commands (Frontend → Bridge)

| Command | Fields | Description |
|---------|--------|-------------|
| `start_capture` | `format?: "raw"\|"png"`, `timeout?: number` | Start continuous capture |
| `stop_capture` | | Stop capturing |
| `get_status` | | Get current status |
| `get_devices` | | List connected readers |
| `select_device` | `deviceId: string` | Select a specific reader |

### Events (Bridge → Frontend)

| Event | Key Fields | Description |
|-------|-----------|-------------|
| `device_connected` | `deviceId`, `deviceName` | Reader plugged in |
| `device_disconnected` | | Reader unplugged |
| `reader_ready` | | Reader ready for capture |
| `capture_started` | | Capture mode active |
| `finger_detected` | | Finger placed on sensor |
| `capture_completed` | `imageData`, `quality`, `imageWidth`, `imageHeight`, `imageResolution` | Fingerprint captured |
| `capture_failed` | `errorCode`, `errorMessage` | Capture error |
| `finger_removed` | | Finger lifted from sensor |
| `status` | `deviceConnected`, `capturing`, `readerStatus` | Status response |
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
