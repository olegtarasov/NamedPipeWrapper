using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NamedPipeWrapper;

namespace TestClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var client = new NamedPipeClient("npstest", TimeSpan.FromSeconds(2));
            client.ConnectAsync().Wait();
            client.SendAsync(new byte[] {1, 2, 3}).Wait();
        }
    }
}
