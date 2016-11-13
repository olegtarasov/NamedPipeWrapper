using System.IO.Pipes;
using System.Threading.Tasks;

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

        public Task RespondAsync(byte[] response)
        {
            return _server.WriteAsync(_pipeServer, response);
        }
    }
}