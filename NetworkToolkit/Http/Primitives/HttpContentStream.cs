using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A <see cref="Stream"/> that reads/writes content over a <see cref="ValueHttpRequest"/>.
    /// </summary>
    public class HttpContentStream : Stream, ICancellableAsyncDisposable, IGatheringStream
    {
        internal ValueHttpRequest _request;

        private readonly bool _ownsRequest;
        private StreamState _readState;

        /// <summary>
        /// The <see cref="ValueHttpRequest"/> being operated on.
        /// </summary>
        public ValueHttpRequest Request => _request;

        /// <inheritdoc/>
        public override bool CanRead => _readState < StreamState.EndOfStream;

        /// <inheritdoc/>
        public override bool CanWrite => _readState != StreamState.Disposed;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override long Length => throw new InvalidOperationException();

        /// <inheritdoc/>
        public override long Position { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }

        /// <summary>
        /// Instantiates a new <see cref="HttpContentStream"/>.
        /// </summary>
        /// <param name="request">The <see cref="ValueHttpRequest"/> to operate on.</param>
        /// <param name="ownsRequest">If true, the <paramref name="request"/> will be disposed once the <see cref="HttpContentStream"/> is disposed.</param>
        public HttpContentStream(ValueHttpRequest request, bool ownsRequest)
        {
            _request = request;
            _ownsRequest = ownsRequest;
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing && _readState != StreamState.Disposed)
            {
                if (_ownsRequest)
                {
                    Tools.BlockForResult(_request.DisposeAsync());
                }
                _readState = StreamState.Disposed;
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc/>
        public override ValueTask DisposeAsync() =>
            DisposeAsync(CancellationToken.None);

        /// <inheritdoc/>
        public virtual async ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            if (_readState != StreamState.Disposed)
            {
                if (_ownsRequest)
                {
                    await _request.DisposeAsync(cancellationToken).ConfigureAwait(false);
                }
                _readState = StreamState.Disposed;
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            if (_readState == StreamState.Disposed)
            {
                throw new ObjectDisposedException(nameof(HttpContentStream));
            }

            try
            {
                Tools.BlockForResult(_request.FlushContentAsync());
            }
            catch (Exception ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }

        /// <inheritdoc/>
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_readState == StreamState.Disposed)
            {
                throw new ObjectDisposedException(nameof(HttpContentStream));
            }

            try
            {
                await _request.FlushContentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new InvalidOperationException();

        /// <inheritdoc/>
        public override void SetLength(long value) =>
            throw new InvalidOperationException();

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            switch (_readState)
            {
                case StreamState.EndOfStream:
                    return 0;
                case StreamState.Disposed:
                    throw new ObjectDisposedException(GetType().Name);
            }

            try
            {
                int len;

                while((len = await _request.ReadContentAsync(buffer, cancellationToken).ConfigureAwait(false)) == 0)
                {
                    if (!await _request.ReadToNextContentAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _readState = StreamState.EndOfStream;
                        return 0;
                    }
                }

                return len;
            }
            catch (Exception ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc/>
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);

        /// <inheritdoc/>
        public override int EndRead(IAsyncResult asyncResult) =>
            TaskToApm.End<int>(asyncResult);

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) =>
            Tools.BlockForResult(ReadAsync(buffer.AsMemory(offset, count)));

        /// <inheritdoc/>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_readState == StreamState.Disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            try
            {
                await _request.WriteContentAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }

        /// <inheritdoc/>
        public virtual async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            if (_readState == StreamState.Disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            try
            {
                await _request.WriteContentAsync(buffers, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }

        /// <inheritdoc/>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <inheritdoc/>
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);

        /// <inheritdoc/>
        public override void EndWrite(IAsyncResult asyncResult) =>
            TaskToApm.End(asyncResult);

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) =>
            Tools.BlockForResult(WriteAsync(buffer.AsMemory(offset, count)));

        private enum StreamState : byte
        {
            Reading,
            EndOfStream,
            Disposed
        }
    }
}
