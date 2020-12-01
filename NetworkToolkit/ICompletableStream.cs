using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    /// <summary>
    /// A <see cref="Stream"/> extension that allows completion of writes against a duplex stream.
    /// </summary>
    public interface ICompletableStream
    {
        /// <summary>
        /// If true, the <see cref="CompleteWritesAsync(CancellationToken)"/> method is implemented.
        /// </summary>
        bool CanCompleteWrites { get; }

        /// <summary>
        /// Signals that writes to a duplex stream are complete.
        /// This will mark the end of the stream for the remote side's reads.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        public ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default);
    }
}
