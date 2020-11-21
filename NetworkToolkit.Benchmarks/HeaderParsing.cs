using BenchmarkDotNet.Attributes;
using NetworkToolkit.Http.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Internal = NetworkToolkitInternal.Http.Primitives;

namespace NetworkToolkit.Benchmarks
{
    [Config(typeof(NetworkToolkitConfig))]
    public class HeaderParsing
    {
        private NullHeaderSink _sink = new NullHeaderSink();
        private readonly Internal.Http1Connection _connection = new Internal.Http1Connection();

        // SocketsHttpHandler state.
        private byte[] _readBuffer = null!;
        private int _readLength, _readOffset, _allowedReadLineBytes;

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

        [IterationSetup(Target = nameof(SocketsHandler))]
        public void IterationSetup()
        {
            _readBuffer = Buffer!.GetArray();
            _readLength = _readBuffer.Length;
            _readOffset = 0;
            _allowedReadLineBytes = int.MaxValue;
        }

        [Benchmark]
        public void SocketsHandler()
        {
            while (true)
            {
                ReadOnlyMemory<byte> line = SocketsHandler_ReadNextResponseHeaderLine();
                if (line.Length == 0) break;
                ParseHeaderNameValue(line.Span, _sink, null);
            }
        }

        private static void ParseHeaderNameValue(ReadOnlySpan<byte> line, IHttpHeadersSink sink, object? state)
        {
            Debug.Assert(line.Length > 0);

            int pos = 0;
            while (line[pos] != (byte)':' && line[pos] != (byte)' ')
            {
                pos++;
                if (pos == line.Length)
                {
                    // Invalid header line that doesn't contain ':'.
                    throw new Exception();
                }
            }

            if (pos == 0)
            {
                // Invalid empty header name.
                throw new Exception();
            }

            ReadOnlySpan<byte> name = line.Slice(0, pos);

            // Eat any trailing whitespace
            while (line[pos] == (byte)' ')
            {
                pos++;
                if (pos == line.Length)
                {
                    // Invalid header line that doesn't contain ':'.
                    throw new Exception();
                }
            }

            if (line[pos++] != ':')
            {
                // Invalid header line that doesn't contain ':'.
                throw new Exception();
            }

            // Skip whitespace after colon
            while (pos < line.Length && (line[pos] == (byte)' ' || line[pos] == (byte)'\t'))
            {
                pos++;
            }

            // Note we ignore the return value from TryAddWithoutValidation. If the header can't be added, we silently drop it.
            ReadOnlySpan<byte> value = line.Slice(pos);

            sink.OnHeader(state, name, value);
        }

