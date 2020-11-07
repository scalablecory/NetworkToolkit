using NetworkToolkit.Connections;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests.Servers
{
    internal sealed class Http1TestServer : HttpTestServer
    {
        private readonly ConnectionListener _listener;

        public EndPoint? EndPoint => _listener.EndPoint;

        public Http1TestServer(ConnectionListener connectionListener)
        {
            _listener = connectionListener;
        }

        public override async Task<HttpTestConnection> AcceptAsync()
        {
            Connection? connection = await _listener.AcceptConnectionAsync().ConfigureAwait(false);
            Debug.Assert(connection != null);
            return new Http1TestConnection(connection);
        }

        public override ValueTask DisposeAsync() =>
            _listener.DisposeAsync();
    }
}
