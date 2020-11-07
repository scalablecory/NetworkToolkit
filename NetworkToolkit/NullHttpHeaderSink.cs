using NetworkToolkit.Http.Primitives;
using System;

namespace NetworkToolkit
{
    internal sealed class NullHttpHeaderSink : IHttpHeadersSink
    {
        public static readonly NullHttpHeaderSink Instance = new NullHttpHeaderSink();

        public void OnHeader(object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
        {
        }
    }
}
