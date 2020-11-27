using BenchmarkDotNet.Attributes;
using NetworkToolkit.Connections;
using NetworkToolkit.Http.Primitives;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Benchmarks
{
    [Config(typeof(NetworkToolkitConfig))]
    public class SerializedGet
    {
        private readonly ConnectionFactory _connectionFactory = new MemoryConnectionFactory();

        private ConnectionListener? _connectionListener;
        private SimpleHttp1Server? _server;

        private HttpMessageInvoker? _httpClient;
        private Uri? _uri;

        private Connection? _connection;
        private HttpConnection? _primitiveConnection;
        private byte[]? _encodedAuthority, _encodedPathAndQuery;

        [ParamsSource(nameof(ValuesForRequestHeaders))]
        public RequestHeaderCollection? RequestHeaders { get; set; }

        public static IEnumerable<RequestHeaderCollection> ValuesForRequestHeaders => new[]
        {
            new RequestHeaderCollection("Minimal").Build(),
            new RequestHeaderCollection("Normal")
            {
                StaticHeaders = new List<(string, string)>
                    {
                        ("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9"),
                        ("accept-encoding", "gzip, deflate, br"),
                        ("accept-language", "en-US,en;q=0.9"),
                        ("sec-fetch-dest", "document"),
                        ("sec-fetch-mode", "navigate"),
                        ("sec-fetch-site", "none"),
                        ("sec-fetch-user", "?1"),
                        ("upgrade-insecure-requests", "1"),
                        ("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.198 Safari/537.36 Edg/86.0.622.69")
                    },
                DynamicHeaders = new List<(string, string)>
                    {
                        ("cookie", "cookie: aaaa=000000000000000000000000000000000000; bbb=111111111111111111111111111; ccccc=22222222222222222222222222; dddddd=333333333333333333333333333333333333333333333333333333333333333333333; eeee=444444444444444444444444444444444444444444444444444444444444444444444444444")
                    }
            }.Build()
        };

        [ParamsSource(nameof(ValuesForResponseBytes))]
        public EncodedMessage? ResponseBytes { get; set; }

        public static IEnumerable<EncodedMessage> ValuesForResponseBytes => new[]
        {
            new EncodedMessage("Minimal", "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"),
            new EncodedMessage("StackOverflow",
                "HTTP/1.1 200 OK\r\n" +
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
                "\r\n")
        };

        [GlobalSetup]
        public void GlobalSetup()
        {
            _connectionListener = _connectionFactory.ListenAsync().AsTask().Result;
            _server = new SimpleHttp1Server(_connectionListener, trigger: Encoding.ASCII.GetBytes("\r\n\r\n"), response: ResponseBytes!.Response);
            _connection = _connectionFactory.ConnectAsync(_connectionListener.EndPoint!).AsTask().Result;
            _primitiveConnection = new Http1Connection(_connection, HttpPrimitiveVersion.Version11);
            _httpClient = new HttpMessageInvoker(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectCallback = async (ct, token) => (await _connectionFactory.ConnectAsync(_connectionListener.EndPoint!).ConfigureAwait(false)).Stream,
                UseProxy = false
            });

            _uri = new Uri("http://localhost/");
            _encodedAuthority = Encoding.ASCII.GetBytes(_uri.IdnHost);
            _encodedPathAndQuery = Encoding.ASCII.GetBytes("/");
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            _httpClient!.Dispose();
            _primitiveConnection!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _connection!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _server!.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _connectionListener!.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        [Benchmark]
        public Task Primitive() =>
            Primitive(RequestHeaders!.EncodedAllHeaders, preparedHeaders: null);

        [Benchmark]
        public Task PrimitivePrepared() =>
            Primitive(RequestHeaders!.EncodedDynamicHeaders, RequestHeaders!.PreparedHeaders);

        private async Task Primitive(List<(byte[], byte[])>? encodedHeaders, PreparedHeaderSet? preparedHeaders)
        {
            ValueHttpRequest request = (await _primitiveConnection!.CreateNewRequestAsync(HttpPrimitiveVersion.Version11, HttpVersionPolicy.RequestVersionExact).ConfigureAwait(false)).Value;
            await using (request.ConfigureAwait(false))
            {
                request.ConfigureRequest(contentLength: 0, hasTrailingHeaders: false);
                request.WriteRequest(HttpRequest.GetMethod, _encodedAuthority!, _encodedPathAndQuery!);

                if (encodedHeaders != null)
                {
                    foreach ((byte[] name, byte[] value) in encodedHeaders)
                    {
                        request.WriteHeader(name, value);
                    }
                }

                if (preparedHeaders != null)
                {
                    request.WriteHeader(preparedHeaders);
                }

                await request.CompleteRequestAsync().ConfigureAwait(false);

                while (await request.ReadAsync().ConfigureAwait(false) != HttpReadType.EndOfStream)
                {
                    // skip everything.
                }
            }
        }

        [Benchmark(Baseline = true)]
        public async Task SocketsHandler()
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, _uri!);

            if (RequestHeaders!.StaticHeaders is List<(string, string)> staticHeaders)
            {
                foreach ((string name, string value) in staticHeaders)
                {
                    requestMessage.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (RequestHeaders!.DynamicHeaders is List<(string, string)> dynamicHeaders)
            {
                foreach ((string name, string value) in dynamicHeaders)
                {
                    requestMessage.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var responseMessage = await _httpClient!.SendAsync(requestMessage, CancellationToken.None).ConfigureAwait(false);
        }

        public sealed class RequestHeaderCollection
        {
            public string Name { get; }
            public List<(string,string)>? StaticHeaders { get; init; }
            public List<(string,string)>? DynamicHeaders { get; init; }

            public PreparedHeaderSet? PreparedHeaders { get; private set; }
            public List<(byte[], byte[])>? EncodedDynamicHeaders { get; private set; }
            public List<(byte[], byte[])>? EncodedAllHeaders { get; private set; }

            public RequestHeaderCollection(string name)
            {
                Name = name;
            }

            public override string ToString() => Name;

            public RequestHeaderCollection Build()
            {
                if (StaticHeaders is List<(string, string)> staticHeaders)
                {
                    EncodedAllHeaders ??= new List<(byte[], byte[])>();

                    var builder = new PreparedHeaderSetBuilder();
                    foreach ((string name, string value) in staticHeaders)
                    {
                        EncodedAllHeaders.Add((Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value)));
                        builder.AddHeader(name, value);
                    }

                    PreparedHeaders = builder.Build();
                }

                if (DynamicHeaders is List<(string, string)> dynamicHeaders)
                {
                    EncodedAllHeaders ??= new List<(byte[], byte[])>();
                    EncodedDynamicHeaders = new List<(byte[], byte[])>();

                    foreach ((string name, string value) in dynamicHeaders)
                    {
                        EncodedAllHeaders.Add((Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value)));
                        EncodedDynamicHeaders.Add((Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value)));
                    }
                }

                return this;
            }
        }

        public sealed record EncodedMessage(string Name, byte[] Response)
        {
            public EncodedMessage(string name, string response)
                : this(name, Encoding.ASCII.GetBytes(response))
            {
            }

            public override string ToString() => Name;
        }
    }
}
