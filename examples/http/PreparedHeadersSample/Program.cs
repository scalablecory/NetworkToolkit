using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace PreparedHeadersSample
{
    class Program
    {
        static async Task Main(string[] args)
        {
            PreparedHeaderSet preparedHeaders =
                new PreparedHeaderSetBuilder()
                .AddHeader("User-Agent", "NetworkToolkit")
                .AddHeader("Accept", "text/html")
                .Build();

            await using ConnectionFactory connectionFactory = new SocketConnectionFactory();
            await using Connection connection = await connectionFactory.ConnectAsync(new DnsEndPoint("microsoft.com", 80));
            await using HttpConnection httpConnection = new Http1Connection(connection, HttpPrimitiveVersion.Version11);

            int requestCounter = 0;
            await SingleRequest();
            await SingleRequest();

            async Task SingleRequest()
            {
                await using ValueHttpRequest request = (await httpConnection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact)).Value;

                request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                request.WriteRequest(HttpMethod.Get, new Uri("http://microsoft.com"));
                request.WriteHeader(preparedHeaders);
                request.WriteHeader("X-Example-RequestNo", requestCounter++.ToString());
                await request.CompleteRequestAsync();
                await request.DrainAsync();
            }
        }
    }
}
