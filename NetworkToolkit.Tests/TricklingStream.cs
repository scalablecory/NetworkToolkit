using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests
{
    internal sealed class TricklingStream : TestStreamBase
    {
        private readonly Stream _baseStream;
        private readonly int[] _trickleSequence;
        private readonly bool _forceAsync;
        private int _readIdx;

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanWrite => _baseStream.CanWrite;
        public override bool CanCompleteWrites => _baseStream is ICompletableStream s && s.CanCompleteWrites;

        public TricklingStream(Stream baseStream, IEnumerable<int> trickleSequence, bool forceAsync)
        {
            _baseStream = baseStream;
            _trickleSequence = trickleSequence.ToArray();
            _forceAsync = forceAsync;
            Debug.Assert(_trickleSequence.Length > 0);
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing) _baseStream.Dispose();
        }

        public override ValueTask DisposeAsync(CancellationToken cancellationToken) =>
            _baseStream.DisposeAsync(cancellationToken);

        public override async ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
        {
            if (_baseStream is ICompletableStream s && s.CanCompleteWrites)
            {
                await s.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        public override void Flush() =>
            _baseStream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _baseStream.FlushAsync(cancellationToken);

        private int NextReadSize()
        {
            int size = _trickleSequence[_readIdx];
            _readIdx = (_readIdx + 1) % _trickleSequence.Length;
            return size;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int readLength = Math.Min(buffer.Length, NextReadSize());

            ValueTask<int> readTask = _baseStream.ReadAsync(buffer.Slice(0, readLength), cancellationToken);

            if (readTask.IsCompleted && _forceAsync)
            {
                await Task.Yield();
            }

            return await readTask.ConfigureAwait(false);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _baseStream.WriteAsync(buffer, cancellationToken);
    }
}
