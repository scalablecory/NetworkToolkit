﻿using NetworkToolkit.Connections;
using System;
using System.Net;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests.Http.Servers
{
    internal abstract class HttpTestServer : IAsyncDisposable
    {
        public abstract ValueTask DisposeAsync();
        public abstract Task<HttpTestConnection> AcceptAsync();

        public abstract EndPoint? EndPoint { get; }

        public Uri Uri
        {
            get
            {
                var uriBuilder = new UriBuilder
                {
                    Scheme = Uri.UriSchemeHttp,
                    Path = "/"
                };

                switch (EndPoint)
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

                return uriBuilder.Uri;
            }
        }

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
