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
    /// A <see cref="NetworkStream"/> that supports gathered writes via <see cref="IScatterGatherStream"/>, socket shutdown via <see cref="ICompletableStream"/>, and on Linux, corked writes.
    /// </summary>
    public class NetworkStreamEnhanced : NetworkStream, IScatterGatherStream, ICompletableStream
    {
        const int IPPROTO_TCP = 6; // from netinet/in.h
        const int TCP_CORK = 3; // from linux/tcp.h

        private static Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>? s_receiveAsyncWithCancellation;
        private static Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>? s_sendAsyncWithCancellation;

        /// <summary>
        /// If true, <see cref="NetworkStreamEnhanced"/> supports <c>TCP_CORK</c> on the running platform.
        /// </summary>
        /// <remarks>
        /// Corking requires <c>TCP_CORK</c> option on Linux.
        /// </remarks>
        public static bool IsCorkingSupported { get; } = OperatingSystem.IsLinux();

        private bool _isCorked;
        private ReadEventArgs? _gatheredReadEventArgs;
        private WriteEventArgs? _gatheredWriteEventArgs;

        static NetworkStreamEnhanced()
        {
            MethodInfo? method = typeof(Socket).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance, binder: null, new[] { typeof(SocketAsyncEventArgs), typeof(CancellationToken) }, modifiers: null);

            if (method != null)
            {
                s_sendAsyncWithCancellation =
                    (Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>)
                    Delegate.CreateDelegate(typeof(Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>), firstArgument: null, method);
            }

            method = typeof(Socket).GetMethod("ReceiveAsync", BindingFlags.NonPublic | BindingFlags.Instance, binder: null, new[] { typeof(SocketAsyncEventArgs), typeof(CancellationToken) }, modifiers: null);

            if (method != null)
            {
                s_receiveAsyncWithCancellation =
                    (Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>)
                    Delegate.CreateDelegate(typeof(Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>), firstArgument: null, method);
            }
        }

        /// <inheritdoc/>
        public bool CanScatterGather => true;

        /// <inheritdoc/>
        public bool CanCompleteWrites => true;

        /// <summary>
        /// Instantiates a new <see cref="NetworkStreamEnhanced"/> over a <see cref="Socket"/>.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> this stream will operate over.</param>
        /// <param name="ownsSocket">If true, the <paramref name="socket"/> will be disposed of along with this stream.</param>
        /// <param name="corked">
        /// Applies <c>TCP_CORK</c> to the <paramref name="socket"/>.
        /// The application must be running on Linux to support this.
        /// Writes to this <see cref="Stream"/> will not immediately be sent to wire, instead waiting for one of:
        /// <list type="bullet">
        /// <item><description><see cref="Flush"/> or <see cref="FlushAsync(CancellationToken)"/> to be called.</description></item>
        /// <item><description>A full packet worth of data to be written.</description></item>
        /// <item><description>200ms to pass.</description></item>
        /// </list>
        /// </param>
        public NetworkStreamEnhanced(Socket socket, bool ownsSocket, bool corked = false) : base(socket, ownsSocket)
        {
        }

        /// <summary>
        /// Instantiates a new <see cref="NetworkStreamEnhanced"/> over a <see cref="Socket"/>.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> this stream will operate over.</param>
        /// <param name="access">The access permissions given to this stream.</param>
        /// <param name="ownsSocket">If true, the <paramref name="socket"/> will be disposed of along with this stream.</param>
        /// <param name="corked">
        /// Applies <c>TCP_CORK</c> to the <paramref name="socket"/>.
        /// The application must be running on Linux to support this.
        /// Writes to this <see cref="Stream"/> will not immediately be sent to wire, instead waiting for one of:
        /// <list type="bullet">
        /// <item><description><see cref="Flush"/> or <see cref="FlushAsync(CancellationToken)"/> to be called.</description></item>
        /// <item><description>A full packet worth of data to be written.</description></item>
        /// <item><description>200ms to pass.</description></item>
        /// </list>
        /// </param>
        public NetworkStreamEnhanced(Socket socket, FileAccess access, bool ownsSocket, bool corked = false) : base(socket, access, ownsSocket)
        {
            if (corked)
            {
                if (!IsCorkingSupported)
                {
                    throw new PlatformNotSupportedException($"{nameof(corked)} requires Linux.");
                }

                if (socket.ProtocolType != ProtocolType.Tcp)
                {
                    throw new IOException($"{nameof(corked)} requires a {nameof(Socket)}.{nameof(Socket.ProtocolType)} of {nameof(ProtocolType.Tcp)}");
                }

                _isCorked = corked;
                SetCork(1);
            }
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // dispose the NetworkStream first, in case the event args are currently in use.
            base.Dispose(disposing);

            if (disposing)
            {
                _gatheredWriteEventArgs?.Dispose();
            }
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            if (_isCorked)
            {
                SetCork(0);
                SetCork(1);
            }
        }

        /// <inheritdoc/>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);

            if (_isCorked)
            {
                try
                {
                    SetCork(0);
                    SetCork(1);
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            return Task.CompletedTask;
        }

        private void SetCork(int enabled)
        {
            Debug.Assert(_isCorked);
            Debug.Assert(enabled is 0 or 1);
            Socket.SetRawSocketOption(IPPROTO_TCP, TCP_CORK, MemoryMarshal.Cast<int, byte>(MemoryMarshal.CreateReadOnlySpan(ref enabled, 1)));
        }

        /// <inheritdoc/>
        public virtual ValueTask<int> ReadAsync(IReadOnlyList<Memory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            if (buffers.Count == 0)
            {
                return new ValueTask<int>(0);
            }

            // s_receiveAsyncWithCancellation should be non-null on .NET 5; it's possible future versions will remove or change the internal method we depend on.
            if (s_receiveAsyncWithCancellation == null || buffers.Count == 1)
            {
                return ReadAsync(buffers[0], cancellationToken);
            }

            _gatheredReadEventArgs ??= new ReadEventArgs();
            return _gatheredReadEventArgs.ReadAsync(Socket, buffers, cancellationToken);
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

            _gatheredWriteEventArgs ??= new WriteEventArgs();
            return _gatheredWriteEventArgs.WriteAsync(Socket, buffers, cancellationToken);
        }

        private async ValueTask WriteAsyncEmulated(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
        {
            for (int i = 0, count = buffers.Count; i != count; ++i)
            {
                await WriteAsync(buffers[i], cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Socket.Shutdown(SocketShutdown.Send);
                return default;
            }
            catch (Exception ex)
            {
                return ValueTask.FromException(ex);
            }
        }

        private sealed class ReadEventArgs : SocketTaskEventArgs<int>
        {
            private List<ArraySegment<byte>>? _scatteredSegments;
            private List<(byte[] tmp, Memory<byte> memory)>? _pooledArrays;

            public ValueTask<int> ReadAsync(Socket socket, IReadOnlyList<Memory<byte>> buffers, CancellationToken cancellationToken)
            {
                int bufferCount = buffers.Count;

                _scatteredSegments ??= new List<ArraySegment<byte>>(buffers.Count);

                for (int i = 0; i < bufferCount; ++i)
                {
                    Memory<byte> buffer = buffers[i];

                    if (!MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
                    {
                        _pooledArrays ??= new List<(byte[] array, Memory<byte> memory)>();

                        byte[] array = ArrayPool<byte>.Shared.Rent(buffer.Length);
                        _pooledArrays.Add((array, buffer));

                        segment = new ArraySegment<byte>(array, 0, buffer.Length);
                    }

                    _scatteredSegments.Add(segment);
                }

                BufferList = _scatteredSegments;
                Reset();
                if (!s_receiveAsyncWithCancellation!(socket, this, cancellationToken))
                {
                    OnCompleted();
                }

                return GenericTask;
            }

            protected override void OnCompleted(SocketAsyncEventArgs e) =>
                OnCompleted();

            public void OnCompleted()
            {
                if (_scatteredSegments != null)
                {
                    _scatteredSegments.Clear();

                    if (_pooledArrays != null)
                    {
                        foreach ((byte[] tmp, Memory<byte> buffer) in _pooledArrays)
                        {
                            tmp.AsSpan(0, buffer.Length).CopyTo(buffer.Span);
                            ArrayPool<byte>.Shared.Return(tmp);
                        }
                        _pooledArrays.Clear();
                    }
                }

                if (SocketError == SocketError.Success)
                {
                    SetResult(BytesTransferred);
                }
                else
                {
                    var ex = new SocketException((int)SocketError);
                    SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new IOException(ex.Message, ex)));
                }
            }
        }

        private sealed class WriteEventArgs : SocketTaskEventArgs<int>
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
