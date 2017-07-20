using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wire;
using Xunit;
using XunitShould;

namespace NamedPipeWrapper.Tests
{
    public class PipeTests
    {
        private const string PipeName = "TestPipe";

        [Fact]
        public void CanConnectToServer()
        {
            var server = new NamedPipeServer(PipeName);
            server.StartListen();

            var client = new NamedPipeClient(PipeName, TimeSpan.FromSeconds(2));
            client.ConnectAsync().Result.ShouldBeTrue();
        }

        [Fact]
        public void CanSendBinaryMessageToServer()
        {
            var bytes = new byte[] {1, 2, 3};
            var serializer = new Serializer();

            using (var ms = new MemoryStream())
            {
                serializer.Serialize(bytes, ms);
                ms.Position = 0;

                var s = serializer.GetDeserializerSession();
                var d = serializer.GetDeserializerByManifest(ms, s);
                var v = d.ReadValue(ms, s);
            }
            
        }
    }
}