        private ReadOnlyMemory<byte> SocketsHandler_ReadNextResponseHeaderLine()
        {
            int previouslyScannedBytes = 0;
            while (true)
            {
                int scanOffset = _readOffset + previouslyScannedBytes;
                int lfIndex = Array.IndexOf(_readBuffer, (byte)'\n', scanOffset, _readLength - scanOffset);
                if (lfIndex >= 0)
                {
                    int startIndex = _readOffset;
                    int length = lfIndex - startIndex;
                    if (lfIndex > 0 && _readBuffer[lfIndex - 1] == '\r')
                    {
                        length--;
                    }

                    // If this isn't the ending header, we need to account for the possibility
                    // of folded headers, which per RFC2616 are headers split across multiple
                    // lines, where the continuation line begins with a space or horizontal tab.
                    // The feature was deprecated in RFC 7230 3.2.4, but some servers still use it.
                    if (length > 0)
                    {
                        // If the newline is the last character we've buffered, we need at least
                        // one more character in order to see whether it's space/tab, in which
                        // case it's a folded header.
                        if (lfIndex + 1 == _readLength)
                        {
                            // The LF is at the end of the buffer, so we need to read more
                            // to determine whether there's a continuation.  We'll read
                            // and then loop back around again, but to avoid needing to
                            // rescan the whole header, reposition to one character before
                            // the newline so that we'll find it quickly.
                            int backPos = _readBuffer[lfIndex - 1] == '\r' ? lfIndex - 2 : lfIndex - 1;
                            Debug.Assert(backPos >= 0);
                            previouslyScannedBytes = backPos - _readOffset;
                            _allowedReadLineBytes -= backPos - scanOffset;
                            SocketsHandler_ThrowIfExceededAllowedReadLineBytes();
                            continue;
                        }

                        // We have at least one more character we can look at.
                        Debug.Assert(lfIndex + 1 < _readLength);
                        char nextChar = (char)_readBuffer[lfIndex + 1];
                        if (nextChar == ' ' || nextChar == '\t')
                        {
                            // The next header is a continuation.

                            // Folded headers are only allowed within header field values, not within header field names,
                            // so if we haven't seen a colon, this is invalid.
                            if (Array.IndexOf(_readBuffer, (byte)':', _readOffset, lfIndex - _readOffset) == -1)
                            {
                                throw new Exception();
                            }

                            // When we return the line, we need the interim newlines filtered out. According
                            // to RFC 7230 3.2.4, a valid approach to dealing with them is to "replace each
                            // received obs-fold with one or more SP octets prior to interpreting the field
                            // value or forwarding the message downstream", so that's what we do.
                            _readBuffer[lfIndex] = (byte)' ';
                            if (_readBuffer[lfIndex - 1] == '\r')
                            {
                                _readBuffer[lfIndex - 1] = (byte)' ';
                            }

                            // Update how much we've read, and simply go back to search for the next newline.
                            previouslyScannedBytes = (lfIndex + 1 - _readOffset);
                            _allowedReadLineBytes -= (lfIndex + 1 - scanOffset);
                            SocketsHandler_ThrowIfExceededAllowedReadLineBytes();
                            continue;
                        }

                        // Not at the end of a header with a continuation.
                    }

                    // Advance read position past the LF
                    _allowedReadLineBytes -= lfIndex + 1 - scanOffset;
                    SocketsHandler_ThrowIfExceededAllowedReadLineBytes();
                    _readOffset = lfIndex + 1;

                    return new ReadOnlyMemory<byte>(_readBuffer, startIndex, length);
                }

                // Couldn't find LF.  Read more. Note this may cause _readOffset to change.
                previouslyScannedBytes = _readLength - _readOffset;
                _allowedReadLineBytes -= _readLength - scanOffset;
            }
        }

        private void SocketsHandler_ThrowIfExceededAllowedReadLineBytes()
        {
            if (_allowedReadLineBytes < 0)
            {
                throw new Exception();
            }
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
            private readonly byte[] _unpadded;

            public PaddedHeader(string name, string headers)
            {
                _name = name;
                _unpadded = Encoding.ASCII.GetBytes(headers);
                byte[] padded = new byte[headers.Length + Internal.Http1Connection.HeaderBufferPadding * 2];
                _unpadded.CopyTo(padded, Internal.Http1Connection.HeaderBufferPadding);
                _buffer = padded;
            }

            public byte[] GetArray() => _unpadded;
            public Span<byte> GetSpan() => _buffer.AsSpan(Internal.Http1Connection.HeaderBufferPadding, _buffer.Length - Internal.Http1Connection.HeaderBufferPadding);
            public override string ToString() => _name;
        }

        sealed class NullHeaderSink : IHttpHeadersSink
        {
            public void OnHeader(object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
            {
            }
        }

        sealed class PrintHeaderSink : IHttpHeadersSink
        {
            public static readonly PrintHeaderSink Instance = new PrintHeaderSink();
            public void OnHeader(object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
            {
                Console.WriteLine($"{Encoding.ASCII.GetString(headerName)}: {Encoding.ASCII.GetString(headerValue)}");
            }
        }
    }
}
