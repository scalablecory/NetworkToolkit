using BenchmarkDotNet.Attributes;
using NetworkToolkit.Http.Primitives;
using System;
using System.Collections.Generic;
using System.Text;
using Internal = NetworkToolkitInternal.Http.Primitives;

namespace NetworkToolkit.Benchmarks
{
    [Config(typeof(NetworkToolkitConfig))]
    public class HeaderParsing
    {
        private NullHeaderSink _sink = new NullHeaderSink();
        private readonly Internal.Http1Connection _connection = new Internal.Http1Connection();

        [ParamsSource(nameof(ValuesForBuffer))]
        public PaddedHeader? Buffer { get; set; }

        [Benchmark]
        public void Portable()
        {
            _connection.ReadHeadersPortable(Buffer!.GetSpan(), _sink, state: null, out _);
        }

        [Benchmark]
        public void AVX2()
        {
            _connection.ReadHeadersAvx2(Buffer!.GetSpan(), _sink, state: null, out _);
        }

        public static IEnumerable<PaddedHeader> ValuesForBuffer => new[]
        {
            new PaddedHeader("Minimal",
                "Content-Length: 0\r\n\r\n"),
            new PaddedHeader("StackOverflow",
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

        public sealed class PaddedHeader
        {
            private readonly string _name;
            private readonly byte[] _buffer;

            public PaddedHeader(string name, string headers)
            {
                _name = name;

                byte[] buffer = Encoding.ASCII.GetBytes(headers);
                byte[] padded = new byte[headers.Length + Internal.Http1Connection.HeaderBufferPadding * 2];
                buffer.CopyTo(padded, Internal.Http1Connection.HeaderBufferPadding);
                _buffer = padded;
            }

            public Span<byte> GetSpan() => _buffer.AsSpan(Internal.Http1Connection.HeaderBufferPadding, _buffer.Length - Internal.Http1Connection.HeaderBufferPadding);
            public override string ToString() => _name;
        }

        sealed class NullHeaderSink : IHttpHeadersSink
        {
            public void OnHeader(object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
            {
            }
        }
    }
}
