using NetworkToolkit.Http.Primitives;
using System;
using System.Net.Http;
using System.Text;

namespace NetworkToolkit.Http
{
    internal sealed class PrimitiveHttpResponseMessage : HttpResponseMessage, IHttpHeadersSink
    {
        public static readonly object TrailingHeadersSinkState = new object();

        public void OnHeader(object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
        {
            string headerNameString = Encoding.ASCII.GetString(headerName);
            string headerValueString = Encoding.ASCII.GetString(headerValue);

            if (state != TrailingHeaders)
            {
                if (!Headers.TryAddWithoutValidation(headerNameString, headerValueString))
                {
                    Content.Headers.TryAddWithoutValidation(headerNameString, headerValueString);
                }
            }
            else
            {
                TrailingHeaders.TryAddWithoutValidation(headerNameString, headerValueString);
            }
        }
    }
}
