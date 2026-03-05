/**
 * Fingerprint Bridge Client SDK
 *
 * TypeScript client library for communicating with the Fingerprint Bridge
 * WebSocket service running on localhost.
 *
 * Usage:
 *   const bridge = new FingerprintBridge();
 *   bridge.on('capture_completed', (data) => {
 *     console.log('Fingerprint captured!', data.quality, data.imageData);
 *   });
 *   await bridge.connect();
 *   await bridge.startCapture();
 */

// ---------- Types ----------

export interface DeviceInfo {
  id: string;
  name: string;
  serialNumber?: string;
  productName?: string;
  vendor?: string;
}

export interface CaptureData {
  imageData: string;     // Base64-encoded image (raw grayscale or PNG)
  quality: number;       // NFIQ score: 1 (best) to 5 (unusable)
  imageWidth: number;
  imageHeight: number;
  imageResolution: number; // DPI
}

export interface StatusData {
  deviceConnected: boolean;
  capturing: boolean;
  deviceId?: string;
  readerStatus?: string;
}

export interface ErrorData {
  errorCode: string;
  errorMessage: string;
}

export interface BridgeEvent {
  event: string;
  // Device events
  deviceId?: string;
  deviceName?: string;
  devices?: DeviceInfo[];
  // Capture events
  imageData?: string;
  quality?: number;
  imageWidth?: number;
  imageHeight?: number;
  imageResolution?: number;
  // Status
  deviceConnected?: boolean;
  capturing?: boolean;
  readerStatus?: string;
  // Error
  errorCode?: string;
  errorMessage?: string;
}

export type CaptureFormat = 'raw' | 'png' | 'intermediate';

export interface FingerprintBridgeOptions {
  /** WebSocket port (default: 27015) */
  port?: number;
  /** Auto-reconnect on disconnect (default: true) */
  autoReconnect?: boolean;
  /** Reconnect interval in ms (default: 3000) */
  reconnectInterval?: number;
  /** Max reconnect attempts (default: Infinity) */
  maxReconnectAttempts?: number;
}

type EventCallback = (data: BridgeEvent) => void;

// ---------- Client ----------

export class FingerprintBridge {
  private ws: WebSocket | null = null;
  private options: Required<FingerprintBridgeOptions>;
  private listeners: Map<string, Set<EventCallback>> = new Map();
  private reconnectAttempts = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private intentionalClose = false;
  private _isConnected = false;

  constructor(options?: FingerprintBridgeOptions) {
    this.options = {
      port: options?.port ?? 27015,
      autoReconnect: options?.autoReconnect ?? true,
      reconnectInterval: options?.reconnectInterval ?? 3000,
      maxReconnectAttempts: options?.maxReconnectAttempts ?? Infinity,
    };
  }

  // ---------- Connection ----------

  /** Connect to the Fingerprint Bridge service */
  connect(): Promise<void> {
    return new Promise((resolve, reject) => {
      if (this.ws?.readyState === WebSocket.OPEN) {
        resolve();
        return;
      }

      this.intentionalClose = false;

      try {
        this.ws = new WebSocket(`ws://127.0.0.1:${this.options.port}/`);
      } catch (err) {
        reject(new Error(`Failed to create WebSocket: ${err}`));
        return;
      }

      this.ws.onopen = () => {
        this._isConnected = true;
        this.reconnectAttempts = 0;
        this.emit('connected', { event: 'connected' });
        resolve();
      };

      this.ws.onclose = (ev) => {
        this._isConnected = false;
        this.emit('disconnected', { event: 'disconnected' });

        if (!this.intentionalClose && this.options.autoReconnect) {
          this.scheduleReconnect();
        }
      };

      this.ws.onerror = (ev) => {
        if (!this._isConnected) {
          reject(new Error('WebSocket connection failed. Is Fingerprint Bridge running?'));
        }
        this.emit('error', {
          event: 'error',
          errorCode: 'ws_error',
          errorMessage: 'WebSocket connection error',
        });
      };

      this.ws.onmessage = (ev) => {
        try {
          const data: BridgeEvent = JSON.parse(ev.data);
          this.emit(data.event, data);
        } catch (err) {
          console.error('[FingerprintBridge] Failed to parse message:', ev.data);
        }
      };
    });
  }

