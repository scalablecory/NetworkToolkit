using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    internal static class StreamExtensions
    {
        public static ValueTask WriteAsync(this Stream stream, IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
        {
            if (stream is IGatheringStream gatheringStream)
            {
                return gatheringStream.WriteAsync(buffers, cancellationToken);
            }
            else
            {
                return WriteSlowAsync(stream, buffers, cancellationToken);
            }

            static async ValueTask WriteSlowAsync(Stream stream, IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
            {
                for (int i = 0, count = buffers.Count; i < count; ++i)
                {
                    await stream.WriteAsync(buffers[i], cancellationToken).ConfigureAwait(false);
                }
            }
        }

        
    }
}
