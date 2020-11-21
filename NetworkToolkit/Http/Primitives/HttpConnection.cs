using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A HTTP connection.
    /// </summary>
    public abstract class HttpConnection : ICancellableAsyncDisposable
    {
        /// <summary>
        /// The current status of the connection.
        /// </summary>
        /// <remarks>
        /// This should not be relied on to assume
        /// <see cref="CreateNewRequestAsync(HttpPrimitiveVersion, HttpVersionPolicy, CancellationToken)"/>
        /// will succeed. The connection may be closed between checking status and creating a request.
        /// </remarks>
        public abstract HttpConnectionStatus Status { get; }

        /// <summary>
        /// Opens a new request on the connection.
        /// </summary>
        /// <param name="version">The HTTP version of the request to make.</param>
        /// <param name="versionPolicy">A policy controlling version selection for the request.</param>
        /// <param name="cancellationToken">A cancellation token for this operation.</param>
        /// <returns>
        /// If a request can be made, a <see cref="HttpRequest"/> instance used to make a single request.
        /// Otherwise, null to indicate the connection is not accepting new requests.
        /// </returns>
        /// <remarks>
        /// This should return null if the connection has been gracefully closed e.g. connection reset, received GOAWAY, etc.
        /// </remarks>
        public abstract ValueTask<ValueHttpRequest?> CreateNewRequestAsync(HttpPrimitiveVersion version, HttpVersionPolicy versionPolicy, CancellationToken cancellationToken = default);

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            return DisposeAsync(CancellationToken.None);
        }

        /// <inheritdoc/>
        public abstract ValueTask DisposeAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Prunes any resources that have expired past a certain age.
        /// </summary>
        public abstract ValueTask PrunePoolsAsync(long curTicks, TimeSpan lifetimeLimit, TimeSpan idleLimit, CancellationToken cancellationToken = default);
    }
}
