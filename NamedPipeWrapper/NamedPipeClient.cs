using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;

namespace NamedPipeWrapper
{
    public class NamedPipeClient : NamedPipeBase
    {
        private readonly string _pipeName;
        private readonly int _timeout;

        private NamedPipeClientStream _client;

        public NamedPipeClient(string pipeName, TimeSpan timeout)
        {
            _timeout = (int)timeout.TotalMilliseconds;
            _pipeName = pipeName;
        }

        public async Task ConnectAsync()
        {
            Logger.Info($"Trying to connect to {_pipeName}");

            _client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);

            try
            {
                await _client.ConnectAsync(_timeout);
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to connect to {_pipeName} ({e.Message})", e);
                _client = null;
                throw;
            }

            Logger.Info($"Connected to {_pipeName}");

            ReadNextMessage(_client);
        }

        protected override void OnPipeDied(PipeStream stream)
        {
            base.OnPipeDied(stream);
            ConnectAsync().HandleException(e =>
            {
                Logger.Warn($"Reconnect failed: {e.Message}", e);
                throw e;
            });
        }

        public Task SendAsync<T>(T message)
        {
            if (_client == null)
            {
                throw new InvalidOperationException("Client is not connected!");
            }

            string serialized = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(serialized);
            return _client.WriteAsync(buffer, 0, buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _client?.Dispose();
            }
        }
    }
}