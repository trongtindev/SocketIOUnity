using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SocketIOClient.Extensions;

#if DEBUG
using System.Diagnostics;
#endif

namespace SocketIOClient.Transport.WebSockets
{
    public class WebSocketTransport : BaseTransport
    {
        public WebSocketTransport(TransportOptions options, IClientWebSocket ws) : base(options)
        {
            this._ws = ws;
            this._sendLock = new SemaphoreSlim(1, 1);
            this._listenCancellation = new CancellationTokenSource();
        }

        protected override TransportProtocol Protocol => TransportProtocol.WebSocket;

        readonly IClientWebSocket _ws;
        readonly SemaphoreSlim _sendLock;
        readonly CancellationTokenSource _listenCancellation;
        int _sendChunkSize = ChunkSize.Size8K;
        int _receiveChunkSize = ChunkSize.Size8K;
        bool _dirty;

        private async Task SendAsync(TransportMessageType type, byte[] bytes, CancellationToken cancellationToken)
        {
            if (type == TransportMessageType.Binary && this.Options.EIO == EngineIO.V3)
            {
                var buffer = new byte[bytes.Length + 1];
                buffer[0] = 4;
                Buffer.BlockCopy(bytes, 0, buffer, 1, bytes.Length);
                bytes = buffer;
            }
            var pages = (int)Math.Ceiling(bytes.Length * 1.0 / this._sendChunkSize);
            for (var i = 0; i < pages; i++)
            {
                var offset = i * this._sendChunkSize;
                var length = this._sendChunkSize;
                if (offset + length > bytes.Length)
                {
                    length = bytes.Length - offset;
                }
                var subBuffer = new byte[length];
                Buffer.BlockCopy(bytes, offset, subBuffer, 0, subBuffer.Length);
                var endOfMessage = pages - 1 == i;
                await this._ws.SendAsync(subBuffer, type, endOfMessage, cancellationToken).ConfigureAwait(false);
            }
        }

        private void Listen(CancellationToken cancellationToken)
        {
            Task.Factory.StartNew(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var binary = new byte[this._receiveChunkSize];
                    var count = 0;
                    WebSocketReceiveResult result = null;

                    while (this._ws.State == WebSocketState.Open)
                    {
                        try
                        {
                            result = await this._ws.ReceiveAsync(this._receiveChunkSize, cancellationToken).ConfigureAwait(false);

                            // resize
                            if (binary.Length - count < result.Count)
                            {
                                Array.Resize(ref binary, binary.Length + result.Count);
                            }
                            Buffer.BlockCopy(result.Buffer, 0, binary, count, result.Count);
                            count += result.Count;
                            if (result.EndOfMessage)
                            {
                                break;
                            }
                        }
                        catch (Exception e)
                        {
                            this.OnError.TryInvoke(e);
#if DEBUG
                            Debug.WriteLine($"[WebSocketTransport] Exception: {e.Message}");
#endif
                            return;
                        }
                    }

                    if (result == null)
                    {
                        return;
                    }

                    try
                    {
                        switch (result.MessageType)
                        {
                            case TransportMessageType.Text:
                                var text = Encoding.UTF8.GetString(binary, 0, count);
                                await this.OnTextReceived(text);
                                break;
                            case TransportMessageType.Binary:
                                byte[] bytes;
                                if (this.Options.EIO == EngineIO.V3)
                                {
                                    bytes = new byte[count - 1];
                                    Buffer.BlockCopy(binary, 1, bytes, 0, bytes.Length);
                                }
                                else
                                {
                                    bytes = new byte[count];
                                    Buffer.BlockCopy(binary, 0, bytes, 0, bytes.Length);
                                }
                                this.OnBinaryReceived(bytes);
                                break;
                            case TransportMessageType.Close:
                                this.OnError.TryInvoke(new TransportException("Received a Close message"));
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        this.OnError.TryInvoke(e);
#if DEBUG
                        Debug.WriteLine($"[WebSocketTransport] Exception: {e.Message}");
#endif
                        break;
                    }
                }
            }, cancellationToken);
        }

        public override async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (this._dirty)
                throw new InvalidOperationException(DirtyMessage);
            this._dirty = true;
            try
            {
                await this._ws.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new TransportException($"Could not connect to '{uri}'", e);
            }
            this.Listen(this._listenCancellation.Token);
        }

        public override async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            await this._ws.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async Task SendAsync(Payload payload, CancellationToken cancellationToken)
        {
            try
            {
                await this._sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(payload.Text))
                {
                    var bytes = Encoding.UTF8.GetBytes(payload.Text);
                    await this.SendAsync(TransportMessageType.Text, bytes, cancellationToken);
#if DEBUG
                    Debug.WriteLine($"[WebSocketTransport] SendAsync() {payload.Text}");
#endif
                }
                if (payload.Bytes != null)
                {
                    foreach (var item in payload.Bytes)
                    {
                        await this.SendAsync(TransportMessageType.Binary, item, cancellationToken).ConfigureAwait(false);
#if DEBUG
                        Debug.WriteLine($"[WebSocketTransport] SendAsync() {Convert.ToBase64String(item)}");
#endif
                    }
                }
            }
            finally
            {
                this._sendLock.Release();
            }
        }

        public async Task ChangeSendChunkSizeAsync(int size)
        {
            try
            {
                await this._sendLock.WaitAsync().ConfigureAwait(false);
                this._sendChunkSize = size;
            }
            finally
            {
                this._sendLock.Release();
            }
        }

        public async Task ChangeReceiveChunkSizeAsync(int size)
        {
            try
            {
                await this._sendLock.WaitAsync().ConfigureAwait(false);
                this._sendChunkSize = size;
            }
            finally
            {
                this._sendLock.Release();
            }
        }

        public override void AddHeader(string key, string val)
        {
            if (this._dirty)
            {
                throw new InvalidOperationException("Unable to add header after connecting");
            }
            this._ws.AddHeader(key, val);
        }

        public override void SetProxy(IWebProxy proxy)
        {
            if (this._dirty)
            {
                throw new InvalidOperationException("Unable to set proxy after connecting");
            }
            this._ws.SetProxy(proxy);
        }

        public override void Dispose()
        {
            base.Dispose();
            this._sendLock.Dispose();
        }
    }
}
