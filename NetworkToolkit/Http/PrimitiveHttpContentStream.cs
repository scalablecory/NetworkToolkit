using NetworkToolkit.Http.Primitives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http
{
    internal sealed class PrimitiveHttpContentStream : HttpContentStream
    {
        public PrimitiveHttpResponseMessage? ResponseMessage { get; set; }

        public PrimitiveHttpContentStream(ValueHttpRequest request) : base(request, ownsRequest: true)
        {
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int len = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (len == 0 && ResponseMessage is PrimitiveHttpResponseMessage response)
            {
                if (await _request.ReadToTrailingHeadersAsync(cancellationToken).ConfigureAwait(false))
                {
                    await _request.ReadHeadersAsync(response, PrimitiveHttpResponseMessage.TrailingHeadersSinkState, cancellationToken).ConfigureAwait(false);
                }

                ResponseMessage = null;
                await _request.DisposeAsync(cancellationToken).ConfigureAwait(false);
            }

            return len;
        }
    }
}
