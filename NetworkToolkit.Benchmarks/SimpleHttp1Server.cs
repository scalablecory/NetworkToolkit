using NetworkToolkit.Connections;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Benchmarks
{
    internal sealed class SimpleHttp1Server : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ConnectionListener _listener;
        private readonly byte[] _trigger;
        private readonly byte[] _response;

        public SimpleHttp1Server(ConnectionListener listener, byte[] trigger, byte[] response)
        {
            _listener = listener;
            _trigger = trigger;
            _response = response;
            _ = ListenAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        private async Task ListenAsync()
        {
            Connection? con;
            while ((con = await _listener.AcceptConnectionAsync(cancellationToken: _cts.Token)) != null)
            {
                _ = RunConnectionAsync(con);
            }
        }

        private async Task RunConnectionAsync(Connection connection)
        {
            try
            {
                await using (connection.ConfigureAwait(false))
                using (var readBuffer = new ArrayBuffer(4096))
                {
                    Stream stream = connection.Stream;

                    while (true)
                    {
                        int triggerIdx;
                        while ((triggerIdx = readBuffer.ActiveSpan.IndexOf(_trigger)) == -1)
                        {
                            readBuffer.EnsureAvailableSpace(1);

                            int readLen = await stream.ReadAsync(readBuffer.AvailableMemory).ConfigureAwait(false);
                            if (readLen == 0) return;

                            readBuffer.Commit(readLen);
                        }

                        readBuffer.Discard(triggerIdx + _trigger.Length);

                        await stream.WriteAsync(_response).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
