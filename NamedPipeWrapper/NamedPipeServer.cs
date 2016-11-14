using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Serialization.Formatters;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using Newtonsoft.Json;

namespace NamedPipeWrapper
{
    public class NamedPipeServer : NamedPipeBase
    {
        private readonly ConcurrentDictionary<NamedPipeServerStream, object> _streams = new ConcurrentDictionary<NamedPipeServerStream, object>();
        
        private int _shouldListen;

        public NamedPipeServer(string pipeName)
        {
            PipeName = pipeName;
        }

        public string PipeName { get; }
        
        public event EventHandler<ClientConnectedArgs> ClientConnected;

        public event EventHandler<ClientConnectedArgs> ClientDisconnected;

        public void StartListen()
        {
            if (_streams.Count > 0)
            {
                Logger.Info("Already listening.");
                return;
            }

            Interlocked.Exchange(ref _shouldListen, 1);
            ListenForConnectionsAsync().HandleException(ex => {});
        }

        public Task SendMessageToAllAsync<T>(T message)
        {
            CheckDisposed();

            string json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);

            Logger.Info($"Sending message to all clients: {json}");

            return SendToAllAsync(buffer);
        }

        public async Task SendToAllAsync(byte[] message)
        {
            CheckDisposed();

            foreach (var server in _streams.Keys.ToList())
            {
                if (!server.CanWrite)
                    continue;

                await SendAsync(server, message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                foreach (var stream in _streams.Keys.ToList())
                {
                    stream.Dispose();
                }

                _streams.Clear();
            }
        }

        protected override void OnPipeDied(PipeStream stream, bool isCancelled)
        {
            base.OnPipeDied(stream, isCancelled);
            RemoveServer((NamedPipeServerStream)stream);
        }

        private void OnClientConnected(NamedPipeServer server)
        {
            ClientConnected?.Invoke(this, new ClientConnectedArgs(server));
        }

        private void OnClientDisconnected(NamedPipeServer server)
        {
            ClientDisconnected?.Invoke(this, new ClientConnectedArgs(server));
        }
        
        private void AddServer(NamedPipeServerStream server)
        {
            _streams.AddOrUpdate(server, new object(), (stream, o) => new object());
        }

        private void RemoveServer(NamedPipeServerStream server)
        {
            object dummy;

            if (_streams.TryRemove(server, out dummy))
            {
                OnClientDisconnected(this);
            }
        }

        private async Task ListenForConnectionsAsync()
        {
            CheckDisposed();

            Logger.Info($"Starting to listen to connections on pipe {PipeName}");

            while (!CancellationToken.Token.IsCancellationRequested)
            {
                var server = GetServer();

                try
                {
                    await server.WaitForConnectionAsync(CancellationToken.Token);
                }
                catch (OperationCanceledException)
                {
                    OnPipeDied(server, true);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Unhandled stream exception: {ex.Message}. Disposing the pipe.", ex);
                    Dispose();
                    return;
                }

                AddServer(server);

                if (server.CanRead)
                    ReadMessagesAsync(server, CancellationToken.Token).HandleException(ex => { });

                OnClientConnected(this);

                if (_shouldListen == 0)
                    break;
            }
        }

        private NamedPipeServerStream GetServer()
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        }
    }
}