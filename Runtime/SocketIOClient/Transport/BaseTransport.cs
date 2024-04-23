using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketIOClient.Extensions;
using SocketIOClient.Messages;
using System.Linq;


#if DEBUG
using System.Diagnostics;
#endif

namespace SocketIOClient.Transport
{
    public abstract class BaseTransport : ITransport
    {
        protected BaseTransport(TransportOptions options)
        {
            this.Options = options ?? throw new ArgumentNullException(nameof(options));
            this._messageQueue = new Queue<IMessage>();
        }

        protected const string DirtyMessage = "Invalid object's current state, may need to create a new object.";

        DateTime _pingTime;
        readonly Queue<IMessage> _messageQueue;
        protected TransportOptions Options { get; }

        public Action<IMessage> OnReceived { get; set; }

        protected abstract TransportProtocol Protocol { get; }
        protected CancellationTokenSource PingTokenSource { get; private set; }
        protected OpenedMessage OpenedMessage { get; private set; }

        public string Namespace { get; set; }
        public Action<Exception> OnError { get; set; }

        public async Task SendAsync(IMessage msg, CancellationToken cancellationToken)
        {
            msg.EIO = this.Options.EIO;
            msg.Protocol = this.Protocol;
            var payload = new Payload
            {
                Text = msg.Write()
            };
            if (msg.OutgoingBytes != null)
            {
                payload.Bytes = msg.OutgoingBytes;
            }
            await this.SendAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        protected virtual async Task OpenAsync(OpenedMessage msg)
        {
            this.OpenedMessage = msg;
            if (this.Options.EIO == EngineIO.V3 && string.IsNullOrEmpty(this.Namespace))
            {
                return;
            }
            var connectMsg = new ConnectedMessage
            {
                Namespace = this.Namespace,
                EIO = this.Options.EIO,
                Query = this.Options.Query,
            };
            if (this.Options.EIO == EngineIO.V4)
            {
                connectMsg.AuthJsonStr = this.Options.Auth;
            }

            for (var i = 1; i <= 3; i++)
            {
                try
                {
                    await this.SendAsync(connectMsg, CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                catch (Exception e)
                {
                    if (i == 3)
                        this.OnError.TryInvoke(e);
                    else
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Pow(2, i) * 100));
                }
            }
        }

        private void StartPing(CancellationToken cancellationToken)
        {
            // _logger.LogDebug($"[Ping] Interval: {OpenedMessage.PingInterval}");
            Task.Factory.StartNew(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(this.OpenedMessage.PingInterval, cancellationToken);
                    try
                    {
                        var ping = new PingMessage();
                        // _logger.LogDebug($"[Ping] Sending");
                        using (var cts = new CancellationTokenSource(this.OpenedMessage.PingTimeout))
                        {
                            await this.SendAsync(ping, cts.Token).ConfigureAwait(false);
                        }
                        // _logger.LogDebug($"[Ping] Has been sent");
                        this._pingTime = DateTime.Now;
                        this.OnReceived.TryInvoke(ping);
                    }
                    catch (Exception e)
                    {
                        // _logger.LogDebug($"[Ping] Failed to send, {e.Message}");
                        this.OnError.TryInvoke(e);
                        break;
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        public abstract Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

        public abstract Task DisconnectAsync(CancellationToken cancellationToken);

        public abstract void AddHeader(string key, string val);
        public abstract void SetProxy(IWebProxy proxy);

        public virtual void Dispose()
        {
            this._messageQueue.Clear();
            if (this.PingTokenSource != null)
            {
                this.PingTokenSource.Cancel();
                this.PingTokenSource.Dispose();
            }
        }

        public abstract Task SendAsync(Payload payload, CancellationToken cancellationToken);

        protected async Task OnTextReceived(string text)
        {
            // TODO: refactor
#if DEBUG
            Debug.WriteLine($"[BaseTransport] OnTextReceived() {text}");
#endif
            var msg = MessageFactory.CreateMessage(this.Options.EIO, text);
            if (msg == null)
            {
                return;
            }
            msg.Protocol = this.Protocol;
            if (msg.BinaryCount > 0)
            {
                msg.IncomingBytes = new List<byte[]>(msg.BinaryCount);
                this._messageQueue.Enqueue(msg);
                return;
            }
            if (msg.Type == MessageType.Opened)
            {
                await this.OpenAsync(msg as OpenedMessage).ConfigureAwait(false);
            }

            if (this.Options.EIO == EngineIO.V3)
            {
                if (msg.Type == MessageType.Connected)
                {
                    var ms = 0;
                    while (this.OpenedMessage is null)
                    {
                        await Task.Delay(10);
                        ms += 10;
                        if (ms > this.Options.ConnectionTimeout.TotalMilliseconds)
                        {
                            this.OnError.TryInvoke(new TimeoutException());
                            return;
                        }
                    }

                    var connectMsg = msg as ConnectedMessage;
                    connectMsg.Sid = this.OpenedMessage.Sid;
                    if ((string.IsNullOrEmpty(this.Namespace) && string.IsNullOrEmpty(connectMsg.Namespace)) || connectMsg.Namespace == this.Namespace)
                    {
                        if (this.PingTokenSource != null)
                        {
                            this.PingTokenSource.Cancel();
                        }
                        this.PingTokenSource = new CancellationTokenSource();
                        this.StartPing(this.PingTokenSource.Token);
                    }
                    else
                    {
                        return;
                    }
                }
                else if (msg.Type == MessageType.Pong)
                {
                    var pong = msg as PongMessage;
                    pong.Duration = DateTime.Now - this._pingTime;
                }
            }

            this.OnReceived.TryInvoke(msg);

            if (msg.Type == MessageType.Ping)
            {
                this._pingTime = DateTime.Now;
                try
                {
                    await this.SendAsync(new PongMessage(), CancellationToken.None).ConfigureAwait(false);
                    this.OnReceived.TryInvoke(new PongMessage
                    {
                        EIO = this.Options.EIO,
                        Protocol = this.Protocol,
                        Duration = DateTime.Now - this._pingTime
                    });
                }
                catch (Exception e)
                {
                    this.OnError.TryInvoke(e);
                }
            }
        }

        protected void OnBinaryReceived(byte[] bytes)
        {
#if DEBUG
            Debug.WriteLine($"[BaseTransport] OnBinaryReceived() {bytes.Count()}");
#endif
            if (this._messageQueue.Count > 0)
            {
                var msg = this._messageQueue.Peek();
                msg.IncomingBytes.Add(bytes);
                if (msg.IncomingBytes.Count == msg.BinaryCount)
                {
                    this.OnReceived.TryInvoke(msg);
                    this._messageQueue.Dequeue();
                }
            }
        }
    }
}