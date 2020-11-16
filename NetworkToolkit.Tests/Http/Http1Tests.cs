using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using NetworkToolkit.Tests.Http.Servers;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace NetworkToolkit.Tests.Http
{
    public class Http1Tests : HttpGenericTests
    {
        public virtual ConnectionFactory CreateConnectionFactory() => new MemoryConnectionFactory();

        internal override async Task RunSingleStreamTest(Func<ValueHttpRequest, Uri, Task> clientFunc, Func<HttpTestStream, Task> serverFunc, int? millisecondsTimeout = null)
        {
            ConnectionFactory connectionFactory = CreateConnectionFactory();
            await using (connectionFactory.ConfigureAwait(false))
            {
                var server = new Http1TestServer(await connectionFactory.ListenAsync().ConfigureAwait(false));
                await using (server.ConfigureAwait(false))
                {
                    var uriBuilder = new UriBuilder
                    {
                        Scheme = Uri.UriSchemeHttp,
                        Path = "/"
                    };

                    switch (server.EndPoint)
                    {
                        case DnsEndPoint dnsEp:
                            uriBuilder.Host = dnsEp.Host;
                            uriBuilder.Port = dnsEp.Port;
                            break;
                        case IPEndPoint ipEp:
                            uriBuilder.Host = ipEp.Address.ToString();
                            uriBuilder.Port = ipEp.Port;
                            break;
                        default:
                            uriBuilder.Host = "localhost";
                            uriBuilder.Port = 80;
                            break;
                    }

                    Uri serverUri = uriBuilder.Uri;

                    await RunClientServer(RunClientAsync, RunServerAsync, millisecondsTimeout).ConfigureAwait(false);

                    async Task RunClientAsync()
                    {
                        HttpConnection connection = new Http1Connection(await connectionFactory.ConnectAsync(server.EndPoint!).ConfigureAwait(false));
                        await using (connection.ConfigureAwait(false))
                        {
                            ValueHttpRequest? optionalRequest = await connection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact).ConfigureAwait(false);
                            Assert.NotNull(optionalRequest);

                            ValueHttpRequest request = optionalRequest.Value;
                            await using (request.ConfigureAwait(false))
                            {
                                await clientFunc(request, serverUri).ConfigureAwait(false);
                                await request.DrainAsync().ConfigureAwait(false);
                            }
                        }
                    }

                    async Task RunServerAsync()
                    {
                        HttpTestConnection connection = await server.AcceptAsync().ConfigureAwait(false);
                        await using (connection.ConfigureAwait(false))
                        {
                            HttpTestStream request = await connection.AcceptStreamAsync().ConfigureAwait(false);
                            await using (request.ConfigureAwait(false))
                            {
                                await serverFunc(request).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }
    }
}
