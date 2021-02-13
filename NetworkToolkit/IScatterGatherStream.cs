using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    /// <summary>
    /// A <see cref="Stream"/> extension that enables scattered reads and gathered writes.
    /// </summary>
    public interface IScatterGatherStream
    {
        /// <summary>
        /// If true, the <see cref="ReadAsync(IReadOnlyList{Memory{byte}}, CancellationToken)"/> and <see cref="WriteAsync(IReadOnlyList{ReadOnlyMemory{byte}}, CancellationToken)"/> methods will perform optimal scattered reads and gathered writes.
        /// </summary>
        bool CanScatterGather { get; }

        /// <summary>
        /// Reads a list of buffers as a single I/O.
        /// </summary>
        /// <param name="buffers">The buffers to read.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>The number of bytes read.</returns>
        ValueTask<int> ReadAsync(IReadOnlyList<Memory<byte>> buffers, CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes a list of buffers as a single I/O.
        /// </summary>
        /// <param name="buffers">The buffers to write.</param>
        /// <param name="cancellationToken">A cancellation token for the asynchronous operation.</param>
        /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
        ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default);
    }
}
