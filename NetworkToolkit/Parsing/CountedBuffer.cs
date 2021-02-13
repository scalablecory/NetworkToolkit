using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NetworkToolkit.Parsing
{
    internal sealed class CountedBuffer
    {
        private readonly int _segmentLength;

        private Segment? _first, _last, _availableFirst;
        private int _activeOffset, _activeLength, _availableOffset;
        private volatile int _availableLength;

        public int ActiveLength => _activeLength;
        public int AvailableLength => _availableLength;

        public CountedSegment FirstActive
        {
            get
            {
                Debug.Assert(_first != null);
                return new CountedSegment(_first, _activeOffset, _segmentLength - _activeOffset);
            }
        }

        public CountedBufferEnumerator ActiveSegments
        {
            get
            {
                Debug.Assert(_first != null);
                return new CountedBufferEnumerator(_first, _activeOffset, _activeLength);
            }
        }

        public CountedBuffer(int segmentLength)
        {
            _first = _last = _availableFirst = new Segment(this, segmentLength);
            _segmentLength = segmentLength;
            _availableLength = segmentLength;
        }

        public CountedBufferEnumerator GetAvailableSegments()
        {
            lock (this)
            {
                Debug.Assert(_availableFirst != null);
                EnsureSegmentAvailable();
                return new CountedBufferEnumerator(_availableFirst, _availableOffset, _availableLength);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> PeekActiveBytes(Span<byte> tempStorage)
        {
            Debug.Assert(tempStorage.Length != 0);
            Debug.Assert(tempStorage.Length <= _activeLength);
            Debug.Assert(_first != null);

            Span<byte> span = FirstActive.Span;

            if (span.Length < tempStorage.Length)
            {
                PeekActiveBytesSlow(tempStorage);
                span = tempStorage;
            }

            return span;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PeekActiveBytesSlow(Span<byte> tempStorage)
        {
            Debug.Assert(tempStorage.Length != 0);
            Debug.Assert(tempStorage.Length <= _activeLength);
            Debug.Assert(_first != null);

            Segment iter = _first;
            int iterOffset = _activeOffset;

            do
            {
                int take = Math.Min(_segmentLength - iterOffset, tempStorage.Length);

                iter.Buffer.AsSpan(_activeOffset, take).CopyTo(tempStorage);
                tempStorage = tempStorage.Slice(take);

                iterOffset += take;
                if (iterOffset == _segmentLength)
                {
                    Debug.Assert(iter.Next != null);
                    iter = iter.Next;
                    iterOffset = 0;
                }
            }
            while (tempStorage.Length != 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureSegmentAvailable()
        {
            if (_availableLength >= _segmentLength)
            {
                return;
            }

            EnsureSegmentAvailableSlow();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EnsureSegmentAvailableSlow()
        {
            var seg = new Segment(this, _segmentLength);

            if (_last != null)
            {
                _last.Next = seg;
            }
            else
            {
                _first = seg;
            }

            _last = seg;
            _availableFirst ??= seg;
            _availableLength += _segmentLength;
        }

        public void Commit(int length)
        {
            Debug.Assert(length >= 0);
            Debug.Assert(length <= _availableLength);

            _activeLength += length;

            lock (this)
            {
                _availableLength -= length;

                do
                {
                    int left = _segmentLength - _availableOffset;
                    if (length < left)
                    {
                        _availableOffset += length;
                        return;
                    }

                    _availableOffset = 0;
                    _availableFirst = _availableFirst!.Next;

                    length -= left;
                }
                while (length != 0);
            }
        }

        public void Discard(int length)
        {
            Debug.Assert(length >= 0);
            Debug.Assert(length <= _activeLength);

            _activeLength -= length;

            bool monitorEntered = false;
            try
            {
                do
                {
                    int left = _segmentLength - _activeOffset;

                    if (length < left)
                    {
                        _activeOffset += length;
                        return;
                    }

                    _activeOffset = 0;

                    if (!monitorEntered)
                    {
                        Monitor.Enter(this, ref monitorEntered);
                    }

                    _first = _first!.Next;

                    if (_first == null)
                    {
                        Debug.Assert(length == 0);
                        _last = null;
                        return;
                    }

                    length -= left;
                }
                while (length != 0);
            }
            finally
            {
                if (monitorEntered)
                {
                    Monitor.Exit(this);
                }
            }
        }

        private void AddFreeSegment(Segment segment)
        {
            lock (this)
            {
                _availableLength += _segmentLength;

                if (_last != null)
                {
                    _last.Next = segment;
                }
                else
                {
                    _first = segment;
                }

                _last = segment;

                if (_availableFirst == null)
                {
                    _availableFirst = segment;
                }
            }
        }

        internal sealed class Segment
        {
            private readonly CountedBuffer _owningBuffer;
            private readonly byte[] _buffer;
            private int _references;

            public byte[] Buffer => _buffer;
            public Segment? Next { get; set; }

            public Segment(CountedBuffer owningBuffer, int segmentLength)
            {
                _owningBuffer = owningBuffer;
                _buffer = GC.AllocateUninitializedArray<byte>(segmentLength, pinned: true);
                _references = 1;
            }

            public void AddRef()
            {
                int newReferences = Interlocked.Increment(ref _references);
                Debug.Assert(newReferences > 1);
            }

            public void RemoveRef()
            {
                int newReferences = Interlocked.Decrement(ref _references);
                Debug.Assert(newReferences >= 0);

                if (newReferences == 0)
                {
                    Volatile.Write(ref _references, 1);
                    _owningBuffer.AddFreeSegment(this);
                }
            }
        }
    }

    /// <summary>
    /// Enumerates segments of the buffer.
    /// Mutating the buffer will invalidate this enumerator.
    /// </summary>
    /// <remarks>
    /// Unlike an IEnumerator implementation, this enumerate comes with <see cref="Current"/> already set to the first value.
    /// </remarks>
    internal struct CountedBufferEnumerator
    {
        private CountedBuffer.Segment _segment;
        private int _offset, _totalLength;

        public int RemainingBytes =>
            _totalLength;

        public int RemainingSegmentBytes =>
            Math.Min(_segment.Buffer.Length - _offset, _totalLength);

        public CountedSegment Current =>
            new(_segment, _offset, RemainingSegmentBytes);

        internal CountedBufferEnumerator(CountedBuffer.Segment segment, int offset, int totalLength)
        {
            Debug.Assert(totalLength != 0);
            _segment = segment;
            _offset = offset;
            _totalLength = totalLength;
        }

        public bool MoveToNextSegment()
        {
            Debug.Assert(_totalLength != 0);
            Debug.Assert(_segment != null);

            _totalLength -= RemainingSegmentBytes;

            if (_totalLength == 0)
            {
                return false;
            }

            Debug.Assert(_segment.Next != null);
            _segment = _segment.Next;
            _offset = 0;
            return true;
        }

        public void MoveForward(int bytes)
        {
            Debug.Assert(_segment != null);
            Debug.Assert(_totalLength != 0);
            Debug.Assert(bytes > 0);
            Debug.Assert(bytes <= _totalLength);

            while (true)
            {
                int remainingBytes = RemainingSegmentBytes;
                if (bytes < remainingBytes)
                {
                    _offset += bytes;
                    _totalLength -= bytes;
                    return;
                }

                _totalLength -= remainingBytes;
                if (_totalLength == 0)
                {
                    return;
                }

                bytes -= remainingBytes;
                _offset = 0;

                Debug.Assert(_segment.Next != null);
                _segment = _segment.Next;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> ReadBytes(Span<byte> tempStorage)
        {
            Debug.Assert(tempStorage.Length != 0);
            Debug.Assert(tempStorage.Length <= _totalLength);
            Debug.Assert(_segment != null);

            Span<byte> s = Current.Span;
            if (s.Length >= tempStorage.Length)
            {
                tempStorage = s;
            }
            else
            {
                PeekBytes(tempStorage);
            }

            MoveForward(tempStorage.Length);

            return tempStorage;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PeekBytes(Span<byte> tempStorage)
        {
            Debug.Assert(tempStorage.Length != 0);
            Debug.Assert(tempStorage.Length <= _totalLength);
            Debug.Assert(_segment != null);

            CountedBuffer.Segment iter = _segment;
            int iterOffset = _offset;
            int totalRemaining = _totalLength;

            while (true)
            {
                int segmentRemaining = Math.Min(iter.Buffer.Length - iterOffset, totalRemaining);
                int take = Math.Min(segmentRemaining, tempStorage.Length);

                iter.Buffer.AsSpan(_offset, take).CopyTo(tempStorage);

                if (take == tempStorage.Length)
                {
                    return;
                }

                Debug.Assert(iter.Next != null);
                iter = iter.Next;
                iterOffset = 0;
                totalRemaining -= take;
                tempStorage = tempStorage[take..];
            }
        }
    }

    internal readonly struct CountedSegment : IDisposable
    {
        private readonly CountedBuffer.Segment? _segment;
        private readonly int _offset, _count;

        public int Length => _count;

        public ArraySegment<byte> Array
        {
            get
            {
                Debug.Assert(_segment != null);
                return new ArraySegment<byte>(_segment.Buffer, _offset, _count);
            }
        }

        public Span<byte> Span
        {
            get
            {
                Debug.Assert(_segment != null);
                return new Span<byte>(_segment.Buffer, _offset, _count);
            }
        }

        public Memory<byte> Memory
        {
            get
            {
                Debug.Assert(_segment != null);
                return new Memory<byte>(_segment.Buffer, _offset, _count);
            }
        }

        internal CountedSegment(CountedBuffer.Segment segment, int offset, int count)
        {
            Debug.Assert(offset <= segment.Buffer.Length);
            Debug.Assert(segment.Buffer.Length - offset >= count);

            _segment = segment;
            _offset = offset;
            _count = count;
        }

        public CountedSegment Slice()
        {
            Debug.Assert(_segment != null);
            _segment.AddRef();
            return new CountedSegment(_segment, _offset, _count);
        }

        public CountedSegment Slice(int offset)
        {
            Debug.Assert(_segment != null);
            Debug.Assert(offset >= 0);
            Debug.Assert(_count - offset >= 0);

            _segment.AddRef();
            return new CountedSegment(_segment, _offset + offset, _count - offset);
        }

        public CountedSegment Slice(int offset, int count)
        {
            Debug.Assert(_segment != null);
            Debug.Assert(offset >= 0);
            Debug.Assert(count >= 0);
            Debug.Assert(_count - offset >= count);

            _segment.AddRef();
            return new CountedSegment(_segment, _offset + offset, count);
        }

        public CountedSegment UncountedSlice()
        {
            Debug.Assert(_segment != null);
            return new CountedSegment(_segment, _offset, _count);
        }

        public CountedSegment UncountedSlice(int offset)
        {
            Debug.Assert(_segment != null);
            Debug.Assert(offset >= 0);
            Debug.Assert(_count - offset >= 0);

            return new CountedSegment(_segment, _offset + offset, _count - offset);
        }

        public CountedSegment UncountedSlice(int offset, int count)
        {
            Debug.Assert(_segment != null);
            Debug.Assert(offset >= 0);
            Debug.Assert(count >= 0);
            Debug.Assert(_count - offset >= count);

            return new CountedSegment(_segment, _offset + offset, count);
        }

        public void Dispose()
        {
            if (_segment is not null)
            {
                _segment.RemoveRef();
                Unsafe.AsRef(_segment) = null!;
            }
        }
    }
}
