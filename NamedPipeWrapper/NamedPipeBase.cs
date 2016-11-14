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
        protected readonly CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        private readonly ConcurrentDictionary<Type, Action<DeserializedMessage>> _handlers = new ConcurrentDictionary<Type, Action<DeserializedMessage>>();
        private readonly ConcurrentDictionary<DeserializedMessage, object> _messageCache = new ConcurrentDictionary<DeserializedMessage, object>();

        private bool _isDisposed = false;

        protected NamedPipeBase()
        {
            Logger = LogManager.GetLogger(GetType());
        }

        public event EventHandler<NamedPipeMessageArgs> BinaryMessageReceived;

        protected CancellationTokenSource CombineWithInternalToken(CancellationToken ct)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(CancellationTokenSource.Token, ct);
        }

        public Task<DeserializedMessage> AwaitAnyMessageAsync<T1, T2>(CancellationToken ct = default(CancellationToken)) where T1 : class where T2 : class
        {
            return AwaitAnyMessageAsync(new[] {typeof(T1), typeof(T2)}, ct);
        }

        public async Task<DeserializedMessage<T>> AwaitSingleMessageAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class
        {
            return (DeserializedMessage<T>)await AwaitAnyMessageAsync(new[] {typeof(T)}, ct);
        }

        private async Task<DeserializedMessage> AwaitAnyMessageAsync(Type[] types, CancellationToken ct = default(CancellationToken))
        {
            var evt = new ManualResetEventSlim();
            DeserializedMessage result = null;
            var set = types.ToHashSet();

            foreach (var type in types)
            {
                var handler = new Action<DeserializedMessage>(msg =>
                {
                    result = msg;
                    evt.Set();
                });
                _handlers.AddOrUpdate(type, handler, (t, a) => handler);
            }

            if (_messageCache.Count > 0)
            {
                foreach (var message in _messageCache.Keys.ToList())
                {
                    if (set.Contains(message.Message.GetType()))
                    {
                        _messageCache.Remove(message);
                        foreach (var type in types)
                        {
                            _handlers.Remove(type);
                        }

                        return message;
                    }
                }
            }

            var cts = CombineWithInternalToken(ct);
            try
            {
                await Task.Run(() => evt.Wait(cts.Token), cts.Token);
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
                CancellationTokenSource.Cancel();
                CancellationTokenSource.Dispose();
            }
        }

        protected async Task ReadMessagesAsync(PipeStream stream, CancellationToken ct)
        {
            CheckDisposed();

            if (!stream.CanRead)
            {
                Logger.Warn($"Pipe stream doesn't support reading. Not listening to messages.");
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var buffer = new byte[2048];
                var ms = new MemoryStream();

                do
                {
                    int read = 0;
                    try
                    {
                        read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
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

                    if (read == 0) // Means that connection was closed.
                    {
                        OnPipeDied(stream, false);
                        return;
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

        internal async Task<bool> SendAsync(PipeStream stream, byte[] message, CancellationToken ct = default(CancellationToken))
        {
            CheckDisposed();

            if (!stream.CanWrite)
            {
                Logger.Warn($"Tried to send a messege to the pipe that can't be written to. Dropping the message.");
                return false;
            }

            try
            {
                var cts = CombineWithInternalToken(ct);
                await stream.WriteAsync(message, 0, message.Length, cts.Token);
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
                        _messageCache.Remove(key);
                    }
                }
                _messageCache.AddOrUpdate(msg, new object(), (deserializedMessage, o) => new object());
            }

            return true;
        }
    }
}