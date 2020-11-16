using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A set of prepared headers, used to more efficiently write frequently reused headers.
    /// </summary>
    public sealed class PreparedHeaderSet
    {
        readonly PreparedHeader[] _headers;

        byte[]? _http1Value;
        internal byte[] Http1Value => _http1Value ?? GetHttp1ValueSlow();

        internal PreparedHeaderSet(List<PreparedHeader> headers)
        {
            _headers = headers.ToArray();
        }

        /// <inheritdoc/>
        public override string ToString() => Encoding.ASCII.GetString(Http1Value);

        private byte[] GetHttp1ValueSlow()
        {
            int totalLen = 0;
            foreach (PreparedHeader header in _headers)
            {
                int headerLen = Http1Connection.GetEncodeHeaderLength(header._headerName, header._headerValue);
                totalLen = checked(totalLen + headerLen);
            }

            var buffer = new byte[totalLen];
            Span<byte> writePos = buffer;

            foreach (PreparedHeader header in _headers)
            {
                int len = Http1Connection.EncodeHeader(header._headerName, header._headerValue, writePos);
                writePos = writePos.Slice(len);
            }

            Volatile.Write(ref _http1Value, buffer);
            return buffer;
        }
    }
}
