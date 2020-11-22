using NetworkToolkit.Connections;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    internal sealed class TcpFastOpenStream : Stream, IGatheringStream
    {
        public const int IPPROTO_TCP = 6; // from ws2def.h
        public const int TCP_FASTOPEN = 15; // from ws2ipdef.h

        private NetworkStream? _stream;
        private IGatheringStream? _gatheringStream;
        private SocketConnectionFactory? _factory;
        private Socket? _socket;
        private EndPoint? _endPoint;
        private TaskCompletionSource? _init;

        /// <summary>
        /// Currently, Socket.ConnectAsync() only supports Fast Open through Windows.
        /// </summary>
        // If this ever supports more than Windows, make sure constants above hold across OSes.
        public static bool IsSupported => OperatingSystem.IsWindows();

        public override bool CanRead => _socket != null || _stream?.CanRead == true;

        public override bool CanSeek => false;

        public override bool CanWrite => _socket != null || _stream?.CanWrite == true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public TcpFastOpenStream(SocketConnectionFactory factory, Socket socket, EndPoint endPoint)
        {
            _factory = factory;
            _socket = socket;
            _endPoint = endPoint;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private NetworkStream GetStream()
        {
            if (_stream is NetworkStream stream)
            {
                return stream;
            }

            Tools.BlockForResult(ConnectAndSend(ReadOnlyMemory<byte>.Empty, buffers: null, CancellationToken.None));

            Debug.Assert(_stream != null);
            return _stream;
        }

        public override void Flush() =>
            GetStream().Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _stream is NetworkStream stream ? stream.FlushAsync(cancellationToken) : FlushAsyncSlow(cancellationToken);

        private async Task FlushAsyncSlow(CancellationToken cancellationToken)
        {
            await ConnectAndSend(ReadOnlyMemory<byte>.Empty, buffers: null, cancellationToken).ConfigureAwait(false);

            Debug.Assert(_stream != null);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            GetStream().Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) =>
            GetStream().Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _stream is NetworkStream stream ? stream.ReadAsync(buffer, offset, count, cancellationToken) : ReadAsyncSlow(buffer, offset, count, cancellationToken);

        private async Task<int> ReadAsyncSlow(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await ConnectAndSend(ReadOnlyMemory<byte>.Empty, buffers: null, cancellationToken).ConfigureAwait(false);

            Debug.Assert(_stream != null);
            return await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _stream is NetworkStream stream ? stream.ReadAsync(buffer, cancellationToken) : ReadAsyncSlow(buffer, cancellationToken);

        private async ValueTask<int> ReadAsyncSlow(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await ConnectAndSend(ReadOnlyMemory<byte>.Empty, buffers: null, cancellationToken).ConfigureAwait(false);

            Debug.Assert(_stream != null);
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);

        public override int EndRead(IAsyncResult asyncResult) =>
            TaskToApm.End<int>(asyncResult);

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            GetStream().Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) =>
            GetStream().Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _stream is NetworkStream stream ? stream.WriteAsync(buffer, offset, count, cancellationToken) : ConnectAndSend(buffer.AsMemory(offset, count), buffers: null, cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _stream is NetworkStream stream ? stream.WriteAsync(buffer, cancellationToken) : ConnectAndSend(buffer, buffers: null, cancellationToken);

        public ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            if ((_gatheringStream ??= (IGatheringStream?)_stream) is IGatheringStream gatheringStream)
            {
                return gatheringStream.WriteAsync(buffers, cancellationToken);
            }
            else
            {
                return ConnectAndSend(ReadOnlyMemory<byte>.Empty, buffers, cancellationToken);
            }
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            return base.BeginWrite(buffer, offset, count, callback, state);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            base.EndWrite(asyncResult);
        }

        public override void CopyTo(Stream destination, int bufferSize)
        {
            base.CopyTo(destination, bufferSize);
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }

        private async ValueTask ConnectAndSend(ReadOnlyMemory<byte> buffer, IReadOnlyList<ReadOnlyMemory<byte>>? buffers, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _stream) is NetworkStream stream)
            {
                // Someone else initialized.
                await (buffers != null
                    ? (_gatheringStream = (IGatheringStream)stream).WriteAsync(buffers, cancellationToken)
                    : stream.WriteAsync(buffer, cancellationToken)).ConfigureAwait(false);
                return;
            }

            TaskCompletionSource tcs = new TaskCompletionSource();
            TaskCompletionSource? oldTcs = Interlocked.CompareExchange(ref _init, tcs, null!);

            if (oldTcs != null)
            {
                // Someone else is initializing.
                try
                {
                    await oldTcs.Task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new IOException(ex.Message, ex);
                }

                Debug.Assert(_stream != null);

                await (buffers != null
                    ? (_gatheringStream = (IGatheringStream)_stream).WriteAsync(buffers, cancellationToken)
                    : _stream.WriteAsync(buffer, cancellationToken)).ConfigureAwait(false);
                return;
            }

            if ((stream = Volatile.Read(ref _stream)!) != null)
            {
                // Someone else initialized.
                await (buffers != null
                    ? (_gatheringStream = (IGatheringStream)stream).WriteAsync(buffers, cancellationToken)
                    : stream.WriteAsync(buffer, cancellationToken)).ConfigureAwait(false);
                return;
            }

            // We are initializing.

            byte[]? gatherTemp = null;
            int takeLen = 0;
            long leftLen = 0;

            Debug.Assert(_socket != null);
            Debug.Assert(_factory != null);

            try
            {
                using var args = new SocketTaskEventArgs<int>();

                if (buffers != null)
                {
                    Debug.Assert(buffer.Length == 0);

                    if (buffers.Count == 1)
                    {
                        // Gathered write, but only one buffer. Send it out along with the connect.

                        ReadOnlyMemory<byte> memory = buffers[0];
                        if (memory.Length != 0)
                        {
                            args.SetBuffer(MemoryMarshal.AsMemory(memory));
                            takeLen = memory.Length;
                        }
                    }
                    else
                    {
                        // Multiple buffers. Combine the first 1500 bytes into a buffer.

                        leftLen = 0;

                        for (int i = 0, count = buffers.Count; i < count; ++i)
                        {
                            leftLen = checked(leftLen + buffers[i].Length);
                        }

                        if (leftLen != 0)
                        {
                            gatherTemp = ArrayPool<byte>.Shared.Rent((int)Math.Min(leftLen, 1500));
                            takeLen = (int)Math.Min(leftLen, gatherTemp.Length);
                            leftLen -= takeLen;

                            int gatherTempPos = 0;

                            for (int i = 0, count = buffers.Count; i < count && takeLen != 0; ++i)
                            {
                                int len = Math.Min(takeLen, buffers[i].Length);

                                buffers[i].Span[..len].CopyTo(gatherTemp.AsSpan(gatherTempPos));
                                gatherTempPos += len;
                                takeLen -= len;
                            }

                            takeLen = gatherTempPos;
                            args.SetBuffer(gatherTemp.AsMemory(0, gatherTempPos));
                        }
                    }
                }
                else if (buffer.Length != 0)
                {
                    // Only one buffer.

                    args.SetBuffer(MemoryMarshal.AsMemory(buffer));
                    takeLen = buffer.Length;
                }

                if (_socket.ConnectAsync(args))
                {
                    using (cancellationToken.UnsafeRegister(o => Socket.CancelConnectAsync((SocketTaskEventArgs<int>)o!), args))
                    {
                        await args.Task.ConfigureAwait(false);
                    }
                }
                else
                {
                    args.SetResult(0);
                }

                if (gatherTemp != null)
                {
                    ArrayPool<byte>.Shared.Return(gatherTemp);
                }

                if (args.SocketError != SocketError.Success)
                {
                    throw new SocketException((int)SocketError.Success);
                }

                stream = _factory.CreateStream(_socket);

                if (stream is not IGatheringStream gatheringStream)
                {
                    throw new Exception($"{nameof(TcpFastOpenStream)} requires {nameof(SocketConnectionFactory.CreateStream)} to return an {nameof(IGatheringStream)}");
                }

                leftLen += takeLen - args.BytesTransferred;
                if (leftLen != 0)
                {
                    takeLen -= args.BytesTransferred;

                    if (buffer.Length != 0)
                    {
                        await stream.WriteAsync(buffer[takeLen..], cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        Debug.Assert(buffers != null);

                        int count = buffers.Count;
                        var newBuffers = new List<ReadOnlyMemory<byte>>(count);

                        int i;
                        for (i = 0; i != count; ++i)
                        {
                            ReadOnlyMemory<byte> memory = buffers[i];
                            if (takeLen < memory.Length)
                            {
                                newBuffers.Add(memory[takeLen..]);
                                break;
                            }
                            takeLen -= memory.Length;
                        }

                        while (++i != count)
                        {
                            newBuffers.Add(buffers[i]);
                        }

                        if (newBuffers.Count == 1)
                        {
                            await stream.WriteAsync(newBuffers[0], cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await gatheringStream.WriteAsync(newBuffers, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                Volatile.Write(ref _stream, stream);

                _init.SetResult();
                Volatile.Write(ref _init, null!);

                _socket = null;
                _factory = null;
            }
            catch (Exception ex)
            {
                _init.SetException(ex);
                if (stream != null) await stream.DisposeAsync(cancellationToken).ConfigureAwait(false);
                throw new IOException(ex.Message, ex);
            }
        }
    }
}
