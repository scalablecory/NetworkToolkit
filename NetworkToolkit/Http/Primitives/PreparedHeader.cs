using System;
using System.Text;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A prepared header.
    /// </summary>
    public sealed class PreparedHeader
    {
        internal readonly byte[] _headerName, _headerValue;

        /// <summary>
        /// Instantiates a new <see cref="PreparedHeader"/>.
        /// </summary>
        /// <param name="headerName">The header's name.</param>
        /// <param name="headerValue">The header's value.</param>
        public PreparedHeader(ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
        {
            _headerName = headerName.ToArray();
            _headerValue = headerValue.ToArray();
        }

        /// <summary>
        /// Instantiates a new <see cref="PreparedHeader"/>.
        /// </summary>
        /// <param name="headerName">The header's name.</param>
        /// <param name="headerValue">The header's value.</param>
        public PreparedHeader(string headerName, string headerValue)
        {
            _headerName = Encoding.ASCII.GetBytes(headerName);
            _headerValue = Encoding.ASCII.GetBytes(headerValue);
        }

        /// <inheritdoc/>
        public override string ToString() => Encoding.ASCII.GetString(_headerName) + ": " + Encoding.ASCII.GetString(_headerValue);
    }
}
