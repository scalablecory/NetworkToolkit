using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    /// <summary>
    /// A stream that buffers writes.
    /// </summary>
    public sealed class WriteBufferingStream : Stream, IGatheringStream, ICancellableAsyncDisposable
    {
        private readonly Stream _baseStream;
        private byte[] _buffer;
        private int _writePos;
        private bool _ownsBaseStream;
        private bool _disposed;

        /// <summary>
        /// The underlying stream being buffered.
        /// </summary>
        public Stream BaseStream => _baseStream;

        /// <inheritdoc/>
        public override bool CanRead => _baseStream?.CanRead ?? false;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override bool CanWrite => _baseStream != null;

        /// <inheritdoc/>
        public override long Length => throw new NotImplementedException();

        /// <inheritdoc/>
        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        /// <summary>
        /// Instantiates a new <see cref="WriteBufferingStream"/>.
        /// </summary>
        /// <param name="baseStream">The underlying stream to buffer.</param>
        /// <param name="minimumBufferSize">The minimum size of buffer to use.</param>
        /// <param name="ownsBaseStream">If true, <paramref name="baseStream"/> will be disposed by the <see cref="WriteBufferingStream"/>.</param>
        public WriteBufferingStream(Stream baseStream, int minimumBufferSize = 4096, bool ownsBaseStream = true)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _buffer = ArrayPool<byte>.Shared.Rent(minimumBufferSize);
            _ownsBaseStream = ownsBaseStream;
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                Flush();
                if (_ownsBaseStream)
                {
                    _baseStream.Dispose();
                }

                _disposed = true;

                byte[]? buffer = Interlocked.Exchange(ref _buffer, null!);
                if (buffer != null) ArrayPool<byte>.Shared.Return(_buffer);
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc/>
        public override ValueTask DisposeAsync() =>
            DisposeAsync(CancellationToken.None);

        /// <inheritdoc/>
        public async ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            if (!_disposed)
            {
                try
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted && cancellationToken.IsCancellationRequested)
                {
                    // ignore OperationAborted triggered by cancellation token
                    // as DisposeAsync() should not throw on cancel.
                }

                if (_ownsBaseStream)
                {
                    await _baseStream.DisposeAsync(cancellationToken).ConfigureAwait(false);
                }

                _disposed = true;

                byte[]? buffer = Interlocked.Exchange(ref _buffer, null!);
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            if (_writePos != 0)
            {
                _baseStream.Write(_buffer, 0, _writePos);
                _writePos = 0;
            }

            _baseStream.Flush();
        }

        /// <inheritdoc/>
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_writePos != 0)
            {
                await _baseStream.WriteAsync(_buffer.AsMemory(0, _writePos), cancellationToken).ConfigureAwait(false);
                _writePos = 0;
            }

            await _baseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) =>
            _baseStream.Read(buffer, offset, count);

        /// <inheritdoc/>
        public override int Read(Span<byte> buffer) =>
            _baseStream.Read(buffer);

        /// <inheritdoc/>
        public override int ReadByte() =>
            _baseStream.ReadByte();

        /// <inheritdoc/>
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            _baseStream.BeginRead(buffer, offset, count, callback, state);

        /// <inheritdoc/>
        public override int EndRead(IAsyncResult asyncResult) =>
            _baseStream.EndRead(asyncResult);

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _baseStream.ReadAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc/>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _baseStream.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        /// <inheritdoc/>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_writePos != 0)
            {
                int len = Math.Min(buffer.Length, _buffer.Length - _writePos);

                buffer.Slice(0, len).CopyTo(_buffer.AsSpan(_writePos));
                _writePos += len;

                if (_writePos != _buffer.Length)
                {
                    return;
                }

                _baseStream.Write(_buffer);
                _writePos = 0;

                buffer = buffer.Slice(len);
            }

            Debug.Assert(_writePos == 0);

            if (buffer.Length >= _buffer.Length)
            {
                _baseStream.Write(buffer);
                return;
            }

            if (buffer.Length >= 0)
            {
                buffer.CopyTo(_buffer);
                _writePos = buffer.Length;
            }
        }

        /// <inheritdoc/>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc/>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_writePos != 0)
            {
                int len = Math.Min(buffer.Length, _buffer.Length - _writePos);

                buffer.Span.Slice(0, len).CopyTo(_buffer.AsSpan(_writePos));
                _writePos += len;

                if (_writePos != _buffer.Length)
                {
                    return;
                }

                await _baseStream.WriteAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _writePos = 0;

                buffer = buffer.Slice(len);
            }

            if (buffer.Length >= _buffer.Length)
            {
                await _baseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (buffer.Length > 0)
            {
                buffer.Span.CopyTo(_buffer);
                _writePos = buffer.Length;
            }
        }

        /// <inheritdoc/>
        public async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            for (int i = 0, count = buffers.Count; i < count; ++i)
            {
                ReadOnlyMemory<byte> buffer = buffers[i];

                if (_writePos != 0)
                {
                    int len = Math.Min(buffer.Length, _buffer.Length - _writePos);

                    buffer.Span.Slice(0, len).CopyTo(_buffer.AsSpan(_writePos));
                    _writePos += len;

                    if (_writePos != _buffer.Length)
                    {
                        continue;
                    }

                    await _baseStream.WriteAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    _writePos = 0;

                    buffer = buffer.Slice(len);
                }

                if (buffer.Length >= _buffer.Length)
                {
                    await _baseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (buffer.Length > 0)
                {
                    buffer.Span.CopyTo(_buffer);
                    _writePos = buffer.Length;
                }
            }
        }

        /// <inheritdoc/>
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);

        /// <inheritdoc/>
        public override void EndWrite(IAsyncResult asyncResult) =>
            TaskToApm.End(asyncResult);

        /// <inheritdoc/>
        public override void CopyTo(Stream destination, int bufferSize) =>
            _baseStream.CopyTo(destination, bufferSize);

        /// <inheritdoc/>
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            _baseStream.CopyToAsync(destination, bufferSize, cancellationToken);
    }
}
