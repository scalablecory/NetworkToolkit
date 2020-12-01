using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    /// <summary>
    /// A <see cref="Stream"/> extension that enables gathered writes.
    /// </summary>
    public interface IGatheringStream
    {
        /// <summary>
        /// If true, the <see cref="WriteAsync(IReadOnlyList{ReadOnlyMemory{byte}}, CancellationToken)"/> implementation will perform an optimal gathered write.
        /// </summary>
        bool CanWriteGathered { get; }

        /// <summary>
        /// Writes a list of buffers as a single I/O.
        /// </summary>
        /// <param name="buffers">The buffers to write.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default);
    }
}
