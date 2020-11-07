using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Connections
{
    /// <summary>
    /// An in-memory connection.
    /// </summary>
    public sealed class MemoryConnection : Connection
    {
        /// <inheritdoc/>
        public override EndPoint? LocalEndPoint { get; }

        /// <inheritdoc/>
        public override EndPoint? RemoteEndPoint { get; }

        /// <summary>
        /// Opens a new in-memory connection.
        /// </summary>
        /// <param name="clientEndPoint">The <see cref="EndPoint"/> to use for the client connection, if any.</param>
        /// <param name="serverEndPoint">The <see cref="EndPoint"/> to use for the server connection, if any.</param>
        /// <returns>A tuple of the client and server connections.</returns>
        public static (Connection clientConnection, Connection serverConnection) Create(EndPoint? clientEndPoint = null, EndPoint? serverEndPoint = null)
        {
            var bufferA = new Pipe();
            var bufferB = new Pipe();

            Connection clientConnection = new MemoryConnection(bufferA.Reader, bufferB.Writer, clientEndPoint, serverEndPoint);
            Connection serverConnection = new MemoryConnection(bufferB.Reader, bufferA.Writer, serverEndPoint, clientEndPoint);

            return (clientConnection, serverConnection);
        }

        private MemoryConnection(PipeReader reader, PipeWriter writer, EndPoint? localEndPoint, EndPoint? remoteEndPoint) : base(new MemoryConnectionStream(reader, writer))
        {
            LocalEndPoint = localEndPoint;
            RemoteEndPoint = remoteEndPoint;
        }

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore(CancellationToken cancellationToken)
            => default;

        /// <inheritdoc/>
        public override ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
            => ((MemoryConnectionStream)Stream).CompleteWritesAsync(cancellationToken);

        private sealed class MemoryConnectionStream : Stream
        {
            readonly PipeReader _reader;
            readonly PipeWriter _writer;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotImplementedException();

            public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

            public MemoryConnectionStream(PipeReader reader, PipeWriter writer)
            {
                _reader = reader;
                _writer = writer;
            }

            internal async ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
            {
                ValueTask task = _writer.CompleteAsync();
                CancellationTokenRegistration registration =
                    task.IsCompleted || !cancellationToken.CanBeCanceled ? default :
                    cancellationToken.UnsafeRegister(o => ((PipeWriter)o!).CancelPendingFlush(), _writer);

                try
                {
                    await task.ConfigureAwait(false);
                }
                finally
                {
                    await registration.DisposeAsync().ConfigureAwait(false);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
                => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                ValueTask<ReadResult> resTask = _reader.ReadAsync();
                ReadResult res = resTask.IsCompleted ? resTask.GetAwaiter().GetResult() : resTask.AsTask().GetAwaiter().GetResult();

                if (res.IsCanceled)
                {
                    throw new OperationCanceledException();
                }

                int len = (int)Math.Min(buffer.Length, res.Buffer.Length);

                SequencePosition consumeTo = res.Buffer.GetPosition(len);

                res.Buffer.Slice(0, consumeTo).CopyTo(buffer);
                _reader.AdvanceTo(consumeTo);

                return len;
            }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
                TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);

            public override int EndRead(IAsyncResult asyncResult) =>
                TaskToApm.End<int>(asyncResult);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                try
                {
                    ReadResult res = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                    if (res.IsCanceled)
                    {
                        throw new SocketException((int)SocketError.OperationAborted);
                    }

                    int len = (int)Math.Min(buffer.Length, res.Buffer.Length);

                    SequencePosition consumeTo = res.Buffer.GetPosition(len);

                    res.Buffer.Slice(0, consumeTo).CopyTo(buffer.Span);
                    _reader.AdvanceTo(consumeTo);

                    return len;
                }
                catch (Exception ex)
                {
                    throw new IOException("Read failed. See InnerException for details.", ex);
                }
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                buffer.CopyTo(_writer.GetSpan(buffer.Length));
                _writer.Advance(buffer.Length);

                ValueTask<FlushResult> resTask = _writer.FlushAsync();
                FlushResult res = resTask.IsCompleted ? resTask.GetAwaiter().GetResult() : resTask.AsTask().GetAwaiter().GetResult();

                if (res.IsCanceled)
                {
                    throw new OperationCanceledException();
                }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                try
                {
                    FlushResult res = await _writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (res.IsCanceled)
                    {
                        throw new SocketException((int)SocketError.OperationAborted);
                    }
                }
                catch (Exception ex)
                {
                    throw new IOException("Write failed. See InnerException for details.", ex);
                }
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
                return Task.CompletedTask;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }
        }
    }
}
