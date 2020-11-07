using NetworkToolkit.Http.Primitives;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http
{
    /// <summary>
    /// A <see cref="HttpMessageHandler"/> that operates over <see cref="HttpConnection"/>.
    /// </summary>
    public sealed class PrimitiveHttpMessageHandler : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        private async Task<HttpResponseMessage> SendAsync(HttpConnection connection, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpPrimitiveVersion requestVersion = request.Version switch
            {
                { MajorRevision: 1, MinorRevision: 0 } => HttpPrimitiveVersion.Version10,
                { MajorRevision: 1 } => HttpPrimitiveVersion.Version11,
                _ => throw new ArgumentException($"Unknown HTTP version {request.Version}")
            };

            if (request.RequestUri == null)
            {
                throw new ArgumentException($"{nameof(request)}.{nameof(request.RequestUri)} must not be null.");
            }

            ValueHttpRequest httpRequest = (await connection.CreateNewRequestAsync(requestVersion, request.VersionPolicy, cancellationToken).ConfigureAwait(false))
                ?? throw new HttpRequestException($"{nameof(HttpConnection)} used by {nameof(PrimitiveHttpMessageHandler)} has been closed by peer.");

            try
            {
                long? contentLength =
                    request.Content == null ? 0L :
                    request.Content.Headers.ContentLength is long contentLengthHeader ? contentLengthHeader :
                    null;

                // HttpRequestMessage has no support for sending trailing headers.
                httpRequest.ConfigureRequest(contentLength, hasTrailingHeaders: false);
                httpRequest.WriteRequest(request.Method, request.RequestUri);

                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
                {
                    httpRequest.WriteHeader(header.Key, header.Value, ";");
                }

                PrimitiveHttpContentStream stream = new PrimitiveHttpContentStream(httpRequest);

                if (request.Content != null)
                {
                    await request.Content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                await httpRequest.CompleteRequestAsync(cancellationToken).ConfigureAwait(false);

                if (!await httpRequest.ReadToFinalResponseAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new HttpRequestException("Unexpected end of stream before response received.");
                }

                var responseContent = new StreamContent(stream);

                var response = new PrimitiveHttpResponseMessage
                {
                    StatusCode = httpRequest.StatusCode,
                    Version = httpRequest.Version!,
                    Content = responseContent
                };

                stream.ResponseMessage = response;

                if (await httpRequest.ReadToHeadersAsync(cancellationToken).ConfigureAwait(false))
                {
                    await httpRequest.ReadHeadersAsync(response, state: null, cancellationToken).ConfigureAwait(false);
                }

                return response;
            }
            catch
            {
                await httpRequest.DisposeAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }
}
