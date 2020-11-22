using NetworkToolkit.Benchmarks;
using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NetworkToolkit.ProfilerTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_THREADING_POOLASYNCVALUETASKS", "1");

            await using ConnectionFactory connectionFactory = new MemoryConnectionFactory();
            
            await using ConnectionListener listener = await connectionFactory.ListenAsync();
            await using SimpleHttp1Server server = new(listener, triggerBytes, responseBytes);

            await using Connection connection = await connectionFactory.ConnectAsync(listener.EndPoint!);
            await using HttpConnection httpConnection = new Http1Connection(connection, HttpPrimitiveVersion.Version11);

            if (!Debugger.IsAttached)
            {
                Console.WriteLine("Press any key to continue, once profiler is attached...");
                Console.ReadKey();
            }

            for (int i = 0; i < 1000000; ++i)
            {
                await using ValueHttpRequest request = (await httpConnection.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact))
                    ?? throw new Exception("HttpConnection failed to return a request");

                request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                request.WriteRequest(HttpRequest.GetMethod, scheme, authority, pathAndQuery);

                request.WriteHeader(preparedRequestHeaders);

                foreach ((byte[] name, byte[] value) in dynamicRequestHeaders)
                {
                    request.WriteHeader(name, value);
                }

                await request.CompleteRequestAsync();

                while (await request.ReadAsync() != HttpReadType.EndOfStream)
                {
                    // do nothing, just draining.
                }
            }
        }

        static readonly byte[] scheme = Encoding.ASCII.GetBytes("http");
        static readonly byte[] authority = Encoding.ASCII.GetBytes("localhost");
        static readonly byte[] pathAndQuery = Encoding.ASCII.GetBytes("/");

        static readonly PreparedHeaderSet preparedRequestHeaders =
            new PreparedHeaderSetBuilder()
            .AddHeader("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9")
            .AddHeader("accept-encoding", "gzip, deflate, br")
            .AddHeader("accept-language", "en-US,en;q=0.9")
            .AddHeader("sec-fetch-dest", "document")
            .AddHeader("sec-fetch-mode", "navigate")
            .AddHeader("sec-fetch-site", "none")
            .AddHeader("sec-fetch-user", "?1")
            .AddHeader("upgrade-insecure-requests", "1")
            .AddHeader("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.198 Safari/537.36 Edg/86.0.622.69")
            .Build();

        static readonly List<(byte[], byte[])> dynamicRequestHeaders = new()
        {
            (Encoding.ASCII.GetBytes("cookie"), Encoding.ASCII.GetBytes("cookie: aaaa=000000000000000000000000000000000000; bbb=111111111111111111111111111; ccccc=22222222222222222222222222; dddddd=333333333333333333333333333333333333333333333333333333333333333333333; eeee=444444444444444444444444444444444444444444444444444444444444444444444444444"))
        };

        static readonly byte[] triggerBytes = Encoding.ASCII.GetBytes("\r\n\r\n");
        static readonly byte[] responseBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" +
            "Content-Length: 0\r\n" +
            "Accept-Ranges: bytes\r\n" +
            "Cache-Control: private\r\n" +
            "Content-Security-Policy: upgrade-insecure-requests; frame-ancestors 'self' https://stackexchange.com\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Date: Mon, 16 Nov 2020 23:35:36 GMT\r\n" +
            "Feature-Policy: microphone 'none'; speaker 'none'\r\n" +
            "Server: Microsoft-IIS/10.0\r\n" +
            "Strict-Transport-Security: max-age=15552000\r\n" +
            "Vary: Accept-Encoding,Fastly-SSL\r\n" +
            "Via: 1.1 varnish\r\n" +
            "x-account-id: 12345\r\n" +
            "x-aspnet-duration-ms: 44\r\n" +
            "x-cache: MISS\r\n" +
            "x-cache-hits: 0\r\n" +
            "x-dns-prefetch-control: off\r\n" +
            "x-flags: QA\r\n" +
            "x-frame-options: SAMEORIGIN\r\n" +
            "x-http-count: 2\r\n" +
            "x-http-duration-ms: 8\r\n" +
            "x-is-crawler: 0\r\n" +
            "x-page-view: 1\r\n" +
            "x-providence-cookie: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\r\n" +
            "x-redis-count: 22\r\n" +
            "x-redis-duration-ms: 2\r\n" +
            "x-request-guid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\r\n" +
            "x-route-name: Home/Index\r\n" +
            "x-served-by: cache-sea4460-SEA\r\n" +
            "x-sql-count: 12\r\n" +
            "x-sql-duration-ms: 12\r\n" +
            "x-timer: S1605569737.604081,VS0,VE106\r\n" +
            "\r\n");
    }
}
