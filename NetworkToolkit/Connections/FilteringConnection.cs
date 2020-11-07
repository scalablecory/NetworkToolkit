using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A connection that filters another connection.
    /// </summary>
    public class FilteringConnection : Connection
    {
        /// <summary>
        /// The base connection.
        /// </summary>
        protected Connection BaseConnection { get; }

        /// <inheritdoc/>
        public override EndPoint? LocalEndPoint => BaseConnection.LocalEndPoint;

        /// <inheritdoc/>
        public override EndPoint? RemoteEndPoint => BaseConnection.RemoteEndPoint;

        /// <summary>
        /// Instantiates a new <see cref="FilteringConnection"/>
        /// </summary>
        /// <param name="baseConnection">The base connection for the <see cref="FilteringConnection"/>.</param>
        /// <param name="stream">The connection's stream.</param>
        public FilteringConnection(Connection baseConnection, Stream stream) : base(stream)
        {
            BaseConnection = baseConnection ?? throw new ArgumentNullException(nameof(baseConnection));
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken) =>
            BaseConnection.DisposeAsync(cancellationToken);

        /// <inheritdoc/>
        public override ValueTask CompleteWritesAsync(CancellationToken cancellationToken) =>
            BaseConnection.CompleteWritesAsync(cancellationToken);
    }
}
