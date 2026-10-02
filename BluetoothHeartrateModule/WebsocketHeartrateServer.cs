using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;

namespace BluetoothHeartrateModule
{
    internal class WebsocketHeartrateServer
    {
        private readonly BluetoothHeartrateModule _module;
        private readonly AsyncHelper _ah;
        private static readonly ConcurrentDictionary<Guid, WebSocket> ConnectedClients = new();
        private static CancellationTokenSource? _serverCancellation;
        private static HttpListener? _httpListener;

        public WebsocketHeartrateServer(BluetoothHeartrateModule module)
        {
            _module = module;
            _ah = module.Ah;
        }

        internal async Task Start()
        {
            _module.LogDebug("Starting WebSocket server");

            _module.LogDebug("Clearing connected clients");
            ConnectedClients.Clear();
            _module.LogDebug("Resetting server cancellation token");
            ResetServerCancellation();

            if (!_module.GetWebocketEnabledSetting())
            {
                _module.LogDebug("WebSocket server is disabled, start aborted");
                return;
            }
            _module.LogDebug("Creating new cancellation token");
            _serverCancellation = new CancellationTokenSource();

            _module.LogDebug("Determining listen addresses");
            var httpAddress = GetListenAddress("http");
            var wsAddress = GetListenAddress("ws");
            if (_httpListener == null)
            {
                _module.LogDebug("Creating HTTP listener");
                var newListener = new HttpListener();
                try
                {
                    newListener.Prefixes.Add(httpAddress);
                }
                catch (ArgumentException ex)
                {
                    // Host or port settings are free-form text and may not form a valid prefix
                    _module.Log($"Invalid WebSocket server address {httpAddress}: {ex.Message}");
                    return;
                }
                _httpListener = newListener;
            }
            if (!_httpListener.IsListening)
            {
                _module.Log($"Starting Websocket server on {httpAddress}");
                try
                {
                    _httpListener.Start();
                }
                catch (HttpListenerException ex)
                {
                    _module.Log($"Failed to start WebSocket server: {ex.Message}");
                    return;
                }
            }

            _module.Log($"Websocket server is running, connnect using {wsAddress}");

            try
            {
                _ = HandleServerCommands();
                while (true)
                {
                    var context = await _httpListener.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        _module.LogDebug("Accepting WebSocket request");
                        try
                        {
                            var webSocketContext = await context.AcceptWebSocketAsync(null);
                            var clientId = Guid.NewGuid();
                            _module.LogDebug("Storing connected client");
                            ConnectedClients.TryAdd(clientId, webSocketContext.WebSocket);
                            _module.Log($"Websocket client {clientId} connected.");

                            _ = HandleWebSocketConnection(clientId, webSocketContext.WebSocket);
                        }
                        catch (Exception ex) when (ex is WebSocketException || ex is HttpListenerException || ex is InvalidOperationException)
                        {
                            // A single failed handshake must not take the whole server down
                            _module.Log($"Failed to accept WebSocket client: {ex.Message}");
                            try { context.Response.Abort(); } catch { }
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
            }
            catch (Exception ex) when (ex is HttpListenerException || ex is OperationCanceledException || ex is ObjectDisposedException)
            {
                // Ignore exceptions caused by stopping the server
                _module.LogDebug("Caught exception while stopping WebSocket server");
            }
            finally
            {
                StopHttpListener();
                ResetServerCancellation();
                _module.Log("Websocket server stopped");
            }
        }

        private void ResetServerCancellation()
        {
            if (_serverCancellation != null)
            {
                _module.LogDebug("Resetting server cancellation");
                _serverCancellation.Cancel();
                _serverCancellation.Dispose();
                _serverCancellation = null;
            }
        }

        private void StopHttpListener()
        {
            if (_httpListener?.IsListening == true)
            {
                _module.LogDebug("Stopping HTTP listener");
                _httpListener.Stop();
            }
            // Drop the listener so a restart picks up changed host/port settings
            _httpListener?.Close();
            _httpListener = null;
        }

        // Public method to send an int message to all clients
        internal async Task SendIntMessage(int message)
        {
            _module.LogDebug($"Sending message {message} to all clients");
            var messageBuffer = new ArraySegment<byte>(Converter.GetAsciiStringInt(message));
            foreach (var clientId in ConnectedClients.Keys)
            {
                await SendMessage(clientId, messageBuffer);
            }
        }
        // Public method to send an int message to specific client
        internal async Task SendIntMessage(int message, Guid clientId)
        {
            _module.LogDebug($"Sending message {message} to client {clientId}");
            var messageBuffer = new ArraySegment<byte>(Converter.GetAsciiStringInt(message));
            await SendMessage(clientId, messageBuffer);
        }

        // Public method to stop the server
        internal void Stop()
        {
            _module.Log($"WebSocket server stopping");
            ResetServerCancellation();
            StopHttpListener();
        }

        private string GetListenAddress(string protocol)
        {
            return $"{protocol}://{_module.GetWebocketHostSetting()}:{_module.GetWebocketPortSetting()}/";
        }
        private async Task HandleWebSocketConnection(Guid clientId, WebSocket webSocket)
        {
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    // Wait for incoming connections
                    await Task.Delay(-1);
                }
            }
            catch (WebSocketException ex)
            {
                _module.Log($"WebSocket error for Client {clientId}: {ex}");
            }
            finally
            {
                DisconnectClient(clientId);
            }
        }

        private async void DisconnectClient(Guid clientId)
        {
            _module.LogDebug($"Disconnecting client {clientId}");
            ConnectedClients.TryRemove(clientId, out WebSocket? removedClient);
            if (removedClient != null)
            {
                // async void: an exception escaping here (e.g. closing an already aborted socket) would crash the host
                try
                {
                    await _ah.WaitAsyncVoid(removedClient.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None), AsyncTask.CloseWebsocketConnection);
                }
                catch (Exception ex)
                {
                    _module.LogDebug($"Failed to close client {clientId} cleanly: {ex.Message}");
                }
                finally
                {
                    removedClient.Dispose();
                }
            }
            _module.Log($"WebSocket client {clientId} disconnected.");
        }

        // Public method to send a byte message to a specific client
        private async Task SendMessage(Guid clientId, ArraySegment<byte> buffer)
        {
            if (!_module.GetWebocketEnabledSetting())
            {
                _module.LogDebug("WebSocket server found to be disbled while sending message, stopping");
                Stop();
                return;
            }

            _module.LogDebug($"Trying to find client {clientId} for messaging");
            if (ConnectedClients.TryGetValue(clientId, out WebSocket? webSocket))
            {
                if (webSocket == null || webSocket.State != WebSocketState.Open)
                {
                    _module.LogDebug($"Cound not find open socket for client {clientId}, skipping");
                    return;
                }

                try
                {
                    _module.LogDebug($"Sending message to client {clientId}");
                    await webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    if (ex.InnerException is HttpListenerException iex)
                    {
                        // System.Net.HttpListenerException (1229): An operation was attempted on a nonexistent network connection.
                        if (iex.ErrorCode == 1229)
                        {
                            DisconnectClient(clientId);
                            return;
                        }
                    }
                    _module.Log($"WebSocket send error for Client {clientId}: {ex}");
                }
            }
            else
            {
                _module.Log($"WebSocket client {clientId} not found");
            }
        }

        private async Task HandleServerCommands()
        {
            if (_serverCancellation == null) { return; }

            try
            {
                await Task.Delay(-1, _serverCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Stop();
        }
    }
}

