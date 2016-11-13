using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using Common.Logging;

// ReSharper disable ImpureMethodCallOnReadonlyValueField

namespace NamedPipeWrapper
{
    public class NamedPipeMessage
    {
        private readonly NamedPipeServerStream _pipeServer;
        private readonly NamedPipeServer _server;

        public NamedPipeMessage(NamedPipeServerStream pipeServer, NamedPipeServer server, byte[] message)
        {
            this._pipeServer = pipeServer;
            this._server = server;
            Message = message;
        }

        public byte[] Message { get; private set; }

        public void Respond(byte[] response)
        {
            _server.Write(_pipeServer, response);
        }
    }

    public class NamedPipeMessageArgs : EventArgs
    {
        public NamedPipeMessageArgs(NamedPipeMessage message)
        {
            Message = message;
        }

        public NamedPipeMessage Message { get; set; }
    }

    public class ClientConnectedArgs : EventArgs
    {
        public ClientConnectedArgs(NamedPipeServer server)
        {
            Server = server;
        }

        public NamedPipeServer Server { get; set; }
    }

    public class NamedPipeServer : IDisposable
    {
        private static readonly ILog _logger = LogManager.GetLogger<NamedPipeServer>();


        private readonly ConcurrentDictionary<NamedPipeServerStream, object> _servers = new ConcurrentDictionary<NamedPipeServerStream, object>();
        //private readonly Dictionary<>
        private readonly SpinLock _spinLock = new SpinLock(false);

        private NamedPipeServerStream _listener;
        private int _shouldListen;

        public NamedPipeServer(string pipeName)
        {
            PipeName = pipeName;
        }

        public string PipeName { get; private set; }

        public event EventHandler<NamedPipeMessageArgs> MessageReceived;

        public event EventHandler<ClientConnectedArgs> ClientConnected;

        public event EventHandler<ClientConnectedArgs> ClientDisconnected;

        public void StartListen()
        {
            _logger.Debug(nameof(StartListen));

            if (_servers.Count > 0)
            {
                _logger.Info("Already listening.");
                return;
            }

            Interlocked.Exchange(ref _shouldListen, 1);
            SpawnListener();
        }

        public void StopListen()
        {
            _logger.Debug(nameof(StopListen));

            if (_shouldListen == 0 || _listener == null)
                return;

            Interlocked.Exchange(ref _shouldListen, 0);

            _listener.Dispose();
            _listener = null;
        }

        public void StopServers()
        {
            _logger.Debug(nameof(StopServers));

            foreach (var server in _servers.Keys.ToList())
            {
                server.Dispose();
            }

            _servers.Clear();
        }

        public void WriteToAll(byte[] message)
        {
            _logger.Debug(nameof(WriteToAll));

            List<NamedPipeServerStream> dead = null;

            bool locked = false;
            _spinLock.Enter(ref locked);
            foreach (var server in _servers.Keys.ToList())
            {
                if (!server.CanWrite)
                    continue;

                try
                {
                    server.BeginWrite(message, 0, message.Length, EndWrite, server);
                }
                catch (Exception e)
                {
                    if (e is ObjectDisposedException ||
                        e is InvalidOperationException ||
                        e is IOException)
                    {
                        _logger.Info("One of the servers is dead and will be collected");

                        // If the server is dead, we stash its corpse for further disposal
                        if (dead == null)
                            dead = new List<NamedPipeServerStream>();
                        dead.Add(server);
                    }
                }
            }
            _spinLock.Exit();

            // Get rid of the corpses
            if (dead != null && dead.Count > 0)
                for (int i = 0; i < dead.Count; i++)
                    RemoveServer(dead[i]);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _logger.Debug(nameof(Dispose));
                StopListen();
                StopServers();
            }
        }

