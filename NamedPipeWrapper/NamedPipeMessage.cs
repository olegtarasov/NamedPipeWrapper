using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace NamedPipeWrapper
{
    public class NamedPipeMessage
    {
        private readonly PipeStream _stream;
        private readonly NamedPipeBase _pipe;

        public NamedPipeMessage(PipeStream stream, NamedPipeBase pipe, byte[] message)
        {
            this._stream = stream;
            this._pipe = pipe;
            Message = message;
        }

        public byte[] Message { get; private set; }

        public Task RespondAsync(byte[] response, CancellationToken ct = default(CancellationToken))
        {
            return _pipe.SendAsync(_stream, response, ct);
        }
    }
}