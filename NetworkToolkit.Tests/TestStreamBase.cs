using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests
{
    internal abstract class TestStreamBase : Stream, IScatterGatherStream, ICompletableStream, ICancellableAsyncDisposable
    {
        public bool CanScatterGather => true;
        public virtual bool CanCompleteWrites => false;

        public override bool CanRead => false;

        public override bool CanWrite => false;

        public override bool CanSeek => false;
        public override long Length => throw new InvalidOperationException();
        public override long Position { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }

        public sealed override ValueTask DisposeAsync() =>
            DisposeAsync(CancellationToken.None);

        public virtual ValueTask DisposeAsync(CancellationToken cancellationToken) =>
            default;

        public virtual ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(ExceptionDispatchInfo.SetCurrentStackTrace(new InvalidOperationException()));

        public override void Flush() => throw new NotImplementedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

        public override void SetLength(long value) => throw new NotImplementedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(ExceptionDispatchInfo.SetCurrentStackTrace(new InvalidOperationException()));

        public virtual ValueTask<int> ReadAsync(IReadOnlyList<Memory<byte>> buffers, CancellationToken cancellationToken = default) =>
            ReadAsync(buffers.Count != 0 ? buffers[0] : default, cancellationToken);

        public sealed override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public sealed override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);

        public sealed override int EndRead(IAsyncResult asyncResult) =>
            TaskToApm.End<int>(asyncResult);

        public sealed override int Read(byte[] buffer, int offset, int count) =>
            Tools.BlockForResult(ReadAsync(buffer.AsMemory(offset, count)));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(ExceptionDispatchInfo.SetCurrentStackTrace(new InvalidOperationException()));

        public virtual async ValueTask WriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken = default)
        {
            for (int i = 0, count = buffers.Count; i != count; ++i)
            {
                await WriteAsync(buffers[i], cancellationToken).ConfigureAwait(false);
            }
        }

        public sealed override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public sealed override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
            TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);

        public sealed override void EndWrite(IAsyncResult asyncResult) =>
            TaskToApm.End(asyncResult);

        public sealed override void Write(byte[] buffer, int offset, int count) =>
            Tools.BlockForResult(WriteAsync(buffer.AsMemory(offset, count)));
    }
}
