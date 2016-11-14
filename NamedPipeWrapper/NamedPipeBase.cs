using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
        private const int ReceiveCacheSize = 10;

        protected readonly ILog Logger;
        protected readonly CancellationTokenSource CancellationToken = new CancellationTokenSource();

        private readonly ConcurrentDictionary<Type, Action<DeserializedMessage>> _handlers = new ConcurrentDictionary<Type, Action<DeserializedMessage>>();
        private readonly ConcurrentDictionary<DeserializedMessage, object> _messageCache = new ConcurrentDictionary<DeserializedMessage, object>();

        private bool _isDisposed = false;

        protected NamedPipeBase()
        {
            Logger = LogManager.GetLogger(GetType());
        }

        public event EventHandler<NamedPipeMessageArgs> BinaryMessageReceived;

        public async Task<Tuple<DeserializedMessage<T1>, DeserializedMessage<T2>>> AwaitAnyMessageAsync<T1, T2>(CancellationToken cts = default(CancellationToken))
            where T1 : class
            where T2 : class
        {
            // First we register a handler.
            var evt = new ManualResetEventSlim();
            Tuple<DeserializedMessage<T1>, DeserializedMessage<T2>> result = null;
            HandleMessage<T1>(msg =>
            {
                result = new Tuple<DeserializedMessage<T1>, DeserializedMessage<T2>>(msg, null);
                evt.Set();
            });
            HandleMessage<T2>(msg =>
            {
                result = new Tuple<DeserializedMessage<T1>, DeserializedMessage<T2>>(null, msg);
                evt.Set();
            });

            // Then we check if the message is already in cache.
            if (_messageCache.Count > 0)
            {
                bool found = false;

                foreach (var message in _messageCache.Keys.ToList())
                {
                    var msg1 = message.Message as T1;
                    if (msg1 != null)
                    {
                        result = new Tuple<DeserializedMessage<T1>, DeserializedMessage<T2>>((DeserializedMessage<T1>)message, null);
                        found = true;
                        object dummy;
                        _messageCache.TryRemove(message, out dummy);
                        break;
                    }

                    var msg2 = message.Message as T2;
                    if (msg2 != null)
                    {
                        result = new Tuple<DeserializedMessage<T1>, DeserializedMessage<T2>>(null, (DeserializedMessage<T2>)message);
                        found = true;
                        object dummy;
                        _messageCache.TryRemove(message, out dummy);
                        break;
                    }
                }

                if (found)
                {
                    Action<DeserializedMessage> dummy;
                    _handlers.TryRemove(typeof(T1), out dummy);
                    _handlers.TryRemove(typeof(T2), out dummy);

                }
            }

            // We didn't find our message in cache, so just wait for it.
            var ct = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.Token, cts);
            try
            {
                await Task.Run(() => evt.Wait(ct.Token), ct.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            return result;
        }

        public async Task<DeserializedMessage<T>> AwaitSingleMessageAsync<T>(CancellationToken cts = default(CancellationToken)) where T : class 
        {
            // First we register a handler.
            var evt = new ManualResetEventSlim();
            DeserializedMessage<T> result = null;
            HandleMessage<T>(msg =>
            {
                result = msg;
                evt.Set();
            });

            // Then we check if the message is already in cache.
            if (_messageCache.Count > 0)
            {
                foreach (var message in _messageCache.Keys.ToList())
                {
                    var msg = message.Message as T;
                    if (msg != null)
                    {
                        object dummy;
                        _messageCache.TryRemove(message, out dummy);
                        Action<DeserializedMessage> hDummy;
                        _handlers.TryRemove(typeof(T), out hDummy);
                        return (DeserializedMessage<T>)message;
                    }
                }
            }

            // We didn't find our message in cache, so just wait for it.
            var ct = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.Token, cts);
            try
            {
                await Task.Run(() => evt.Wait(ct.Token), ct.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            return result;
        }

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

        protected async Task ReadMessagesAsync(PipeStream stream, CancellationToken cancellationToken)
        {
            CheckDisposed();

            if (!stream.CanRead)
            {
                Logger.Warn($"Pipe stream doesn't support reading. Not listening to messages.");
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var buffer = new byte[2048];
                var ms = new MemoryStream();

                do
                {
                    int read = 0;
                    try
                    {
                        read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
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

                    if (read == 0)
                    {
                        break;
                    }

                    ms.Write(buffer, 0, read);
                } while (!stream.IsMessageComplete);

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

        internal async Task<bool> SendAsync(PipeStream stream, byte[] message, CancellationToken cts = default(CancellationToken))
        {
            CheckDisposed();

            if (!stream.CanWrite)
            {
                Logger.Warn($"Tried to send a messege to the pipe that can't be written to. Dropping the message.");
                return false;
            }

            try
            {
                var ct = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.Token, cts);
                await stream.WriteAsync(message, 0, message.Length, ct.Token);
                return true;
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

            return false;
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
            var msg = new DeserializedMessage(message, deserialized);
            if (_handlers.TryGetValue(deserialized.GetType(), out handler))
            {
                handler(msg);
            }
            else
            {
                Logger.Info("No handler found for this message, storing in the cache.");
                if (_messageCache.Count >= ReceiveCacheSize)
                {
                    Logger.Info("Maximum receive cache size is reached, dropping RANDOM message from the cache.");
                    var key = _messageCache.Keys.FirstOrDefault();
                    if (key != null)
                    {
                        object dummy;
                        _messageCache.TryRemove(key, out dummy);
                    }
                }
                _messageCache.AddOrUpdate(msg, new object(), (deserializedMessage, o) => new object());
            }

            return true;
        }
    }
}