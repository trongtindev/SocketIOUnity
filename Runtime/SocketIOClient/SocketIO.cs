using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SocketIOClient.Extensions;
using SocketIOClient.JsonSerializer;
using SocketIOClient.Messages;
using SocketIOClient.Transport;
using SocketIOClient.Transport.Http;
using SocketIOClient.Transport.WebSockets;
using SocketIOClient.UriConverters;

#if DEBUG
using System.Diagnostics;
#endif

namespace SocketIOClient
{
    /// <summary>
    /// socket.io client class
    /// </summary>
    public class SocketIO : IDisposable
    {
        /// <summary>
        /// Create SocketIO object with default options
        /// </summary>
        /// <param name="uri"></param>
        public SocketIO(string uri) : this(new Uri(uri))
        {
        }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        public SocketIO(Uri uri) : this(uri, new SocketIOOptions())
        {
        }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="options"></param>
        public SocketIO(string uri, SocketIOOptions options) : this(new Uri(uri), options)
        {
        }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="options"></param>
        public SocketIO(Uri uri, SocketIOOptions options)
        {
            this.ServerUri = uri ?? throw new ArgumentNullException("uri");
            this.Options = options ?? throw new ArgumentNullException("options");
            this.Initialize();
        }

        Uri _serverUri;

        private Uri ServerUri
        {
            get => this._serverUri;
            set
            {
                if (this._serverUri != value)
                {
                    this._serverUri = value;
                    if (value != null && value.AbsolutePath != "/")
                    {
                        this.Namespace = value.AbsolutePath;
                    }
                }
            }
        }

        /// <summary>
        /// An unique identifier for the socket session. Set after the connect event is triggered, and updated after the reconnect event.
        /// </summary>
        public string Id { get; private set; }

        public string Namespace { get; private set; }

        /// <summary>
        /// Whether or not the socket is connected to the server.
        /// </summary>
        public bool Connected { get; private set; }

        int _attempts;

        public SocketIOOptions Options { get; }

        public IJsonSerializer JsonSerializer { get; set; }
        public ITransport Transport { get; set; }
        public IHttpClient HttpClient { get; set; }

        public Func<IClientWebSocket> ClientWebSocketProvider { get; set; }

        List<IDisposable> _resources = new();

        List<Type> _expectedExceptions;

        int _packetId;
        Exception _backgroundException;
        Dictionary<int, Action<SocketIOResponse>> _ackHandlers;
        List<OnAnyHandler> _onAnyHandlers;
        Dictionary<string, Action<SocketIOResponse>> _eventHandlers;
        double _reconnectionDelay;

        #region Socket.IO event

        public event EventHandler OnConnected;

        //public event EventHandler<string> OnConnectError;
        //public event EventHandler<string> OnConnectTimeout;
        public event EventHandler<string> OnError;
        public event EventHandler<string> OnDisconnected;

        /// <summary>
        /// Fired upon a successful reconnection.
        /// </summary>
        public event EventHandler<int> OnReconnected;

        /// <summary>
        /// Fired upon an attempt to reconnect.
        /// </summary>
        public event EventHandler<int> OnReconnectAttempt;

        /// <summary>
        /// Fired upon a reconnection attempt error.
        /// </summary>
        public event EventHandler<Exception> OnReconnectError;

        /// <summary>
        /// Fired when couldn’t reconnect within reconnectionAttempts
        /// </summary>
        public event EventHandler OnReconnectFailed;

        public event EventHandler OnPing;
        public event EventHandler<TimeSpan> OnPong;

        #endregion

        private void Initialize()
        {
            this._packetId = -1;
            this._ackHandlers = new Dictionary<int, Action<SocketIOResponse>>();
            this._eventHandlers = new Dictionary<string, Action<SocketIOResponse>>();
            this._onAnyHandlers = new List<OnAnyHandler>();

            this.JsonSerializer = new SystemTextJsonSerializer();

            this.HttpClient = new DefaultHttpClient();
            this.ClientWebSocketProvider = () => new DefaultClientWebSocket();
            this._expectedExceptions = new List<Type>
            {
                typeof(TimeoutException),
                typeof(WebSocketException),
                typeof(HttpRequestException),
                typeof(OperationCanceledException),
                typeof(TaskCanceledException),
                typeof(TransportException),
            };
        }

