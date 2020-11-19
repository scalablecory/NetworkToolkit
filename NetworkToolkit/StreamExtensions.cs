using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    internal static class StreamExtensions
    {
        public static ValueTask DisposeAsync(this Stream stream, CancellationToken cancellationToken)
        {
            if (stream is ICancellableAsyncDisposable disposable)
            {
                return disposable.DisposeAsync(cancellationToken);
            }
            else
            {
                return stream.DisposeAsync();
            }
        }
    }
}
