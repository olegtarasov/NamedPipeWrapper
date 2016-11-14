﻿using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;

namespace NamedPipeWrapper
{
    public class NamedPipeClient : NamedPipeBase
    {
        private const int MaxReconnectAttempts = 20;

        private readonly string _pipeName;
        private readonly int _timeout;

        private NamedPipeClientStream _client;

        public NamedPipeClient(string pipeName, TimeSpan timeout)
        {
            _timeout = (int)timeout.TotalMilliseconds;
            _pipeName = pipeName;
        }

        public bool IsConnected { get; set; }

        public async Task<bool> ConnectAsync()
        {
            CheckDisposed();

            IsConnected = false;

            Logger.Info($"Trying to connect to {_pipeName}");

            _client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);

            try
            {
                await _client.ConnectAsync(_timeout, CancellationToken.Token);
                _client.ReadMode = PipeTransmissionMode.Message;
            }
            catch (OperationCanceledException e)
            {
                OnPipeDied(_client, true);
                return false;
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to connect to {_pipeName} ({e.Message})", e);
                _client = null;
                return false;
            }

            Logger.Info($"Connected to {_pipeName}");

            IsConnected = true;

            ReadMessagesAsync(_client).HandleException(ex => {}); // All exceptions are handled internally.

            return true;
        }

        protected override void OnPipeDied(PipeStream stream, bool isCancelled)
        {
            IsConnected = false;

            base.OnPipeDied(stream, isCancelled);

            if (!isCancelled)
            {
                ReconnectAsync().HandleException(ex => {}); // All exceptions are handled internally.
            }
        }

        private async Task ReconnectAsync()
        {
            var interval = TimeSpan.FromSeconds(2);
            int attemptCount = 0;

            while (attemptCount < MaxReconnectAttempts)
            {
                Logger.Info($"Will attempt to reconnect in {interval.TotalSeconds} s.");
                await Task.Delay(interval);
                if (await ConnectAsync())
                {
                    return;
                }

                interval = TimeSpan.FromSeconds(interval.TotalSeconds * 2);
                attemptCount++;
            }

            Logger.Error($"Failed to reconnect to {_pipeName} in {MaxReconnectAttempts} attempts. Disposing pipe.");
            Dispose();
        }

        public Task SendAsync<T>(T message)
        {
            CheckDisposed();

            if (!IsConnected)
            {
                throw new InvalidOperationException("Client is not connected!");
            }

            string serialized = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(serialized);
            return SendAsync(_client, buffer);
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