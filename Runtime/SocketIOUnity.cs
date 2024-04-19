using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SocketIOClient
{
    public class SocketIOUnity : SocketIO
    {
        private bool _verbose;

        public SocketIOUnity(string uri) : base(uri)
        {
            this.Initialize();
        }

        public SocketIOUnity(string uri, bool verbose = false) : base(uri)
        {
            this.Initialize(verbose);
        }

        public SocketIOUnity(string uri, SocketIOOptions options) : base(uri, options)
        {
            this.Initialize();
        }

        public SocketIOUnity(string uri, SocketIOOptions options, bool verbose = false) : base(uri, options)
        {
            this.Initialize(verbose);
        }

        private void Initialize(bool verbose = false)
        {
            this._verbose = verbose;

            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] Initialize()");
            }
        }

        #region Methods
        public void Connect()
        {
            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] Connect()");
            }

            base.ConnectAsync().ContinueWith((_) => { });
        }

        public void Disconnect()
        {
            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] Disconnect()");
            }

            this.DisconnectAsync().ContinueWith(t => { });
        }

        public void Emit(string eventName)
        {
            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] Emit(${eventName})");
            }

            base.EmitAsync(eventName).ContinueWith(t => { });
        }

        public void Emit(string eventName, params object[] data)
        {
            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] Emit(${eventName}, {data.Count()})");
            }

            base.EmitAsync(eventName, data).ContinueWith(t => { });
        }

        public void EmitWithAck(string eventName, Action<SocketIOResponse> ack, params object[] data)
        {
            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] EmitWithAck(${eventName}, {data.Count()})");
            }

            base.EmitAsync(eventName, CancellationToken.None, ack, data).ContinueWith(t => { });
        }

        public void EmitWithAck(string eventName, CancellationToken cancellationToken, Action<SocketIOResponse> ack, params object[] data)
        {
            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] EmitWithAck(${eventName}, {data.Count()})");
            }

            base.EmitAsync(eventName, cancellationToken, ack, data).ContinueWith(t => { });
        }

        public Task<SocketIOResponse> EmitWithAckAsync(string eventName, params object[] data)
        {
            if (this._verbose)
            {
                Debug.Log($"[SocketIOUnity] EmitWithAckAsync(${eventName}, {data.Count()})");
            }

            var job = new TaskCompletionSource<SocketIOResponse>();
            base.EmitAsync(eventName, CancellationToken.None, (response) =>
            {
                job.SetResult(response);
            }, data).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    job.SetException(task.Exception);
                }
            });

            return job.Task;
        }

        public void SetVerbose(bool newValue)
        {
            this._verbose = newValue;
        }
        #endregion
    }
}
