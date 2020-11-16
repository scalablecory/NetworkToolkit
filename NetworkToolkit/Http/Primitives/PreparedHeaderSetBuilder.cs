using System;
using System.Collections.Generic;
using System.Text;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A builder used to create a <see cref="PreparedHeaderSet"/>.
    /// </summary>
    public sealed class PreparedHeaderSetBuilder
    {
        private readonly List<PreparedHeader> _headers = new List<PreparedHeader>();

        /// <summary>
        /// Adds a header to the builder.
        /// </summary>
        /// <param name="header">The header to add.</param>
        public PreparedHeaderSetBuilder AddHeader(PreparedHeader header)
        {
            _headers.Add(header);
            return this;
        }

        /// <summary>
        /// Adds a header to the builder.
        /// </summary>
        /// <param name="name">The name of the header to add.</param>
        /// <param name="value">The value of the header to add.</param>
        public PreparedHeaderSetBuilder AddHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            _headers.Add(new PreparedHeader(name, value));
            return this;
        }

        /// <summary>
        /// Adds a header to the builder.
        /// </summary>
        /// <param name="name">The name of the header to add.</param>
        /// <param name="value">The value of the header to add.</param>
        public PreparedHeaderSetBuilder AddHeader(string name, string value)
        {
            _headers.Add(new PreparedHeader(name, value));
            return this;
        }

        /// <summary>
        /// Builds a <see cref="PreparedHeaderSet"/>.
        /// </summary>
        /// <returns>A <see cref="PreparedHeaderSet"/> instance representing the given headers</returns>
        public PreparedHeaderSet Build() => new PreparedHeaderSet(_headers);
    }
}