        protected void OnMessageReceived(NamedPipeMessage message)
        {
            if (MessageReceived != null)
                MessageReceived(this, new NamedPipeMessageArgs(message));
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

        internal void Write(NamedPipeServerStream server, byte[] message)
        {
            _logger.Debug(nameof(Write));

            if (!server.CanWrite)
                return;

            try
            {
                server.BeginWrite(message, 0, message.Length, EndWrite, server);
            }
            catch (Exception e)
            {
                if (e is ObjectDisposedException ||
                    e is InvalidOperationException ||
                    e is IOException)
                {
                    // The pipe is effectively dead at this point
                    _logger.Info("The server is dead.");
                    RemoveServer(server);
                }
            }
        }

        private void EndWrite(IAsyncResult result)
        {
            var server = (NamedPipeServerStream)result.AsyncState;

            try
            {
                server.EndWrite(result);
            }
            catch (Exception e)
            {
                if (e is ObjectDisposedException ||
                    e is OperationCanceledException ||
                    e is IOException)
                {
                    // The pipe is effectively dead at this point
                    _logger.Info("Detected dead pipe in EndWrite.");
                    RemoveServer(server);
                }
            }
        }

        private void EndWaitForConnection(IAsyncResult result)
        {
            var server = (NamedPipeServerStream)result.AsyncState;

            try
            {
                server.EndWaitForConnection(result);
            }
            catch (ObjectDisposedException)
            {
                _logger.Info("Detected dead pipe in EndWaitForConnection.");
                return;
            }

            AddServer(server);

            if (server.CanRead)
                BeginRead(new ReadDto(server, null, new byte[1024]));

            if (_shouldListen == 1)
                SpawnListener();

            OnClientConnected(this);
        }

        private void EndRead(IAsyncResult result)
        {
            var dto = (ReadDto)result.AsyncState;

            int read = 0;

            try
            {
                read = dto.Server.EndRead(result);
            }
            catch (IOException)
            {
            }

            if (read == 0)
            {
                RemoveServer(dto.Server);
                return;
            }

            // If we got the whole message in one read, don't fuck around
            if (dto.Server.IsMessageComplete && dto.Data == null)
            {
                var msg = new byte[read];

                Array.Copy(dto.Buffer, msg, read);
                RaiseMessageRecieved(dto, msg);
                return;
            }

            if (dto.Data == null)
                dto.Data = new MemoryStream();

            dto.Data.Write(dto.Buffer, 0, read);

            if (dto.Server.IsMessageComplete)
                RaiseMessageRecieved(dto, dto.Data.ToArray());
            else
                BeginRead(dto);
        }

        private void AddServer(NamedPipeServerStream server)
        {
            _logger.Debug(nameof(AddServer));

            bool locked = false;
            _spinLock.Enter(ref locked);
            _servers.AddOrUpdate(server, new object(), (stream, o) => new object());
            _spinLock.Exit();
        }

        private void RemoveServer(NamedPipeServerStream server)
        {
            _logger.Debug(nameof(RemoveServer));

            bool removed, locked = false;
            object dummy;

            _spinLock.Enter(ref locked);
            removed = _servers.TryRemove(server, out dummy);
            _spinLock.Exit();

            if (removed)
                OnClientDisconnected(this);
        }

        private void RaiseMessageRecieved(ReadDto dto, byte[] msg)
        {
            dto.Data = null;
            BeginRead(dto);
            OnMessageReceived(new NamedPipeMessage(dto.Server, this, msg));
        }

        private void BeginRead(ReadDto dto)
        {
            dto.Server.BeginRead(dto.Buffer, 0, dto.Buffer.Length, EndRead, dto);
        }

        private void SpawnListener()
        {
            _logger.Debug(nameof(SpawnListener));

            _listener = GetServer();
            _listener.BeginWaitForConnection(EndWaitForConnection, _listener);
        }

        private NamedPipeServerStream GetServer()
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        }

        private class ReadDto
        {
            public readonly NamedPipeServerStream Server;
            public readonly byte[] Buffer;
            public MemoryStream Data;

            public ReadDto(NamedPipeServerStream server, MemoryStream data, byte[] buffer)
            {
                Server = server;
                Data = data;
                Buffer = buffer;
            }
        }
    }
}