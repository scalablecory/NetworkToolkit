using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    /// <summary>
    /// A <see cref="NetworkStream"/> that supports gathered writes via <see cref="IGatheringStream"/>.
    /// </summary>
    public class GatheringNetworkStream : NetworkStream, IGatheringStream
    {
        private EventArgs? _gatheredEventArgs;
        private static Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>? s_sendAsyncWithCancellation;

        static GatheringNetworkStream()
        {
            MethodInfo? sendAsync = typeof(Socket).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance, binder: null, new[] { typeof(SocketAsyncEventArgs), typeof(CancellationToken) }, modifiers: null);

            if (sendAsync != null)
            {
                s_sendAsyncWithCancellation =
                    (Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>)
                    Delegate.CreateDelegate(typeof(Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>), firstArgument: null, sendAsync);
            }
        }

        /// <summary>
        /// Instantiates a new <see cref="GatheringNetworkStream"/> over a <see cref="Socket"/>.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> this stream will operate over.</param>
        /// <param name="ownsSocket">If true, the <paramref name="socket"/> will be disposed of along with this stream.</param>
        public GatheringNetworkStream(Socket socket, bool ownsSocket) : base(socket, ownsSocket)
        {
        }

        /// <summary>
        /// Instantiates a new <see cref="GatheringNetworkStream"/> over a <see cref="Socket"/>.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> this stream will operate over.</param>
        /// <param name="access">The access permissions given to this stream.</param>
        /// <param name="ownsSocket">If true, the <paramref name="socket"/> will be disposed of along with this stream.</param>
        public GatheringNetworkStream(Socket socket, FileAccess access, bool ownsSocket) : base(socket, access, ownsSocket)
        {
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // dispose the NetworkStream first, in case the event args are currently in use.
            base.Dispose(disposing);

            if (disposing)
            {
                _gatheredEventArgs?.Dispose();
            }
        }

        /// <inheritdoc/>
        public virtual ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            if (buffers.Count == 1)
            {
                return WriteAsync(buffers[0], cancellationToken);
            }

            if (s_sendAsyncWithCancellation == null)
            {
                // This shouldn't be hit on .NET 5; it's possible future versions will remove or change the internal method we depend on.
                return WriteAsyncEmulated(buffers, cancellationToken);
            }

            _gatheredEventArgs ??= new EventArgs();
            return _gatheredEventArgs.WriteAsync(Socket, buffers, cancellationToken);
        }

        private async ValueTask WriteAsyncEmulated(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
        {
            for (int i = 0, count = buffers.Count; i != count; ++i)
            {
                await WriteAsync(buffers[i], cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class EventArgs : SocketTaskEventArgs<int>
        {
            private List<ArraySegment<byte>>? _gatheredSegments;
            private List<byte[]>? _pooledArrays;

            public ValueTask WriteAsync(Socket socket, IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
            {
                int bufferCount = buffers.Count;

                _gatheredSegments ??= new List<ArraySegment<byte>>(buffers.Count);

                for (int i = 0; i < bufferCount; ++i)
                {
                    ReadOnlyMemory<byte> buffer = buffers[i];

                    if (!MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
                    {
                        _pooledArrays ??= new List<byte[]>();

                        byte[] array = ArrayPool<byte>.Shared.Rent(buffer.Length);
                        _pooledArrays.Add(array);

                        buffer.CopyTo(array);

                        segment = new ArraySegment<byte>(array, 0, buffer.Length);
                    }

                    _gatheredSegments.Add(segment);
                }

                BufferList = _gatheredSegments;
                Reset();
                if (!s_sendAsyncWithCancellation!(socket, this, cancellationToken))
                {
                    OnCompleted();
                }

                return Task;
            }

            protected override void OnCompleted(SocketAsyncEventArgs e) =>
                OnCompleted();

            public void OnCompleted()
            {
                if (_gatheredSegments != null)
                {
                    _gatheredSegments.Clear();

                    if (_pooledArrays != null)
                    {
                        foreach (byte[] buffer in _pooledArrays)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                        _pooledArrays.Clear();
                    }
                }

                if (SocketError == SocketError.Success)
                {
                    SetResult(0);
                }
                else
                {
                    var ex = new SocketException((int)SocketError);
                    SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new IOException(ex.Message, ex)));
                }
            }
        }
    }
}
