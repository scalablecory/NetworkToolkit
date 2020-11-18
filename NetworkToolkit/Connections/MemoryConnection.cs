using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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

        private sealed class MemoryConnectionStream : Stream, IGatheringStream
        {
            PipeReader _reader;
            PipeWriter? _writer;

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

            protected override void Dispose(bool disposing)
            {
                if (disposing && _writer != null)
                {
                    _writer.Complete();
                    _reader.Complete();
                    _writer = null!;
                    _reader = null!;
                }
            }

            internal ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
            {
                if (_writer == null) return default;

                try
                {
                    _writer.Complete();
                    return default;
                }
                catch(Exception ex)
                {
                    return ValueTask.FromException(ex);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
                => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_reader == null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));

                try
                {
                    return FinishRead(buffer, Tools.BlockForResult(_reader.ReadAsync()));
                }
                catch (Exception ex)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
                TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);

            public override int EndRead(IAsyncResult asyncResult) =>
                TaskToApm.End<int>(asyncResult);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_reader == null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));

                try
                {
                    ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    return FinishRead(buffer.Span, result);
                }
                catch (Exception ex)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            private int FinishRead(Span<byte> buffer, in ReadResult result)
            {
                if (result.IsCanceled)
                {
                    throw new SocketException((int)SocketError.OperationAborted);
                }

                ReadOnlySequence<byte> sequence = result.Buffer;
                long bufferLength = sequence.Length;
                SequencePosition consumed = sequence.Start;

                try
                {
                    if (bufferLength != 0)
                    {
                        int actual = (int)Math.Min(bufferLength, buffer.Length);

                        ReadOnlySequence<byte> slice = actual == bufferLength ? sequence : sequence.Slice(0, actual);
                        consumed = slice.End;
                        slice.CopyTo(buffer);

                        return actual;
                    }

                    Debug.Assert(result.IsCompleted, "An uncompleted Pipe should never return a 0-length buffer.");
                    return 0;
                }
                finally
                {
                    _reader.AdvanceTo(consumed);
                }
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (_writer == null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));

                try
                {
                    buffer.CopyTo(_writer.GetSpan(buffer.Length));
                    _writer.Advance(buffer.Length);

                    FlushResult res = Tools.BlockForResult(_writer.FlushAsync());

                    if (res.IsCanceled)
                    {
                        throw new SocketException((int)SocketError.OperationAborted);
                    }
                }
                catch (Exception ex)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_writer == null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));

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
                    throw new IOException(ex.Message, ex);
                }
            }

            public async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
            {
                if (_writer == null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));

                try
                {
                    foreach (ReadOnlyMemory<byte> buffer in buffers)
                    {
                        buffer.Span.CopyTo(_writer.GetSpan(buffer.Length));
                        _writer.Advance(buffer.Length);
                    }

                    FlushResult res = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (res.IsCanceled)
                    {
                        throw new SocketException((int)SocketError.OperationAborted);
                    }
                }
                catch (Exception ex)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            public override void Flush()
            {
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
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
