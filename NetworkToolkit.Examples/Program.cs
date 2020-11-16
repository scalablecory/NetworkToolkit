using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace HttpPrimitives
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await RequestSimpleAsync();
            await PreparedHeadersAsync();
        }

        static async Task RequestSimpleAsync()
        {
            await using ConnectionFactory connectionFactory = new SocketConnectionFactory();
            await using Connection connection = await connectionFactory.ConnectAsync(new DnsEndPoint("microsoft.com", 80));
            await using HttpConnection httpConnection = new Http1Connection(connection);

            await using (ValueHttpRequest request = (await httpConnection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact)).Value)
            {
                request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                request.WriteRequest(HttpMethod.Get, new Uri("http://microsoft.com"));
                request.WriteHeader("Accept", "text/html");
                await request.CompleteRequestAsync();

                await request.ReadToFinalResponseAsync();
                Console.WriteLine($"Final response code: {request.StatusCode}");

                if (await request.ReadToHeadersAsync())
                {
                    await request.ReadHeadersAsync(new PrintingHeadersSink(), state: null);
                }
                else
                {
                    Console.WriteLine("No headers received.");
                }

                if (await request.ReadToContentAsync())
                {
                    long totalLen = 0;

                    var buffer = new byte[4096];
                    int readLen;

                    do
                    {
                        while ((readLen = await request.ReadContentAsync(buffer)) != 0)
                        {
                            totalLen += readLen;
                        }
                    }
                    while (await request.ReadToNextContentAsync());

                    Console.WriteLine($"Received {totalLen} byte response.");
                }
                else
                {
                    Console.WriteLine("No content received.");
                }

                if (await request.ReadToTrailingHeadersAsync())
                {
                    await request.ReadHeadersAsync(new PrintingHeadersSink(), state: null);
                }
                else
                {
                    Console.WriteLine("No trailing headers received.");
                }
            }
        }

        static async Task PreparedHeadersAsync()
        {
            PreparedHeaderSet preparedHeaders =
                new PreparedHeaderSetBuilder()
                .AddHeader("User-Agent", "NetworkToolkit")
                .AddHeader("Accept", "text/html")
                .Build();

            await using ConnectionFactory connectionFactory = new SocketConnectionFactory();
            await using Connection connection = await connectionFactory.ConnectAsync(new DnsEndPoint("microsoft.com", 80));
            await using HttpConnection httpConnection = new Http1Connection(connection);

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

        sealed class PrintingHeadersSink : IHttpHeadersSink
        {
            public void OnHeader(object state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
            {
                Console.WriteLine($"{Encoding.ASCII.GetString(headerName)}: {Encoding.ASCII.GetString(headerValue)}");
            }
        }
    }
}
