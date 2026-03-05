using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FingerprintBridge
{
    public class WebSocketServer
    {
        private readonly HttpListener _listener;
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private CancellationToken _ct;
        private readonly JsonSerializerOptions _jsonOptions;

        public int Port { get; }
        public int ClientCount => _clients.Count;

        /// <summary>
        /// Fired when a command is received from any connected client.
        /// </summary>
        public event Func<Protocol.InboundMessage, Task>? OnCommandReceived;

        /// <summary>
        /// Fired when a client connects or disconnects.
        /// </summary>
        public event Action<int>? OnClientCountChanged;

        public WebSocketServer(int port = 27015)
        {
            Port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        public void Start(CancellationToken ct)
        {
            _ct = ct;
            _listener.Start();
            Logger.Info($"WebSocket server listening on ws://127.0.0.1:{Port}");

            // Accept connections on a background thread
            Task.Run(() => AcceptConnectionsAsync(ct), ct);
        }

        public void Stop()
        {
            // Close all client connections
            foreach (var (id, ws) in _clients)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None)
                            .Wait(TimeSpan.FromSeconds(2));
                    }
                }
                catch { }
            }
            _clients.Clear();

            try { _listener.Stop(); } catch { }
            Logger.Info("WebSocket server stopped");
        }

        private async Task AcceptConnectionsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();

                    // Allow CORS preflight
                    if (context.Request.HttpMethod == "OPTIONS")
                    {
                        context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                        context.Response.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
                        context.Response.AddHeader("Access-Control-Allow-Headers", "*");
                        context.Response.StatusCode = 204;
                        context.Response.Close();
                        continue;
                    }

                    // Health check endpoint
                    if (!context.Request.IsWebSocketRequest)
                    {
                        if (context.Request.Url?.AbsolutePath == "/health")
                        {
                            var healthJson = Encoding.UTF8.GetBytes(
                                JsonSerializer.Serialize(new { status = "ok", clients = ClientCount }, _jsonOptions)
                            );
                            context.Response.ContentType = "application/json";
                            context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                            context.Response.StatusCode = 200;
                            context.Response.OutputStream.Write(healthJson, 0, healthJson.Length);
                            context.Response.Close();
                            continue;
                        }

                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        continue;
                    }

                    _ = HandleClientAsync(context, ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    break; // Expected when stopping
                }
                catch (ObjectDisposedException)
                {
                    break; // Listener was disposed
                }
                catch (Exception ex)
                {
                    Logger.Error($"Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(HttpListenerContext context, CancellationToken ct)
        {
            WebSocket? ws = null;
            var clientId = Guid.NewGuid().ToString("N")[..8];

            try
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                ws = wsContext.WebSocket;
                _clients[clientId] = ws;

                Logger.Info($"Client connected: {clientId} (total: {ClientCount})");
                OnClientCountChanged?.Invoke(ClientCount);

                var buffer = new byte[8192];

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Client disconnected",
                            CancellationToken.None
                        );
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Logger.Debug($"Received from {clientId}: {json}");

                        try
                        {
                            var message = JsonSerializer.Deserialize<Protocol.InboundMessage>(json, _jsonOptions);
                            if (message != null && OnCommandReceived != null)
                            {
                                await OnCommandReceived.Invoke(message);
                            }
                        }
                        catch (JsonException ex)
                        {
                            Logger.Error($"Invalid JSON from {clientId}: {ex.Message}");
                            await SendAsync(ws, new Protocol.OutboundMessage
                            {
                                Event = "error",
                                ErrorCode = "invalid_json",
                                ErrorMessage = "Could not parse JSON command"
                            });
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                Logger.Debug($"Client {clientId} WebSocket error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Client {clientId} error: {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                Logger.Info($"Client disconnected: {clientId} (total: {ClientCount})");
                OnClientCountChanged?.Invoke(ClientCount);

                if (ws != null)
                {
                    try { ws.Dispose(); } catch { }
                }
            }
        }

        public async Task BroadcastAsync(object payload)
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            var segment = new ArraySegment<byte>(bytes);

            // Serialize all sends — WebSocket.SendAsync is not thread-safe
            await _sendLock.WaitAsync(_ct).ConfigureAwait(false);
            try
            {
                var deadClients = new System.Collections.Generic.List<string>();

                foreach (var (id, ws) in _clients)
                {
                    if (ws.State != WebSocketState.Open)
                    {
                        deadClients.Add(id);
                        continue;
                    }

                    try
                    {
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, _ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        deadClients.Add(id);
                    }
                }

                foreach (var id in deadClients)
                {
                    _clients.TryRemove(id, out var deadWs);
                    try { deadWs?.Dispose(); } catch { }
                }

                if (deadClients.Count > 0)
                {
                    OnClientCountChanged?.Invoke(ClientCount);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendAsync(WebSocket ws, object payload)
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _ct
            );
        }
    }
}