        private async Task InitTransportAsync()
        {
            this.Options.Transport = await this.GetProtocolAsync();
            var transportOptions = new TransportOptions
            {
                EIO = this.Options.EIO,
                Query = this.Options.Query,
                Auth = this.GetAuth(this.Options.Auth),
                ConnectionTimeout = this.Options.ConnectionTimeout
            };
            if (this.Options.Transport == TransportProtocol.Polling)
            {
                var handler = HttpPollingHandler.CreateHandler(transportOptions.EIO, this.HttpClient);
                this.Transport = new HttpTransport(transportOptions, handler);
            }
            else
            {
                var ws = this.ClientWebSocketProvider();
                if (ws is null)
                {
                    throw new ArgumentNullException(nameof(this.ClientWebSocketProvider),
                        $"{this.ClientWebSocketProvider} returns a null");
                }

                this._resources.Add(ws);
                this.Transport = new WebSocketTransport(transportOptions, ws);
                this.SetWebSocketHeaders();
            }

            this._resources.Add(this.Transport);
            this.Transport.Namespace = this.Namespace;
            if (this.Options.Proxy != null)
            {
                this.Transport.SetProxy(this.Options.Proxy);
            }
            this.Transport.OnReceived = this.OnMessageReceived;
            this.Transport.OnError = this.OnErrorReceived;
        }

        private string GetAuth(object auth)
        {
            if (auth == null)
                return string.Empty;
            var result = this.JsonSerializer.Serialize(new[] { auth });
            return result.Json.TrimStart('[').TrimEnd(']');
        }

        private void SetWebSocketHeaders()
        {
            if (this.Options.ExtraHeaders is null)
            {
                return;
            }

            foreach (var item in this.Options.ExtraHeaders)
            {
                this.Transport.AddHeader(item.Key, item.Value);
            }
        }

        private void SetHttpHeaders()
        {
            if (this.Options.ExtraHeaders is null)
            {
                return;
            }

            foreach (var header in this.Options.ExtraHeaders)
            {
                this.HttpClient.AddHeader(header.Key, header.Value);
            }
        }

        private void DisposeResources()
        {
            foreach (var item in this._resources)
            {
                item.TryDispose();
            }

            this._resources.Clear();
        }

