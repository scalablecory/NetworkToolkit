using NetworkToolkit.Connections;
using System;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests.Http.Servers
{
    internal abstract class HttpTestServer : IAsyncDisposable
    {
        public abstract ValueTask DisposeAsync();
        public abstract Task<HttpTestConnection> AcceptAsync();

        public async Task<HttpTestFullRequest> ReceiveAndSendSingleRequestAsync(int statusCode = 200, TestHeadersSink? headers = null, string? content = null, TestHeadersSink? trailingHeaders = null)
        {
            HttpTestConnection connection = await AcceptAsync().ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                return await connection.ReceiveAndSendSingleRequestAsync(statusCode, headers, content, trailingHeaders).ConfigureAwait(false);
            }
        }
    }
}
