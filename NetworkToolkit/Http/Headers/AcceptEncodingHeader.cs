namespace NetworkToolkit.Http.Headers
{
    /// <summary>
    /// The Accept-Encoding header.
    /// </summary>
    public sealed class AcceptEncodingHeader : PreparedHeaderName
    {
        internal AcceptEncodingHeader()
            : base("Accept-Encoding", http2StaticIndex: 16)
        {
            GzipDeflate = new PreparedHeader(this, "gzip, deflate", http2StaticIndex: 16);
        }

        /// <summary>
        /// The "Accept-Encoding: gzip, deflate" header.
        /// </summary>
        public PreparedHeader GzipDeflate { get; }
    }
}