        private void ConnectInBackground(CancellationToken cancellationToken)
        {
            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    this.DisposeResources();
                    await this.InitTransportAsync().ConfigureAwait(false);
                    var serverUri = UriConverter.GetServerUri(this.Options.Transport == TransportProtocol.WebSocket,
                        this.ServerUri, this.Options.EIO, this.Options.Path, this.Options.Query);
                    if (this._attempts > 0)
                        OnReconnectAttempt.TryInvoke(this, this._attempts);
                    try
                    {
                        using (var cts = new CancellationTokenSource(this.Options.ConnectionTimeout))
                        {
                            await this.Transport.ConnectAsync(serverUri, cts.Token).ConfigureAwait(false);
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        var needBreak = await this.AttemptAsync(e);
                        if (needBreak) break;

                        var canHandle = this.CanHandleException(e);
                        if (!canHandle) throw;
                    }
                }
            }, cancellationToken);
        }

        private async Task<bool> AttemptAsync(Exception e)
        {
            if (this._attempts > 0)
            {
                OnReconnectError.TryInvoke(this, e);
            }

            this._attempts++;
            if (this._attempts <= this.Options.ReconnectionAttempts)
            {
                if (this._reconnectionDelay < this.Options.ReconnectionDelayMax)
                {
                    this._reconnectionDelay += 2 * this.Options.RandomizationFactor;
                }

                if (this._reconnectionDelay > this.Options.ReconnectionDelayMax)
                {
                    this._reconnectionDelay = this.Options.ReconnectionDelayMax;
                }

                await Task.Delay((int)this._reconnectionDelay);
            }
            else
            {
                OnReconnectFailed.TryInvoke(this, EventArgs.Empty);
                return true;
            }

            return false;
        }

        private bool CanHandleException(Exception e)
        {
            if (this._expectedExceptions.Contains(e.GetType()))
            {
                if (!this.Options.Reconnection)
                {
                    this._backgroundException = e;
                    return false;
                }
            }
            else
            {
                this._backgroundException = e;
                return false;
            }

            return true;
        }

        private async Task<TransportProtocol> GetProtocolAsync()
        {
            this.SetHttpHeaders();
            if (this.Options.Transport == TransportProtocol.Polling && this.Options.AutoUpgrade)
            {
                var uri = UriConverter.GetServerUri(false, this.ServerUri, this.Options.EIO, this.Options.Path, this.Options.Query);
                try
                {
                    var text = await this.HttpClient.GetStringAsync(uri);
                    if (text.Contains("websocket"))
                    {
                        return TransportProtocol.WebSocket;
                    }
                }
                catch (Exception e)
                {
#if DEBUG
                    Debug.WriteLine($"[SocketIO] GetProtocolAsync() Exception: {e.Message}");
#endif
                }
            }

            return this.Options.Transport;
        }

        private readonly SemaphoreSlim _connectingLock = new(1, 1);
        private CancellationTokenSource _connCts;

        private void ConnectInBackground()
        {
            this._connCts.TryCancel();
            this._connCts.TryDispose();
            this._connCts = new CancellationTokenSource();
            this.ConnectInBackground(this._connCts.Token);
        }

        public async Task ConnectAsync()
        {
            await this._connectingLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (this.Connected) return;

                this.ConnectInBackground();

                var ms = 0;
                while (true)
                {
                    if (this._connCts.IsCancellationRequested)
                    {
                        break;
                    }

                    if (this._backgroundException != null)
                    {
                        throw new ConnectionException($"Cannot connect to server '{this.ServerUri}'", this._backgroundException);
                    }

                    ms += 100;
                    if (ms > this.Options.ConnectionTimeout.TotalMilliseconds)
                    {
                        throw new ConnectionException($"Cannot connect to server '{this.ServerUri}'",
                            new TimeoutException());
                    }

                    await Task.Delay(100);
                }
            }
            finally
            {
                this._connectingLock.Release();
            }
        }

        private void PingHandler()
        {
            OnPing.TryInvoke(this, EventArgs.Empty);
        }

        private void PongHandler(PongMessage msg)
        {
            OnPong.TryInvoke(this, msg.Duration);
        }

        private void ConnectedHandler(ConnectedMessage msg)
        {
            this.Id = msg.Sid;
            this.Connected = true;
            this._connCts.Cancel();
            OnConnected.TryInvoke(this, EventArgs.Empty);
            if (this._attempts > 0)
            {
                OnReconnected.TryInvoke(this, this._attempts);
            }

            this._attempts = 0;
        }

        private void DisconnectedHandler()
        {
            _ = this.InvokeDisconnect(DisconnectReason.IOServerDisconnect);
        }

        private void EventMessageHandler(EventMessage m)
        {
            var res = new SocketIOResponse(m.JsonElements, this)
            {
                PacketId = m.Id
            };
            foreach (var item in this._onAnyHandlers)
            {
                item.TryInvoke(m.Event, res);
            }

            if (this._eventHandlers.ContainsKey(m.Event))
            {
                this._eventHandlers[m.Event].TryInvoke(res);
            }
        }

        private void AckMessageHandler(ClientAckMessage m)
        {
            if (this._ackHandlers.ContainsKey(m.Id))
            {
                var res = new SocketIOResponse(m.JsonElements, this);
                this._ackHandlers[m.Id].TryInvoke(res);
                this._ackHandlers.Remove(m.Id);
            }
        }

        private void ErrorMessageHandler(ErrorMessage msg)
        {
            OnError.TryInvoke(this, msg.Message);
        }

        private void BinaryMessageHandler(BinaryMessage msg)
        {
            var response = new SocketIOResponse(msg.JsonElements, this)
            {
                PacketId = msg.Id,
            };
            response.InComingBytes.AddRange(msg.IncomingBytes);
            foreach (var item in this._onAnyHandlers)
            {
                item.TryInvoke(msg.Event, response);
            }

            if (this._eventHandlers.ContainsKey(msg.Event))
            {
                this._eventHandlers[msg.Event].TryInvoke(response);
            }
        }

        private void BinaryAckMessageHandler(ClientBinaryAckMessage msg)
        {
            if (this._ackHandlers.ContainsKey(msg.Id))
            {
                var response = new SocketIOResponse(msg.JsonElements, this)
                {
                    PacketId = msg.Id,
                };
                response.InComingBytes.AddRange(msg.IncomingBytes);
                this._ackHandlers[msg.Id].TryInvoke(response);
            }
        }

        private void OnErrorReceived(Exception ex)
        {
#if DEBUG
            Debug.WriteLine($"[SocketIO] OnErrorReceived() Exception: {ex.Message}");
#endif
            _ = this.InvokeDisconnect(DisconnectReason.TransportClose);
        }

        private void OnMessageReceived(IMessage msg)
        {
            try
            {
                switch (msg.Type)
                {
                    case MessageType.Ping:
                        this.PingHandler();
                        break;
                    case MessageType.Pong:
                        this.PongHandler(msg as PongMessage);
                        break;
                    case MessageType.Connected:
                        this.ConnectedHandler(msg as ConnectedMessage);
                        break;
                    case MessageType.Disconnected:
                        this.DisconnectedHandler();
                        break;
                    case MessageType.EventMessage:
                        this.EventMessageHandler(msg as EventMessage);
                        break;
                    case MessageType.AckMessage:
                        this.AckMessageHandler(msg as ClientAckMessage);
                        break;
                    case MessageType.ErrorMessage:
                        this.ErrorMessageHandler(msg as ErrorMessage);
                        break;
                    case MessageType.BinaryMessage:
                        this.BinaryMessageHandler(msg as BinaryMessage);
                        break;
                    case MessageType.BinaryAckMessage:
                        this.BinaryAckMessageHandler(msg as ClientBinaryAckMessage);
                        break;
                }
            }
            catch (Exception e)
            {
#if DEBUG
                Debug.WriteLine($"[SocketIO] OnMessageReceived() Exception: {e.Message}");
#endif
            }
        }

        public async Task DisconnectAsync()
        {
            this._connCts.TryCancel();
            this._connCts.TryDispose();
            var msg = new DisconnectedMessage
            {
                Namespace = this.Namespace
            };
            try
            {
                await this.Transport.SendAsync(msg, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
#if DEBUG
                Debug.WriteLine($"[SocketIO] DisconnectAsync() Exception: {e.Message}");
#endif
            }

            await this.InvokeDisconnect(DisconnectReason.IOClientDisconnect);
        }

        /// <summary>
        /// Register a new handler for the given event.
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="callback"></param>
        public void On(string eventName, Action<SocketIOResponse> callback)
        {
            if (this._eventHandlers.ContainsKey(eventName))
            {
                this._eventHandlers.Remove(eventName);
            }

            this._eventHandlers.Add(eventName, callback);
        }


        /// <summary>
        /// Unregister a new handler for the given event.
        /// </summary>
        /// <param name="eventName"></param>
        public void Off(string eventName)
        {
            if (this._eventHandlers.ContainsKey(eventName))
            {
                this._eventHandlers.Remove(eventName);
            }
        }

        public void OnAny(OnAnyHandler handler)
        {
            if (handler != null)
            {
                this._onAnyHandlers.Add(handler);
            }
        }

        public void PrependAny(OnAnyHandler handler)
        {
            if (handler != null)
            {
                this._onAnyHandlers.Insert(0, handler);
            }
        }

        public void OffAny(OnAnyHandler handler)
        {
            if (handler != null)
            {
                this._onAnyHandlers.Remove(handler);
            }
        }

        public OnAnyHandler[] ListenersAny() => this._onAnyHandlers.ToArray();

        internal async Task ClientAckAsync(int packetId, CancellationToken cancellationToken, params object[] data)
        {
            IMessage msg;
            if (data != null && data.Length > 0)
            {
                var result = this.JsonSerializer.Serialize(data);
                if (result.Bytes.Count > 0)
                {
                    msg = new ServerBinaryAckMessage
                    {
                        Id = packetId,
                        Namespace = this.Namespace,
                        Json = result.Json
                    };
                    msg.OutgoingBytes = new List<byte[]>(result.Bytes);
                }
                else
                {
                    msg = new ServerAckMessage
                    {
                        Namespace = this.Namespace,
                        Id = packetId,
                        Json = result.Json
                    };
                }
            }
            else
            {
                msg = new ServerAckMessage
                {
                    Namespace = this.Namespace,
                    Id = packetId
                };
            }

            await this.Transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Emits an event to the socket
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="data">Any other parameters can be included. All serializable datastructures are supported, including byte[]</param>
        /// <returns></returns>
        public async Task EmitAsync(string eventName, params object[] data)
        {
            await this.EmitAsync(eventName, CancellationToken.None, data).ConfigureAwait(false);
        }

        public async Task EmitAsync(string eventName, CancellationToken cancellationToken, params object[] data)
        {
            if (data != null && data.Length > 0)
            {
                var result = this.JsonSerializer.Serialize(data);
                if (result.Bytes.Count > 0)
                {
                    var msg = new BinaryMessage
                    {
                        Namespace = this.Namespace,
                        OutgoingBytes = new List<byte[]>(result.Bytes),
                        Event = eventName,
                        Json = result.Json
                    };
                    await this.Transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var msg = new EventMessage
                    {
                        Namespace = this.Namespace,
                        Event = eventName,
                        Json = result.Json
                    };
                    await this.Transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var msg = new EventMessage
                {
                    Namespace = this.Namespace,
                    Event = eventName
                };
                await this.Transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Emits an event to the socket
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="ack">will be called with the server answer.</param>
        /// <param name="data">Any other parameters can be included. All serializable datastructures are supported, including byte[]</param>
        /// <returns></returns>
        public async Task EmitAsync(string eventName, Action<SocketIOResponse> ack, params object[] data)
        {
            await this.EmitAsync(eventName, CancellationToken.None, ack, data).ConfigureAwait(false);
        }

        public async Task EmitAsync(string eventName,
            CancellationToken cancellationToken,
            Action<SocketIOResponse> ack,
            params object[] data)
        {
            this._ackHandlers.Add(++this._packetId, ack);
            if (data != null && data.Length > 0)
            {
                var result = this.JsonSerializer.Serialize(data);
                if (result.Bytes.Count > 0)
                {
                    var msg = new ClientBinaryAckMessage
                    {
                        Event = eventName,
                        Namespace = this.Namespace,
                        Json = result.Json,
                        Id = this._packetId,
                        OutgoingBytes = new List<byte[]>(result.Bytes)
                    };
                    await this.Transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var msg = new ClientAckMessage
                    {
                        Event = eventName,
                        Namespace = this.Namespace,
                        Id = this._packetId,
                        Json = result.Json
                    };
                    await this.Transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var msg = new ClientAckMessage
                {
                    Event = eventName,
                    Namespace = this.Namespace,
                    Id = this._packetId
                };
                await this.Transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task InvokeDisconnect(string reason)
        {
            if (this.Connected)
            {
                this.Connected = false;
                this.Id = null;
                OnDisconnected.TryInvoke(this, reason);
                try
                {
                    await this.Transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
#if DEBUG
                    Debug.WriteLine($"[SocketIO] InvokeDisconnect() Exception: {e.Message}");
#endif
                }

                if (reason != DisconnectReason.IOServerDisconnect && reason != DisconnectReason.IOClientDisconnect)
                {
                    //In the this cases (explicit disconnection), the client will not try to reconnect and you need to manually call socket.connect().
                    if (this.Options.Reconnection)
                    {
                        this.ConnectInBackground();
                    }
                }
            }
        }

        public void AddExpectedException(Type type)
        {
            if (!this._expectedExceptions.Contains(type))
            {
                this._expectedExceptions.Add(type);
            }
        }

        public void Dispose()
        {
            this._connCts.TryCancel();
            this._connCts.TryDispose();
            this.Transport.TryDispose();
            this._ackHandlers.Clear();
            this._onAnyHandlers.Clear();
            this._eventHandlers.Clear();
        }
    }
}