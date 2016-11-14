using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;

namespace NamedPipeWrapper
{
    public abstract class NamedPipeBase : IDisposable
    {
        protected readonly ILog Logger;
        protected readonly CancellationTokenSource CancellationToken = new CancellationTokenSource();

        private readonly ConcurrentDictionary<Type, Action<DeserializedMessage>> _handlers = new ConcurrentDictionary<Type, Action<DeserializedMessage>>();

        private bool _isDisposed = false;

        protected NamedPipeBase()
        {
            Logger = LogManager.GetLogger(GetType());
        }

        public event EventHandler<NamedPipeMessageArgs> BinaryMessageReceived;

        public void HandleMessage<T>(Action<DeserializedMessage<T>> handler)
        {
            CheckDisposed();

            var wrapper = new Action<DeserializedMessage>(message => handler((DeserializedMessage<T>)message));

            _handlers.AddOrUpdate(typeof(T), wrapper, (type, action) => wrapper);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected void CheckDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException("Object has been disposed.");
        }

        protected virtual void OnPipeDied(PipeStream stream, bool isCancelled)
        {
            if (!isCancelled)
            {
                Logger.Info("Pipe stream is dead.");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isDisposed = true;
                _handlers.Clear();
                CancellationToken.Cancel();
                CancellationToken.Dispose();
            }
        }

        protected async Task ReadMessagesAsync(PipeStream stream)
        {
            CheckDisposed();

            if (!stream.CanRead)
            {
                Logger.Warn($"Pipe stream doesn't support reading. Not listening to messages.");
                return;
            }

            while (!CancellationToken.Token.IsCancellationRequested)
            {
                var ms = new MemoryStream();

                try
                {
                    await stream.CopyToAsync(ms, 2048, CancellationToken.Token);
                }
                catch (OperationCanceledException e)
                {
                    OnPipeDied(stream, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    OnPipeDied(stream, false);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Unhandled stream exception: {ex.Message}. Disposing the pipe.", ex);
                    Dispose();
                    return;
                }

                if (ms.Length > 0)
                {
                    var message = new NamedPipeMessage(stream, this, ms.ToArray());
                    OnBinaryMessageReceived(message);
                }
            }
        }

        protected void OnBinaryMessageReceived(NamedPipeMessage message)
        {
            if (HandleSerializedMessage(message))
            {
                return;
            }

            if (BinaryMessageReceived != null)
                BinaryMessageReceived(this, new NamedPipeMessageArgs(message));
        }

        internal async Task SendAsync(PipeStream stream, byte[] message)
        {
            CheckDisposed();

            if (!stream.CanWrite)
            {
                Logger.Warn($"Tried to send a messege to the pipe that can't be written to. Dropping the message.");
                return;
            }

            try
            {
                await stream.WriteAsync(message, 0, message.Length, CancellationToken.Token);
            }
            catch (OperationCanceledException e)
            {
                OnPipeDied(stream, true);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                OnPipeDied(stream, false);
            }
            catch (Exception ex)
            {
                Logger.Error($"Unhandled stream exception: {ex.Message}. Disposing the pipe.", ex);
                Dispose();
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

            Logger.Info("No handler found for this message, processing as binary.");
            return false;
        }
    }
}