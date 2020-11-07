using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    internal sealed class GatheringNetworkStream : NetworkStream, IGatheringStream
    {
        private EventArgs? _gatheredEventArgs;

        public GatheringNetworkStream(Socket socket) : base(socket, ownsSocket: true)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                _gatheredEventArgs?.Dispose();
            }
        }

        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            if (buffers.Count == 1)
            {
                return WriteAsync(buffers[0], cancellationToken);
            }

            _gatheredEventArgs ??= new EventArgs();
            return _gatheredEventArgs.WriteAsync(Socket, buffers, cancellationToken);
        }

        private sealed class EventArgs : SocketTaskEventArgs<int>
        {
            private List<ArraySegment<byte>>? _gatheredSegments;
            private List<byte[]>? _pooledArrays;

            public ValueTask WriteAsync(Socket socket, IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
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
                if (!socket.SendAsync(this))
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
                    SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new IOException($"{nameof(WriteAsync)} failed. See InnerException for more details.", new SocketException((int)SocketError))));
                }
            }
        }
    }
}
