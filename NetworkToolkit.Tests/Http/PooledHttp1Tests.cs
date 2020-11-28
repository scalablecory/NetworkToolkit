using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using NetworkToolkit.Tests.Http.Servers;
using System.Net;
using System.Net.Security;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests.Http
{
    public class PooledHttp1Tests : HttpGenericTests
    {
        internal override HttpPrimitiveVersion Version => HttpPrimitiveVersion.Version11;

        internal override async Task<HttpTestServer> CreateTestServerAsync(ConnectionFactory connectionFactory) =>
            new Http1TestServer(await connectionFactory.ListenAsync(options: CreateListenerProperties()).ConfigureAwait(false));

        internal override Task<HttpConnection> CreateTestClientAsync(ConnectionFactory connectionFactory, EndPoint endPoint)
        {
            IConnectionProperties? properties = CreateConnectProperties();

            SslClientAuthenticationOptions? sslOptions = null;
            properties?.TryGetProperty(SslConnectionFactory.SslClientAuthenticationOptionsPropertyKey, out sslOptions);

            return Task.FromResult<HttpConnection>(new PooledHttpConnection(connectionFactory, endPoint, sslOptions));
        }
    }

    public class PooledHttp1SslTests : PooledHttp1Tests
    {
        internal override bool UseSsl => true;
    }
}
