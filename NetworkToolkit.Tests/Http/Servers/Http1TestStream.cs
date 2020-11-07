using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests.Servers
{
    internal sealed class Http1TestStream : HttpTestStream
    {
        private readonly Http1TestConnection _connection;

        public Http1TestStream(Http1TestConnection connection)
        {
            _connection = connection;
        }

        public override ValueTask DisposeAsync() =>
            default;

        public override Task<HttpTestRequest> ReceiveRequestAsync() =>
            _connection.ReceiveRequestAsync();

        public override Stream ReceiveContentStream() =>
            _connection.ReceiveContentStream();

        public override Task<TestHeadersSink> ReceiveTrailingHeadersAsync() =>
            _connection.ReceiveTrailingHeadersAsync();

        public override Task SendResponseAsync(int statusCode = 200, TestHeadersSink? headers = null, string? content = null, TestHeadersSink? trailingHeaders = null) =>
            _connection.SendResponseAsync(statusCode, headers, content, chunkedContent: null, trailingHeaders);

        public override Task SendChunkedResponseAsync(int statusCode = 200, TestHeadersSink? headers = null, IList<string>? content = null, TestHeadersSink? trailingHeaders = null) =>
            _connection.SendResponseAsync(statusCode, headers, content: null, chunkedContent: content, trailingHeaders);

        public Task SendRawResponseAsync(string response) =>
            _connection.SendRawResponseAsync(response);
    }
}
