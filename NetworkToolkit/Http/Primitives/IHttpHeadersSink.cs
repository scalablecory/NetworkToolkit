﻿using System;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A sink used to receive HTTP headers.
    /// </summary>
    public interface IHttpHeadersSink
    {
        /// <summary>
        /// Called when a header has been received.
        /// </summary>
        /// <param name="state">User state passed to <see cref="ValueHttpRequest.ReadHeadersAsync(IHttpHeadersSink, object?, System.Threading.CancellationToken)"/>.</param>
        /// <param name="headerName">The header's name.</param>
        /// <param name="headerValue">The header's value.</param>
        void OnHeader(object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue);

        /// <summary>
        /// Called when a header has been received.
        /// </summary>
        /// <param name="state">User state passed to <see cref="ValueHttpRequest.ReadHeadersAsync(IHttpHeadersSink, object?, System.Threading.CancellationToken)"/>.</param>
        /// <param name="headerName">The header's name.</param>
        /// <param name="headerValue">The header's value.</param>
        /// <param name="flags">Flags for the header.</param>
        void OnHeader(object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue, HttpHeaderFlags flags)
        {
            if (flags.HasFlag(HttpHeaderFlags.NameHuffmanCoded))
            {
                // TODO: decode header name.
            }

            if (flags.HasFlag(HttpHeaderFlags.ValueHuffmanCoded))
            {
                // TODO: decode header value.
            }

            OnHeader(state, headerName, headerValue);
        }
    }
}
