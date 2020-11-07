using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A connection listener.
    /// </summary>
    public abstract class ConnectionListener : IAsyncDisposable
    {
        private int _disposed;

        /// <summary>
        /// The listener's local <see cref="EndPoint"/>, if any.
        /// </summary>
        public abstract EndPoint? EndPoint { get; }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            return DisposeAsync(CancellationToken.None);
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            return Interlocked.Exchange(ref _disposed, 1) == 0
                ? DisposeAsyncCore(cancellationToken)
                : default;
        }

        /// <summary>
        /// Disposes of the connection.
        /// </summary>
        /// <param name="cancellationToken">
        /// A cancellation token for the asynchronous operation.
        /// If canceled, the dispose may finish sooner but with only minimal cleanup, i.e. without flushing buffers to disk.
        /// </param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        protected abstract ValueTask DisposeAsyncCore(CancellationToken cancellationToken);

        /// <summary>
        /// Accepts a new <see cref="Connection"/>, if available.
        /// </summary>
        /// <param name="options">Any options used to control the operation.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>
        /// If the listener is active, an established <see cref="Connection"/>.
        /// If the operation was cancelled, null.
        /// </returns>
        public abstract ValueTask<Connection?> AcceptConnectionAsync(IConnectionProperties? options = null, CancellationToken cancellationToken = default);
    }
}