  /** Disconnect from the service */
  disconnect(): void {
    this.intentionalClose = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
    this._isConnected = false;
  }

  /** Whether the WebSocket is currently connected */
  get isConnected(): boolean {
    return this._isConnected && this.ws?.readyState === WebSocket.OPEN;
  }

  // ---------- Commands ----------

  /**
   * Start capturing fingerprints.
   * The reader will continuously capture fingerprints and emit
   * 'capture_completed' events until stopCapture() is called.
   *
   * @param format - Image format: 'raw' (default), 'png', or 'intermediate'
   * @param timeout - Timeout in ms per capture attempt, -1 for no timeout
   */
  startCapture(format: CaptureFormat = 'raw', timeout: number = -1): void {
    this.send({ command: 'start_capture', format, timeout });
  }

  /** Stop capturing fingerprints */
  stopCapture(): void {
    this.send({ command: 'stop_capture' });
  }

  /** Get current status of the bridge and reader */
  getStatus(): void {
    this.send({ command: 'get_status' });
  }

  /** Get list of connected fingerprint readers */
  getDevices(): void {
    this.send({ command: 'get_devices' });
  }

  /** Select a specific fingerprint reader by its device ID */
  selectDevice(deviceId: string): void {
    this.send({ command: 'select_device', deviceId });
  }

  // ---------- Async command wrappers ----------

  /** Get status and wait for the response */
  requestStatus(): Promise<StatusData> {
    return this.sendAndWait<StatusData>({ command: 'get_status' }, 'status');
  }

  /** Get devices and wait for the response */
  requestDevices(): Promise<DeviceInfo[]> {
    return this.sendAndWait<BridgeEvent>({ command: 'get_devices' }, 'device_list')
      .then(data => data.devices ?? []);
  }

