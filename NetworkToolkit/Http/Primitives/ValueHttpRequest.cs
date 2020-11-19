using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A HTTP request.
    /// </summary>
    public struct ValueHttpRequest : ICancellableAsyncDisposable
    {
        private HttpRequest _request;
        private int _requestVersion;

        internal HttpRequest Request => _request;

        /// <summary>
        /// The local <see cref="EndPoint"/> the <see cref="HttpRequest"/> is being made from.
        /// </summary>
        public EndPoint? LocalEndPoint =>
            _request.LocalEndPoint;

        /// <summary>
        /// The remote <see cref="EndPoint"/> the <see cref="HttpRequest"/> is connected to.
        /// </summary>
        public EndPoint? RemoteEndPoint =>
            _request.RemoteEndPoint;

        /// <summary>
        /// The current <see cref="HttpReadType"/> for the request.
        /// </summary>
        public HttpReadType ReadType =>
            _request.ReadType;

        /// <summary>
        /// The version of the HTTP response.
        /// Only valid when <see cref="ReadType"/> has been <see cref="HttpReadType.Response"/>.
        /// </summary>
        public Version? Version =>
            _request.Version;

        /// <summary>
        /// The status code of the HTTP response.
        /// Only valid when <see cref="ReadType"/> has been <see cref="HttpReadType.Response"/>.
        /// </summary>
        public HttpStatusCode StatusCode =>
            _request.StatusCode;

        /// <summary>
        /// The ALT-SVC value from the HTTP response.
        /// Only valid when <see cref="ReadType"/> has been <see cref="HttpReadType.AltSvc"/>.
        /// </summary>
        public ReadOnlyMemory<byte> AltSvc =>
            _request.AltSvc;

        /// <summary>
        /// Configures the request.
        /// </summary>
        /// <param name="contentLength">If specified, the known length of the request content that will be written.</param>
        /// <param name="hasTrailingHeaders">If true, the request will send trailing headers.</param>
        public void ConfigureRequest(long? contentLength, bool hasTrailingHeaders) =>
            _request.ConfigureRequest(_requestVersion, contentLength, hasTrailingHeaders);

        /// <summary>
        /// Writes a CONNECT request.
        /// </summary>
        /// <param name="authority">The authority to CONNECT to.</param>
        public void WriteConnectRequest(ReadOnlySpan<byte> authority) =>
            _request.WriteConnectRequest(_requestVersion, authority);

        /// <summary>
        /// Writes a request.
        /// </summary>
        /// <param name="method">The request method to use.</param>
        /// <param name="scheme">The request's scheme. Should generally be "http" or "https".</param>
        /// <param name="authority">The authority that should process the request. Ends up in the "Host" or ":authority" header, depending on protocol version.</param>
        /// <param name="pathAndQuery">The path and query of the request.</param>
        public void WriteRequest(ReadOnlySpan<byte> method, ReadOnlySpan<byte> scheme, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery) =>
            _request.WriteRequest(_requestVersion, method, scheme, authority, pathAndQuery);

        /// <summary>
        /// Writes a request.
        /// </summary>
        /// <param name="method">The request method to use.</param>
        /// <param name="uri">The URI to make the request for.</param>
        public void WriteRequest(HttpMethod method, Uri uri) =>
            _request.WriteRequest(_requestVersion, method, uri);

        /// <summary>
        /// Writes a header.
        /// </summary>
        /// <param name="name">The name of the header to write.</param>
        /// <param name="value">The value of the header to write.</param>
        public void WriteHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
            _request.WriteHeader(_requestVersion, name, value);

        /// <summary>
        /// Writes a set of headers.
        /// </summary>
        /// <param name="headers">A set of headers to write to the request.</param>
        public void WriteHeader(PreparedHeaderSet headers) =>
            _request.WriteHeader(_requestVersion, headers);

        /// <summary>
        /// Writes a header.
        /// </summary>
        /// <param name="name">The name of the header to write.</param>
        /// <param name="value">The value of the header to write.</param>
        public void WriteHeader(string name, string value) =>
            _request.WriteHeader(_requestVersion, name, value);

        /// <summary>
        /// Writes a header.
        /// </summary>
        /// <param name="name">The name of the header to write.</param>
        /// <param name="values">The value of the header to write.</param>
        /// <param name="separator">A separator used when concatenating <paramref name="values"/>.</param>
        public void WriteHeader(string name, IEnumerable<string> values, string separator) =>
            _request.WriteHeader(_requestVersion, name, values, separator);

        /// <summary>
        /// Writes a trailing header.
        /// To use, trailing headers must be enabled during <see cref="ConfigureRequest(long?, bool)"/>.
        /// </summary>
        /// <param name="name">The name of the header to write.</param>
        /// <param name="value">The value of the header to write.</param>
        public void WriteTrailingHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) =>
            _request.WriteTrailingHeader(_requestVersion, name, value);

        /// <summary>
        /// Writes a trailing header.
        /// To use, trailing headers must be enabled during <see cref="ConfigureRequest(long?, bool)"/>.
        /// </summary>
        /// <param name="name">The name of the header to write.</param>
        /// <param name="value">The value of the header to write.</param>
        public void WriteTrailingHeader(string name, string value) =>
            _request.WriteTrailingHeader(_requestVersion, name, value);

        /// <summary>
        /// Writes a trailing header.
        /// To use, trailing headers must be enabled during <see cref="ConfigureRequest(long?, bool)"/>.
        /// </summary>
        /// <param name="name">The name of the header to write.</param>
        /// <param name="values">The value of the header to write.</param>
        /// <param name="separator">A separator used when concatenating <paramref name="values"/>.</param>
        public void WriteTrailingHeader(string name, IEnumerable<string> values, string separator) =>
            _request.WriteTrailingHeader(_requestVersion, name, values, separator);

        /// <summary>
        /// Flushes the request and headers to network, if any.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public ValueTask FlushHeadersAsync(CancellationToken cancellationToken = default) =>
            _request.FlushHeadersAsync(_requestVersion, cancellationToken);

        /// <summary>
        /// Writes request content.
        /// </summary>
        /// <param name="buffer">The request content to write.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public ValueTask WriteContentAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _request.WriteContentAsync(_requestVersion, buffer, cancellationToken);

        /// <summary>
        /// Writes request content.
        /// </summary>
        /// <param name="buffers">The request content to write.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public ValueTask WriteContentAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default) =>
            _request.WriteContentAsync(_requestVersion, buffers, cancellationToken);

        /// <summary>
        /// Flushes the request, headers, and request content to network, if any.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public ValueTask FlushContentAsync(CancellationToken cancellationToken = default) =>
            _request.FlushContentAsync(_requestVersion, cancellationToken);

        /// <summary>
        /// Completes the request, flushing the request, headers, request content, and trailing headers to network, if any.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public ValueTask CompleteRequestAsync(CancellationToken cancellationToken = default) =>
            _request.CompleteRequestAsync(_requestVersion, cancellationToken);

        /// <summary>
        /// Reads the next element from the HTTP response stream.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="HttpReadType"/> indicating the type of element read from the stream.</returns>
        public ValueTask<HttpReadType> ReadAsync(CancellationToken cancellationToken = default) =>
            _request.ReadAsync(_requestVersion, cancellationToken);

        /// <summary>
        /// Reads headers, if any. Should be called when <see cref="ReadType"/> is <see cref="HttpReadType.Headers"/> or <see cref="HttpReadType.TrailingHeaders"/>.
        /// </summary>
        /// <param name="headersSink">A sink to retrieve headers.</param>
        /// <param name="state">User state to pass to <see cref="IHttpHeadersSink.OnHeader(object?, ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="HttpReadType"/> indicating the type of element read from the stream.</returns>
        public ValueTask ReadHeadersAsync(IHttpHeadersSink headersSink, object? state, CancellationToken cancellationToken = default) =>
            _request.ReadHeadersAsync(_requestVersion, headersSink, state, cancellationToken);

        /// <summary>
        /// Reads response content, if any. Should be called when <see cref="ReadType"/> is <see cref="HttpReadType.Content"/>.
        /// </summary>
        /// <param name="buffer">The buffer to read into.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>The number of bytes read into <paramref name="buffer"/>.</returns>
        public ValueTask<int> ReadContentAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _request.ReadContentAsync(_requestVersion, buffer, cancellationToken);

        /// <summary>
        /// Reads until a <see cref="HttpReadType.Response"/> is encountered.
        /// It is possible to have more than one response, when informational responses are returned. Expect to call <see cref="ReadToNextResponseAsync(CancellationToken)"/> in this case.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If a response was found before end of stream, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> ReadToResponseAsync(CancellationToken cancellationToken = default)
        {
            HttpReadType readType = _request.ReadType;

            while (true)
            {
                switch (readType)
                {
                    case HttpReadType.Response:
                        return true;
                    case HttpReadType.Headers:
                    case HttpReadType.Content:
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
                readType = await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads until a subsequent <see cref="HttpReadType.Response"/> is encountered.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If a response was found before end of stream, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> ReadToNextResponseAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                switch (await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false))
                {
                    case HttpReadType.Response:
                        return true;
                    case HttpReadType.Headers:
                    case HttpReadType.Content:
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
            }
        }

        /// <summary>
        /// Reads until the final <see cref="HttpReadType.Response"/> is encountered, skipping any informational responses.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If a response was found before end of stream, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> ReadToFinalResponseAsync(CancellationToken cancellationToken = default)
        {
            HttpReadType readType = _request.ReadType;

            while (true)
            {
                switch (readType)
                {
                    case HttpReadType.Response:
                        if((int)_request.StatusCode >= 200) return true;
                        break;
                    case HttpReadType.Headers:
                    case HttpReadType.Content:
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
                readType = await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads until a <see cref="HttpReadType.Headers"/> is encountered.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If headers were found before end of stream, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> ReadToHeadersAsync(CancellationToken cancellationToken = default)
        {
            HttpReadType readType = _request.ReadType;

            while (true)
            {
                switch (readType)
                {
                    case HttpReadType.Headers:
                        return true;
                    case HttpReadType.Content:
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
                readType = await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads until a <see cref="HttpReadType.Content"/> is encountered.
        /// It is possible to have more than one content element, with other element types interleaved between them. Expect to call <see cref="ReadToNextContentAsync(CancellationToken)"/> in this case.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If content was found before end of stream, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> ReadToContentAsync(CancellationToken cancellationToken = default)
        {
            HttpReadType readType = _request.ReadType;
            while (true)
            {
                switch (readType)
                {
                    case HttpReadType.Content:
                        return true;
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
                readType = await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads until a <see cref="HttpReadType.Content"/> is encountered.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If content was found before end of stream, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> ReadToNextContentAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                switch (await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false))
                {
                    case HttpReadType.Content:
                        return true;
                    case HttpReadType.TrailingHeaders:
                    case HttpReadType.EndOfStream:
                        return false;
                }
            }
        }

        /// <summary>
        /// Reads until a <see cref="HttpReadType.TrailingHeaders"/> is encountered.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If trailing headers were found before end of stream, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> ReadToTrailingHeadersAsync(CancellationToken cancellationToken = default)
        {
            HttpReadType readType = _request.ReadType;
            while (true)
            {
                switch (readType)
                {
                    case HttpReadType.TrailingHeaders:
                        return true;
                    case HttpReadType.EndOfStream:
                        return false;
                }
                readType = await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Drains the response until <see cref="HttpReadType.EndOfStream"/> is reached.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
        {
            HttpReadType currentReadType = ReadType;
            while (currentReadType != HttpReadType.EndOfStream)
            {
                currentReadType = await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Drains the response until <see cref="HttpReadType.EndOfStream"/> or a maximum amount of content is read.
        /// </summary>
        /// <param name="maximumContentSize">The maximum content to drain before returning false.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If the request was fully drained, true.
        /// Otherwise, false.
        /// </returns>
        public async ValueTask<bool> DrainAsync(int maximumContentSize, CancellationToken cancellationToken = default)
        {
            byte[]? readBuffer = null;

            HttpReadType readType = _request.ReadType;
            while (true)
            {
                switch (readType)
                {
                    case HttpReadType.Content:
                        readBuffer ??= ArrayPool<byte>.Shared.Rent(8192);

                        int readLen;
                        while ((readLen = await _request.ReadContentAsync(_requestVersion, readBuffer, cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            maximumContentSize -= readLen;
                            if (maximumContentSize < 0) return false;
                        }
                        break;
                    case HttpReadType.EndOfStream:
                        if (readBuffer != null) ArrayPool<byte>.Shared.Return(readBuffer);
                        return true;
                }
                readType = await _request.ReadAsync(_requestVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Instantiates a new <see cref="ValueHttpRequest"/>.
        /// </summary>
        /// <param name="request">The request to use.</param>
        /// <param name="requestVersion">The version of the request this <see cref="ValueHttpRequest"/> should use.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueHttpRequest(HttpRequest request, int requestVersion)
        {
            _request = request;
            _requestVersion = requestVersion;
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync() =>
            DisposeAsync(CancellationToken.None);

        /// <inheritdoc/>
        public ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            if (_request is HttpRequest request)
            {
                _request = null!;
                return request.DisposeAsync(_requestVersion, cancellationToken);
            }

            return default;
        }
    }
}
