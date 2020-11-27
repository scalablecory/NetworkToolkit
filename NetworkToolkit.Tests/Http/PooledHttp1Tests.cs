using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using NetworkToolkit.Tests.Http.Servers;
using System.Net;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests.Http
{
    public class PooledHttp1Tests : HttpGenericTests
    {
        internal override HttpPrimitiveVersion Version => HttpPrimitiveVersion.Version11;

        internal override async Task<HttpTestServer> CreateTestServerAsync(ConnectionFactory connectionFactory) =>
            new Http1TestServer(await connectionFactory.ListenAsync().ConfigureAwait(false));

        internal override Task<HttpConnection> CreateTestClientAsync(ConnectionFactory connectionFactory, EndPoint endPoint) =>
            Task.FromResult<HttpConnection>(new PooledHttpConnection(connectionFactory, endPoint, sslTargetHost: null));
    }
}
