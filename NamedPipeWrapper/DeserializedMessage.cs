﻿using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using Common.Logging;

namespace NamedPipeWrapper
{
    public sealed class DeserializedMessage<T> : DeserializedMessageBase
    {
        public DeserializedMessage(NamedPipeMessage originalMessage, T message) : base(originalMessage)
        {
            Message = message;
        }

        public T Message { get; set; }

        public static explicit operator DeserializedMessage<T>(DeserializedMessage message)
        {
            return new DeserializedMessage<T>(message.OriginalMessage, (T)message.Message);
        }
    }

    public sealed class DeserializedMessage : DeserializedMessageBase
    {
        public DeserializedMessage(NamedPipeMessage originalMessage, object message) : base(originalMessage)
        {
            Message = message;
        }

        public object Message { get; set; }
    }

    public abstract class DeserializedMessageBase
    {
        protected readonly ILog Logger = LogManager.GetLogger<DeserializedMessageBase>();

        protected DeserializedMessageBase(NamedPipeMessage originalMessage)
        {
            OriginalMessage = originalMessage;
        }

        public NamedPipeMessage OriginalMessage { get; set; }

        public Task RespondAsync<T>(T message)
        {
            string json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);

            Logger.Info($"Responding with message: {json}");

            return OriginalMessage.RespondAsync(buffer);
        }
    }
}