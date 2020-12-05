using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
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
        /// <param name="clientPipeOptions">Pipe options used when configuring client-side buffers. If null, <see cref="PipeOptions.Default"/> will be used.</param>
        /// <param name="serverEndPoint">The <see cref="EndPoint"/> to use for the server connection, if any.</param>
        /// <param name="serverPipeOptions">Pipe options used when configuring server-side buffers. If null, <see cref="PipeOptions.Default"/> will be used.</param>
        /// <returns>A tuple of the client and server connections.</returns>
        public static (Connection clientConnection, Connection serverConnection) Create(EndPoint? clientEndPoint = null, PipeOptions? clientPipeOptions = null, EndPoint? serverEndPoint = null, PipeOptions? serverPipeOptions = null)
        {
            var bufferA = new Pipe(clientPipeOptions ?? PipeOptions.Default);
            var bufferB = new Pipe(clientPipeOptions ?? PipeOptions.Default);

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

        private sealed class MemoryConnectionStream : Stream, IGatheringStream, ICompletableStream
        {
            PipeReader? _reader;
            PipeWriter? _writer;

            public bool CanWriteGathered => true;
            public bool CanCompleteWrites => true;

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
                if (disposing && _reader is PipeReader reader)
                {
                    _writer?.Complete();
                    reader.Complete();
                    _writer = null;
                    _reader = null;
                }
            }

            /// <inheritdoc/>
            public ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
            {
                if (_writer == null) return default;

                try
                {
                    _writer.Complete();
                    return default;
                }
                catch(Exception ex)
                {
                    return ValueTask.FromException(ExceptionDispatchInfo.SetCurrentStackTrace(new IOException(ex.Message, ex)));
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
                => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_reader is not PipeReader reader) throw new ObjectDisposedException(nameof(MemoryConnectionStream));

                try
                {
                    return FinishRead(reader, buffer, Tools.BlockForResult(_reader.ReadAsync()), CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
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
                if (_reader is not PipeReader reader) throw new ObjectDisposedException(nameof(MemoryConnectionStream));

                try
                {
                    ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    return FinishRead(reader, buffer.Span, result, cancellationToken);
                }
                catch (Exception ex) when(ex is not OperationCanceledException)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            private static int FinishRead(PipeReader reader, Span<byte> buffer, in ReadResult result, CancellationToken cancellationToken)
            {
                if (result.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new OperationCanceledException();
                }

                ReadOnlySequence<byte> sequence = result.Buffer;
                long sequenceLength = sequence.Length;
                SequencePosition consumed = sequence.Start;

                try
                {
                    if (sequenceLength != 0)
                    {
                        int actual = (int)Math.Min(sequenceLength, buffer.Length);

                        if (actual != sequenceLength)
                        {
                            sequence = sequence.Slice(0, actual);
                        }

                        consumed = sequence.End;
                        sequence.CopyTo(buffer);

                        return actual;
                    }

                    Debug.Assert(result.IsCompleted, "An uncompleted Pipe should never return a 0-length buffer.");
                    return 0;
                }
                finally
                {
                    reader.AdvanceTo(consumed);
                }
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (_reader == null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));
                if (_writer is not PipeWriter writer) throw new InvalidOperationException($"{nameof(MemoryConnectionStream)} cannot be written to after writes have been completed.");

                try
                {
                    buffer.CopyTo(writer.GetSpan(buffer.Length));
                    writer.Advance(buffer.Length);

                    FlushResult res = Tools.BlockForResult(writer.FlushAsync());

                    if (res.IsCanceled)
                    {
                        throw new OperationCanceledException();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_reader is null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));
                if (_writer is not PipeWriter writer) throw new InvalidOperationException($"{nameof(MemoryConnectionStream)} cannot be written to after writes have been completed.");

                try
                {
                    buffer.Span.CopyTo(writer.GetSpan(buffer.Length));
                    writer.Advance(buffer.Length);

                    FlushResult res = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (res.IsCanceled)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            public async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
            {
                if (_reader == null) throw new ObjectDisposedException(nameof(MemoryConnectionStream));
                if (_writer is not PipeWriter writer) throw new InvalidOperationException($"{nameof(MemoryConnectionStream)} cannot be written to after writes have been completed.");

                try
                {
                    foreach (ReadOnlyMemory<byte> buffer in buffers)
                    {
                        buffer.Span.CopyTo(writer.GetSpan(buffer.Length));
                        writer.Advance(buffer.Length);
                    }

                    FlushResult res = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (res.IsCanceled)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException(ex.Message, ex);
                }
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
                TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);

            public override void EndWrite(IAsyncResult asyncResult) =>
                TaskToApm.End(asyncResult);

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

            public override void CopyTo(Stream destination, int bufferSize) =>
                CopyToAsync(destination, bufferSize, CancellationToken.None).GetAwaiter().GetResult();

            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                if (_reader is not PipeReader reader) throw new ObjectDisposedException(nameof(MemoryConnectionStream));
                try
                {
                    await reader.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException(ex.Message, ex);
                }
            }
        }
    }
}
