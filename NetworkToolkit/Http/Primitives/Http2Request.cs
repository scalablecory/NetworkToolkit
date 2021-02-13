using NetworkToolkit.Parsing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    internal sealed class Http2Request : HttpRequest
    {
        private readonly ConcurrentQueue<Frame> _frames = new ConcurrentQueue<Frame>();
        private ReadState _state;
        private int _processing;

        public void OnStatus(int statusCode)
        {
        }

        public void OnHeaders(bool endHeaders, bool endStream, CountedSegment segment)
        {
            segment = segment.Slice();

            int frameData =
                Frame.FrameTypeHeaders
                | (endHeaders ? Frame.EndHeaders : 0)
                | (endStream ? Frame.EndStream : 0);

            AddNewFrame(new Frame(frameData, errorCode: 0u, segment));
        }

        public void OnData(bool endStream, CountedSegment segment)
        {
            segment = segment.Slice();

            int frameData =
                Frame.FrameTypeData
                | (endStream ? Frame.EndStream : 0);

            AddNewFrame(new Frame(frameData, errorCode: 0u, segment));
        }

        public void OnRstStream(uint errorCode)
        {
            AddNewFrame(new Frame(Frame.FrameTypeRstStream, errorCode, segment: default));
        }

        public void OnGoAway(uint errorCode)
        {
            AddNewFrame(new Frame(Frame.FrameTypeGoAway, errorCode, segment: default));
        }

        private void AddNewFrame(in Frame frame)
        {
            _frames.Enqueue(frame);

            if (Interlocked.Exchange(ref _processing, 1) == 0)
            {
                ProcessFrames();
            }
        }

        private void ProcessFrames()
        {
            try
            {
                do
                {
                    while (_frames.TryDequeue(out Frame result))
                    {
                        ProcessFrame(result);
                    }

                    Volatile.Write(ref _processing, 0);
                }
                while (!_frames.IsEmpty && Interlocked.Exchange(ref _processing, 1) == 0);
            }
            catch (Exception ex)
            {
                // TODO: set connection exception.
            }
        }

        private void ProcessFrame(in Frame frame)
        {
        }

        protected internal override ValueTask DisposeAsync(int version, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override void ConfigureRequest(int version, long? contentLength, bool hasTrailingHeaders)
        {
            throw new NotImplementedException();
        }

        protected internal override void WriteConnectRequest(int version, ReadOnlySpan<byte> authority)
        {
            throw new NotImplementedException();
        }

        protected internal override void WriteRequest(int version, ReadOnlySpan<byte> method, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery)
        {
            throw new NotImplementedException();
        }

        protected internal override void WriteHeader(int version, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            throw new NotImplementedException();
        }

        protected internal override void WriteHeader(int version, PreparedHeaderSet headers)
        {
            throw new NotImplementedException();
        }

        protected internal override void WriteTrailingHeader(int version, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask FlushHeadersAsync(int version, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask WriteContentAsync(int version, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask WriteContentAsync(int version, IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask FlushContentAsync(int version, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask CompleteRequestAsync(int version, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask<HttpReadType> ReadAsync(int version, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask ReadHeadersAsync(int version, IHttpHeadersSink headersSink, object? state, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask<int> ReadContentAsync(int version, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected internal override ValueTask<int> ReadContentAsync(int version, IReadOnlyList<Memory<byte>> buffers, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        private readonly struct Frame
        {
            public const int FrameTypeMask = 3;
            public const int FrameTypeHeaders = 0;
            public const int FrameTypeData = 1;
            public const int FrameTypeRstStream = 2;
            public const int FrameTypeGoAway = 3;

            public const int EndHeaders = 4;
            public const int EndStream = 8;

            public CountedSegment Segment { get; }
            public int Data { get; }
            public uint ErrorCode { get; }

            public Frame(int data, uint errorCode, CountedSegment segment)
            {
                Data = data;
                ErrorCode = errorCode;
                Segment = segment;
            }
        }

        private enum ReadState
        {
            None,
            ExpectingPseudoHeaders,
            ExpectingHeaders,
            ExpectingData,
        }
    }
}
