using NetworkToolkit.Connections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests.Http.Servers
{
    internal sealed class Http1TestConnection : HttpTestConnection
    {
        private readonly Connection _connection;
        internal readonly Stream _stream;
        internal ArrayBuffer _readBuffer;

        private long? _contentLength;
        private bool _isChunked;

        public Http1TestConnection(Connection connection)
        {
            _connection = connection;
            _stream = connection.Stream;
            _readBuffer = new ArrayBuffer(4096);
        }

        public override Task<HttpTestStream> AcceptStreamAsync()
        {
            return Task.FromResult<HttpTestStream>(new Http1TestStream(this));
        }

        public override ValueTask DisposeAsync()
        {
            _readBuffer.Dispose();
            return _connection.DisposeAsync();
        }

        internal async Task<HttpTestRequest> ReceiveRequestAsync()
        {
            string request = await ReadLineAsync().ConfigureAwait(false);

            Match match = Regex.Match(request, @"^([^ ]+) ([^ ]+) HTTP/(\d).(\d)$");
            if (!match.Success) throw new Exception("Invalid request line.");

            string method = match.Groups[1].Value;
            string pathAndQuery = match.Groups[2].Value;
            int versionMajor = int.Parse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            int versionMinor = int.Parse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            var version = new Version(versionMajor, versionMinor);

            TestHeadersSink headers = await ReadHeadersAsync().ConfigureAwait(false);

            _contentLength =
                headers.TryGetSingleValue("content-length", out string? contentLength) ? int.Parse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture) :
                method switch
                {
                    "GET" or "HEAD" or "DELETE" or "TRACE" => 0,
                    _ => (int?)null
                };
            _isChunked = headers.TryGetSingleValue("transfer-encoding", out string? transferEncoding) && transferEncoding == "chunked";

            return new HttpTestRequest(method, pathAndQuery, version, headers);
        }

        internal Stream ReceiveContentStream() =>
            _isChunked ? new Http1TestChunkedStream(this, _contentLength) :
            _contentLength != null ? new Http1TestContentLengthStream(this, _contentLength.Value) :
            new Http1TestLengthlessStream(this);

        internal async Task<TestHeadersSink> ReceiveTrailingHeadersAsync() =>
            _isChunked ? await ReadHeadersAsync().ConfigureAwait(false) : new TestHeadersSink();

        private async Task<TestHeadersSink> ReadHeadersAsync()
        {
            var headers = new TestHeadersSink();

            string header;
            while ((header = await ReadLineAsync().ConfigureAwait(false)).Length != 0)
            {
                (string headerName, string headerValue) = ParseHeader(header);
                headers.Add(headerName, headerValue);
            }

            return headers;
        }

        private (string headerName, string headerValue) ParseHeader(string header)
        {
            int idx = header.IndexOf(':');
            if (idx == -1) throw new ArgumentException("The value is not a valid header.", nameof(header));

            string headerName = header[..idx];
            string headerValue = header[(idx + 1)..].Trim(' ');
            return (headerName, headerValue);
        }

        internal async Task<string> ReadLineAsync()
        {
            string? line;

            while (!TryReadLine(out line))
            {
                if (!await FillReadBufferAsync().ConfigureAwait(false))
                {
                    throw new Exception("Unexpected end of stream. Expected CRLF.");
                }
            }

            return line;
        }

        private bool TryReadLine([NotNullWhen(true)] out string? line)
        {
            ReadOnlySpan<byte> span = _readBuffer.ActiveSpan;

            int endIdx = span.IndexOf(new[] { (byte)'\r', (byte)'\n' });
            if (endIdx == -1)
            {
                line = null;
                return false;
            }

            line = Encoding.ASCII.GetString(span.Slice(0, endIdx));
            _readBuffer.Discard(endIdx + 2);
            return true;
        }

        internal async Task<bool> FillReadBufferAsync()
        {
            _readBuffer.EnsureAvailableSpace(1);

            int readLen = await _stream.ReadAsync(_readBuffer.AvailableMemory).ConfigureAwait(false);
            if (readLen == 0) return false;

            _readBuffer.Commit(readLen);
            return true;
        }

        internal async Task SendResponseAsync(int statusCode, TestHeadersSink? headers, string? content, IList<string>? chunkedContent, TestHeadersSink? trailingHeaders)
        {
            Debug.Assert(content == null || chunkedContent == null, $"Only one of {nameof(content)} and {nameof(chunkedContent)} can be specified.");

            var newHeaders = new TestHeadersSink();

            if (headers != null)
            {
                foreach (var kvp in headers)
                {
                    foreach (var value in kvp.Value)
                    {
                        newHeaders.Add(kvp.Key, value);
                    }
                }
            }

            bool chunked = chunkedContent != null || trailingHeaders?.Count > 0;
            if (chunked && !newHeaders.ContainsKey("transfer-encoding"))
            {
                newHeaders.Add("transfer-encoding", "chunked");
            }

            if (!newHeaders.ContainsKey("content-length"))
            {
                int contentLength = content?.Length ?? chunkedContent?.Sum(x => (int?)x.Length) ?? 0;
                newHeaders.Add("content-length", contentLength.ToString(CultureInfo.InvariantCulture));
            }

            using var writer = new StreamWriter(_stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = false };

            await writer.WriteAsync("HTTP/1.1 ").ConfigureAwait(false);
            await writer.WriteAsync(statusCode.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await writer.WriteAsync(" TODOStatusMessage\r\n").ConfigureAwait(false);
            await WriteHeadersAsync(writer, newHeaders).ConfigureAwait(false);

            if (chunked)
            {
                if (chunkedContent != null)
                {
                    foreach (string chunk in chunkedContent)
                    {
                        await WriteChunkAsync(chunk).ConfigureAwait(false);
                    }
                }
                else if (content?.Length > 0)
                {
                    await WriteChunkAsync(content).ConfigureAwait(false);
                }

                await writer.WriteAsync("0\r\n").ConfigureAwait(false);
                await WriteHeadersAsync(writer, trailingHeaders).ConfigureAwait(false);
            }
            else if (content?.Length > 0)
            {
                await writer.WriteAsync(content).ConfigureAwait(false);
            }

            await writer.FlushAsync().ConfigureAwait(false);

            async Task WriteChunkAsync(string chunk)
            {
                Debug.Assert(chunk.Length != 0, "Content chunks must be >0 in length.");
                await writer.WriteAsync(chunk.Length.ToString("X", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await writer.WriteAsync("\r\n").ConfigureAwait(false);
                await writer.WriteAsync(chunk).ConfigureAwait(false);
                await writer.WriteAsync("\r\n").ConfigureAwait(false);
            }
        }

        internal async Task SendRawResponseAsync(string response)
        {
            await _stream.WriteAsync(Encoding.UTF8.GetBytes(response)).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        static async Task WriteHeadersAsync(StreamWriter writer, TestHeadersSink? headers)
        {
            if (headers != null)
            {
                foreach (var kvp in headers)
                {
                    foreach (var value in kvp.Value)
                    {
                        await writer.WriteAsync(kvp.Key).ConfigureAwait(false);
                        await writer.WriteAsync(": ").ConfigureAwait(false);
                        await writer.WriteAsync(value).ConfigureAwait(false);
                        await writer.WriteAsync("\r\n").ConfigureAwait(false);
                    }
                }
            }
            await writer.WriteAsync("\r\n").ConfigureAwait(false);
        }
    }
}
