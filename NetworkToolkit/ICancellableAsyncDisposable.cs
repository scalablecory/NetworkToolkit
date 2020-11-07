using System;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    /// <summary>
    /// An <see cref="IAsyncDisposable"/> that can be cancelled.
    /// </summary>
    public interface ICancellableAsyncDisposable : IAsyncDisposable
    {
        /// <summary>
        /// Disposes of any native resources.
        /// </summary>
        /// <param name="cancellationToken">
        /// A cancellation token for the asynchronous operation.
        /// If canceled, the dispose may finish sooner but with only minimal cleanup, i.e. without flushing buffers to disk.
        /// </param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        ValueTask DisposeAsync(CancellationToken cancellationToken);
    }
}
