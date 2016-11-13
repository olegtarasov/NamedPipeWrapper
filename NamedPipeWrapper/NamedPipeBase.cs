using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Common.Logging;

namespace NamedPipeWrapper
{
    public abstract class NamedPipeBase : IDisposable
    {
        protected readonly ILog Logger;

        private readonly ConcurrentDictionary<Type, Action<DeserializedMessage>> _handlers = new ConcurrentDictionary<Type, Action<DeserializedMessage>>();

        protected NamedPipeBase()
        {
            Logger = LogManager.GetLogger(GetType());
        }

        public event EventHandler<NamedPipeMessageArgs> MessageReceived;

        public void HandleMessage<T>(Action<DeserializedMessage<T>> handler)
        {
            var wrapper = new Action<DeserializedMessage>(message => handler((DeserializedMessage<T>)message));

            _handlers.AddOrUpdate(typeof(T), wrapper, (type, action) => wrapper);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void OnPipeDied(PipeStream stream)
        {
            Logger.Info("Pipe stream is dead.");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _handlers.Clear();
            }
        }

        protected void HandleStreamException(Exception exception, PipeStream stream)
        {
            if (exception is IOException || exception is ObjectDisposedException || exception is OperationCanceledException)
            {
                if (stream != null)
                {
                    Logger.Info("Pipe stream has died.");
                    OnPipeDied(stream);
                }
                return;
            }

            throw exception;
        }

        protected async Task ReadNextMessageAsync(PipeStream stream)
        {
            var buffer = new byte[1024];
            MemoryStream ms = null;

            do
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    OnPipeDied(stream);
                    return;
                }

                if (stream.IsMessageComplete && ms == null)
                {
                    break;
                }

                ms = new MemoryStream();

                ms.Write(buffer, 0, read);
            } while (stream.IsMessageComplete);

            // Start reading the next message
            ReadNextMessage(stream);

            var message = new NamedPipeMessage(stream, this, ms == null ? buffer : ms.ToArray());
            OnMessageReceived(message);
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

        protected void ReadNextMessage(PipeStream stream)
        {
            ReadNextMessageAsync(stream).HandleException(e => HandleStreamException(e, stream));
        }

        internal async Task SendAsync(PipeStream stream, byte[] message)
        {
            if (!stream.CanWrite)
                return;

            try
            {
                await stream.WriteAsync(message, 0, message.Length);
            }
            catch (Exception e)
            {
                HandleStreamException(e, stream);
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
                Logger.Warn("Failed to deserialize message", e);
                return false;
            }

            Logger.Debug($"Received serialized message: {json}");

            Action<DeserializedMessage> handler;
            if (_handlers.TryGetValue(deserialized.GetType(), out handler))
            {
                handler(new DeserializedMessage(message, deserialized));
                return true;
            }

            Logger.Info("No handler found for this message, dropping.");
            return false;
        }
    }
}