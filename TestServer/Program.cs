using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NamedPipeWrapper;

namespace TestServer
{
    class Program
    {
        static void Main(string[] args)
        {
            var server = new NamedPipeServer("npstest");
            server.StartListen();

            while (true)
            {
                var bytes = server.AwaitSingleMessageAsync<byte[]>().Result;
            }
        }
    }
}
