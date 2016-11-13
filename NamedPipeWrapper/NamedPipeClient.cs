using System;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Common.Logging;

namespace NamedPipeWrapper
{
    public class NamedPipeClient
    {
        private static readonly ILog _logger = LogManager.GetLogger<NamedPipeClient>();

        private NamedPipeClientStream _client;

        public void Connect(string pipeName)
        {
            _logger.Debug(nameof(Connect));

            _client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            try
            {
                _client.Connect(1000);
            }
            catch (TimeoutException e)
            {
                _logger.Warn($"Failed to connect to local pipe '{pipeName}' due to 1 second timeout");
                throw;
            }
        }

        public Task SendAsync<T>(T message)
        {
            string serialized = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(serialized);
            return _client.WriteAsync(buffer, 0, buffer.Length);
        }
    }
}