  /**
   * Capture a single fingerprint and return the data.
   * Starts capture, waits for one result, then stops.
   */
  captureOnce(format: CaptureFormat = 'raw', timeoutMs: number = 30000): Promise<CaptureData> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        cleanup();
        this.stopCapture();
        reject(new Error('Capture timed out'));
      }, timeoutMs);

      const onCapture = (data: BridgeEvent) => {
        cleanup();
        this.stopCapture();
        resolve({
          imageData: data.imageData!,
          quality: data.quality!,
          imageWidth: data.imageWidth!,
          imageHeight: data.imageHeight!,
          imageResolution: data.imageResolution!,
        });
      };

      const onError = (data: BridgeEvent) => {
        cleanup();
        this.stopCapture();
        reject(new Error(data.errorMessage ?? 'Capture failed'));
      };

      const cleanup = () => {
        clearTimeout(timer);
        this.off('capture_completed', onCapture);
        this.off('capture_failed', onError);
      };

      this.on('capture_completed', onCapture);
      this.on('capture_failed', onError);
      this.startCapture(format);
    });
  }

  // ---------- Event System ----------

  /** Subscribe to an event */
  on(event: string, callback: EventCallback): this {
    if (!this.listeners.has(event)) {
      this.listeners.set(event, new Set());
    }
    this.listeners.get(event)!.add(callback);
    return this;
  }

  /** Unsubscribe from an event */
  off(event: string, callback: EventCallback): this {
    this.listeners.get(event)?.delete(callback);
    return this;
  }

  /** Subscribe to an event (fires only once) */
  once(event: string, callback: EventCallback): this {
    const wrapper: EventCallback = (data) => {
      this.off(event, wrapper);
      callback(data);
    };
    return this.on(event, wrapper);
  }

  // ---------- Utilities ----------

  /**
   * Convert raw grayscale base64 image data to an HTMLCanvasElement
   * for rendering in the browser.
   */
  static rawToCanvas(
    base64Raw: string,
    width: number,
    height: number
  ): HTMLCanvasElement {
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext('2d')!;

    const rawBytes = Uint8Array.from(atob(base64Raw), c => c.charCodeAt(0));
    const imageData = ctx.createImageData(width, height);

    for (let i = 0; i < rawBytes.length; i++) {
      const pixel = rawBytes[i];
      const offset = i * 4;
      imageData.data[offset] = pixel;     // R
      imageData.data[offset + 1] = pixel; // G
      imageData.data[offset + 2] = pixel; // B
      imageData.data[offset + 3] = 255;   // A
    }

    ctx.putImageData(imageData, 0, 0);
    return canvas;
  }

  /**
   * Convert raw grayscale base64 image data to a data URL
   * suitable for <img src="..."> or CSS background.
   */
  static rawToDataUrl(base64Raw: string, width: number, height: number): string {
    const canvas = FingerprintBridge.rawToCanvas(base64Raw, width, height);
    return canvas.toDataURL('image/png');
  }

  /**
   * Convert PNG base64 data to a data URL.
   * (When using format: 'png', the imageData is already PNG-encoded)
   */
  static pngToDataUrl(base64Png: string): string {
    return `data:image/png;base64,${base64Png}`;
  }

  /**
   * Check if the bridge service is reachable via HTTP health endpoint.
   * Does not require a WebSocket connection.
   */
  static async isServiceRunning(port: number = 27015): Promise<boolean> {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/health`, {
        signal: AbortSignal.timeout(2000),
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  // ---------- Private ----------

  private send(message: Record<string, unknown>): void {
    if (this.ws?.readyState !== WebSocket.OPEN) {
      throw new Error('Not connected to Fingerprint Bridge');
    }
    this.ws.send(JSON.stringify(message));
  }

  private sendAndWait<T>(
    message: Record<string, unknown>,
    responseEvent: string,
    timeoutMs: number = 5000
  ): Promise<T> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.off(responseEvent, handler);
        reject(new Error(`Timed out waiting for '${responseEvent}' response`));
      }, timeoutMs);

      const handler: EventCallback = (data) => {
        clearTimeout(timer);
        this.off(responseEvent, handler);
        resolve(data as unknown as T);
      };

      this.on(responseEvent, handler);
      this.send(message);
    });
  }

  private emit(event: string, data: BridgeEvent): void {
    // Emit specific event
    this.listeners.get(event)?.forEach(cb => {
      try { cb(data); }
      catch (err) { console.error(`[FingerprintBridge] Event handler error (${event}):`, err); }
    });

    // Emit wildcard '*' event for all messages
    this.listeners.get('*')?.forEach(cb => {
      try { cb(data); }
      catch (err) { console.error('[FingerprintBridge] Wildcard handler error:', err); }
    });
  }

  private scheduleReconnect(): void {
    if (this.reconnectAttempts >= this.options.maxReconnectAttempts) {
      this.emit('reconnect_failed', { event: 'reconnect_failed' });
      return;
    }

    this.reconnectAttempts++;
    const delay = Math.min(
      this.options.reconnectInterval * Math.pow(1.5, this.reconnectAttempts - 1),
      30000 // Max 30s between attempts
    );

    this.emit('reconnecting', {
      event: 'reconnecting',
      errorMessage: `Reconnecting in ${Math.round(delay / 1000)}s (attempt ${this.reconnectAttempts})`,
    });

    this.reconnectTimer = setTimeout(() => {
      this.connect().catch(() => {
        // Will trigger another reconnect via onclose
      });
    }, delay);
  }
}

// Default export for convenience
export default FingerprintBridge;
