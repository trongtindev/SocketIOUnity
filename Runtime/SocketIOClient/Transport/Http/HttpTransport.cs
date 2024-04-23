using SocketIOClient.Extensions;
using SocketIOClient.Messages;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SocketIOClient.Transport.Http
{
    public class HttpTransport : BaseTransport
    {
        public HttpTransport(TransportOptions options, IHttpPollingHandler pollingHandler) : base(options)
        {
            this._pollingHandler = pollingHandler ?? throw new ArgumentNullException(nameof(pollingHandler));
            this._pollingHandler.OnTextReceived = this.OnTextReceived;
            this._pollingHandler.OnBytesReceived = this.OnBinaryReceived;
            this._sendLock = new SemaphoreSlim(1, 1);
        }

        bool _dirty;
        string _httpUri;
        readonly SemaphoreSlim _sendLock;
        CancellationTokenSource _pollingTokenSource;

        private readonly IHttpPollingHandler _pollingHandler;

        protected override TransportProtocol Protocol => TransportProtocol.Polling;

        private void StartPolling(CancellationToken cancellationToken)
        {
            Task.Factory.StartNew(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // if (!_httpUri.Contains("&sid="))
                    // {
                    //     await Task.Delay(20, cancellationToken);
                    //     continue;
                    // }
                    try
                    {
                        await this._pollingHandler.GetAsync(this._httpUri, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        this.OnError(e);
                        break;
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public override async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (this._dirty)
                throw new InvalidOperationException(DirtyMessage);
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            this._httpUri = uri.ToString();

            try
            {
                await this._pollingHandler.SendAsync(req, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new TransportException($"Could not connect to '{uri}'", e);
            }

            this._dirty = true;
        }

        public override Task DisconnectAsync(CancellationToken cancellationToken)
        {
            this._pollingTokenSource.Cancel();
            if (this.PingTokenSource != null)
            {
                this.PingTokenSource.Cancel();
            }
            return Task.CompletedTask;
        }

        public override void AddHeader(string key, string val)
        {
            this._pollingHandler.AddHeader(key, val);
        }

        public override void SetProxy(IWebProxy proxy)
        {
            if (this._dirty)
            {
                throw new InvalidOperationException("Unable to set proxy after connecting");
            }
            this._pollingHandler.SetProxy(proxy);
        }

        public override void Dispose()
        {
            base.Dispose();
            this._pollingTokenSource.TryCancel();
            this._pollingTokenSource.TryDispose();
        }

        public override async Task SendAsync(Payload payload, CancellationToken cancellationToken)
        {
            try
            {
                await this._sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(payload.Text))
                {
                    await this._pollingHandler.PostAsync(this._httpUri, payload.Text, cancellationToken);
#if DEBUG
                    Debug.WriteLine($"[HttpTransport] SendAsync() {payload.Text}");
#endif
                }
                if (payload.Bytes != null && payload.Bytes.Count > 0)
                {
                    await this._pollingHandler.PostAsync(this._httpUri, payload.Bytes, cancellationToken);
#if DEBUG
                    Debug.WriteLine($"[HttpTransport] SendAsync() {payload.Bytes.Count} bytes");
#endif
                }
            }
            finally
            {
                this._sendLock.Release();
            }
        }

        protected override async Task OpenAsync(OpenedMessage msg)
        {
            this._httpUri += "&sid=" + msg.Sid;
            this._pollingTokenSource = new CancellationTokenSource();
            this.StartPolling(this._pollingTokenSource.Token);
            await base.OpenAsync(msg);
        }
    }
}