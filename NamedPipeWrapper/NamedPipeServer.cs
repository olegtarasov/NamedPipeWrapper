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
        private readonly ConcurrentDictionary<NamedPipeServerStream, object> _servers = new ConcurrentDictionary<NamedPipeServerStream, object>();
        
        private NamedPipeServerStream _listener;
        private int _shouldListen;

        public NamedPipeServer(string pipeName)
        {
            PipeName = pipeName;
        }

        public string PipeName { get; private set; }
        
        public event EventHandler<ClientConnectedArgs> ClientConnected;

        public event EventHandler<ClientConnectedArgs> ClientDisconnected;

        public void StartListen()
        {
            Logger.Debug(nameof(StartListen));

            if (_servers.Count > 0)
            {
                Logger.Info("Already listening.");
                return;
            }

            Interlocked.Exchange(ref _shouldListen, 1);
            SpawnListener();
        }

        public void StopListen()
        {
            Logger.Debug(nameof(StopListen));

            if (_shouldListen == 0 || _listener == null)
                return;

            Interlocked.Exchange(ref _shouldListen, 0);

            _listener.Dispose();
            _listener = null;
        }

        public void StopServers()
        {
            Logger.Debug(nameof(StopServers));

            foreach (var server in _servers.Keys.ToList())
            {
                server.Dispose();
            }

            _servers.Clear();
        }

        public async Task SendToAllAsync(byte[] message)
        {
            Logger.Debug(nameof(SendToAllAsync));

            List<NamedPipeServerStream> dead = null;

            bool locked = false;
            foreach (var server in _servers.Keys.ToList())
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
                StopListen();
                StopServers();
            }
        }

        protected override void OnPipeDied(PipeStream stream)
        {
            base.OnPipeDied(stream);
            RemoveServer((NamedPipeServerStream)stream);
        }

        protected void OnClientConnected(NamedPipeServer server)
        {
            if (ClientConnected != null)
                ClientConnected(this, new ClientConnectedArgs(server));
        }

        protected void OnClientDisconnected(NamedPipeServer server)
        {
            if (ClientDisconnected != null)
                ClientDisconnected(this, new ClientConnectedArgs(server));
        }
        
        private void AddServer(NamedPipeServerStream server)
        {
            Logger.Debug(nameof(AddServer));

            bool locked = false;
            _servers.AddOrUpdate(server, new object(), (stream, o) => new object());
        }

        private void RemoveServer(NamedPipeServerStream server)
        {
            bool removed, locked = false;
            object dummy;

            removed = _servers.TryRemove(server, out dummy);
            
            if (removed)
                OnClientDisconnected(this);
        }

        

        private void SpawnListener()
        {
            ListenForConnection().HandleException(e => HandleStreamException(e, null));
        }

        private async Task ListenForConnection()
        {
            Logger.Debug(nameof(ListenForConnection));

            var server = GetServer();
            await server.WaitForConnectionAsync();

            AddServer(server);

            if (server.CanRead)
                ReadNextMessage(server);

            if (_shouldListen == 1)
                SpawnListener();

            OnClientConnected(this);
        }

        private NamedPipeServerStream GetServer()
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        }
    }
}