using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// A Stream-oriented connection.
    /// </summary>
    public abstract class Connection : ICancellableAsyncDisposable, IConnectionProperties
    {
        private Stream? _stream;
        private int _disposed;

        /// <summary>
        /// The connection's local endpoint, if any.
        /// </summary>
        public abstract EndPoint? LocalEndPoint { get; }

        /// <summary>
        /// The connection's remote endpoint, if any.
        /// </summary>
        public abstract EndPoint? RemoteEndPoint { get; }

        /// <summary>
        /// The connection's stream.
        /// </summary>
        public Stream Stream => _disposed == 2 ? throw new ObjectDisposedException(GetType().Name) : _stream!;

        /// <summary>
        /// Constructs a new <see cref="Connection"/> with a stream.
        /// </summary>
        /// <param name="stream">The connection's stream.</param>
        protected Connection(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            return DisposeAsync(CancellationToken.None);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await DisposeAsyncCore(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _disposed, 2);

            Stream? stream = _stream;
            Debug.Assert(stream != null);

            _stream = null;

            await (stream is ICancellableAsyncDisposable cancellable ?
                cancellable.DisposeAsync(cancellationToken) :
                stream.DisposeAsync()).ConfigureAwait(false);
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

        /// <inheritdoc/>
        public virtual bool TryGetProperty(Type type, out object? value)
        {
            value = null;
            return false;
        }

        /// <summary>
        /// Completes any writes on the stream.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public abstract ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default);
    }
}
