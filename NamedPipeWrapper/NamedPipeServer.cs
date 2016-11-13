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

// ReSharper disable ImpureMethodCallOnReadonlyValueField

namespace NamedPipeWrapper
{
    public class NamedPipeServer : IDisposable
    {
        private static readonly ILog _logger = LogManager.GetLogger<NamedPipeServer>();


        private readonly ConcurrentDictionary<NamedPipeServerStream, object> _servers = new ConcurrentDictionary<NamedPipeServerStream, object>();
        private readonly ConcurrentDictionary<Type, Action<DeserializedMessage>> _handlers = new ConcurrentDictionary<Type, Action<DeserializedMessage>>();
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

        public void HandleMessage<T>(Action<DeserializedMessage<T>> handler)
        {
            var wrapper = new Action<DeserializedMessage>(message => handler((DeserializedMessage<T>)message));

            _handlers.AddOrUpdate(typeof(T), wrapper, (type, action) => wrapper);
        }

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

        public async Task WriteToAllAsync(byte[] message)
        {
            _logger.Debug(nameof(WriteToAllAsync));

            List<NamedPipeServerStream> dead = null;

            bool locked = false;
            _spinLock.Enter(ref locked);
            foreach (var server in _servers.Keys.ToList())
            {
                if (!server.CanWrite)
                    continue;

                await WriteAsync(server, message);
            }
            _spinLock.Exit();
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
                _handlers.Clear();
            }
        }

        protected void OnMessageReceived(NamedPipeMessage message)
        {
            if (HandleSerializedMessage(message))
            {
                return;
            }

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

        internal async Task WriteAsync(NamedPipeServerStream server, byte[] message)
        {
            _logger.Debug(nameof(WriteAsync));

            if (!server.CanWrite)
                return;

            try
            {
                await server.WriteAsync(message, 0, message.Length);
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

        private bool HandleSerializedMessage(NamedPipeMessage message)
        {
            if (_handlers.Count == 0)
            {
                return false;
            }

            string json;
            object deserialized;

            try
            {
                json = Encoding.UTF8.GetString(message.Message);
                deserialized = JsonSerializer.Deserialize(json);
            }
            catch (Exception e)
            {
                _logger.Info($"Failed to deserialize message", e);
                return false;
            }

            _logger.Debug($"Received serialized message: {json}");

            Action<DeserializedMessage> handler;
            if (_handlers.TryGetValue(deserialized.GetType(), out handler))
            {
                handler(new DeserializedMessage(message, deserialized));
                return true;
            }

            _logger.Info("No handler found for this message, dropping.");
            return false;
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

        private void RaiseMessageRecieved(NamedPipeServerStream server, ReadDto dto, byte[] msg)
        {
            dto.Data = null;
            ReadNextMessage(server, dto);
            OnMessageReceived(new NamedPipeMessage(server, this, msg));
        }

        private void SpawnListener()
        {
            ListenForConnection().HandleException(e => HandlePipeException(e, null));
        }

        private void ReadNextMessage(NamedPipeServerStream server, ReadDto dto)
        {
            ReadNextMessageAsync(server, dto).HandleException(e => HandlePipeException(e, server));
        }

        private void HandlePipeException(Exception exception, NamedPipeServerStream server)
        {
            if (exception is IOException || exception is ObjectDisposedException || exception is OperationCanceledException)
            {
                if (server != null)
                {
                    RemoveServer(server);
                }
                return;
            }

            throw exception;
        }

        private async Task ReadNextMessageAsync(NamedPipeServerStream server, ReadDto dto)
        {
            int read = await server.ReadAsync(dto.Buffer, 0, dto.Buffer.Length);
            if (read == 0)
            {
                RemoveServer(server);
                return;
            }

            // If we got the whole message in one read, don't fuck around
            if (server.IsMessageComplete && dto.Data == null)
            {
                var msg = new byte[read];

                Array.Copy(dto.Buffer, msg, read);
                RaiseMessageRecieved(server, dto, msg);
                return;
            }

            if (dto.Data == null)
                dto.Data = new MemoryStream();

            dto.Data.Write(dto.Buffer, 0, read);

            if (server.IsMessageComplete)
                RaiseMessageRecieved(server, dto, dto.Data.ToArray());
            else
                ReadNextMessage(server, dto);
        }

        private async Task ListenForConnection()
        {
            _logger.Debug(nameof(ListenForConnection));

            var server = GetServer();
            await server.WaitForConnectionAsync();

            AddServer(server);

            if (server.CanRead)
                ReadNextMessage(server, new ReadDto(null, new byte[1024]));

            if (_shouldListen == 1)
                SpawnListener();

            OnClientConnected(this);
        }

        private NamedPipeServerStream GetServer()
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        }

        private class ReadDto
        {
            public readonly byte[] Buffer;
            public MemoryStream Data;

            public ReadDto(MemoryStream data, byte[] buffer)
            {
                Data = data;
                Buffer = buffer;
            }
        }
    }
}