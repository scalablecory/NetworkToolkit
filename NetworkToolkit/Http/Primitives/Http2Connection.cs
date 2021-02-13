using NetworkToolkit.Connections;
using NetworkToolkit.Parsing;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    public class Http2Connection : HttpBaseConnection
    {
        private Connection _connection;
        private Stream _stream;
        private IScatterGatherStream _scatterGatherStream;

        private ParseState _parseState;
        private int _parsePayloadLeft, _parsePaddingLeft, _parseStreamId;
        private bool _parseHeadersDone, _parseStreamDone;

        private CancellationTokenSource _cancellationTokenSource;
        private readonly CancellationToken _cancellationToken;
        private readonly Task _receiveLoopTask;

        public override HttpConnectionStatus Status => throw new NotImplementedException();

        /// <summary>
        /// If true, asynchronous method will be allowed to complete inline.
        /// Depending on workload this may result in a performance increase.
        /// Care must be taken to ensure HTTP/2 streams do not have inter-dependencies, or a deadlock may occur.
        /// </summary>
        public bool UnsafeInlineCompletion { get; init; }

        public Http2Connection(Connection connection)
        {
            _connection = connection;

            if (connection.Stream is IScatterGatherStream { CanScatterGather: true } scatterGatherStream)
            {
                _stream = connection.Stream;
                _scatterGatherStream = scatterGatherStream;
            }
            else
            {
                var bufferingStream = new WriteBufferingStream(connection.Stream);
                _stream = bufferingStream;
                _scatterGatherStream = bufferingStream;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationToken = _cancellationTokenSource.Token;
            _receiveLoopTask = ReceiveLoopAsync();
        }

        public override ValueTask<ValueHttpRequest?> CreateNewRequestAsync(HttpPrimitiveVersion version, HttpVersionPolicy versionPolicy, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public override ValueTask PrunePoolsAsync(long curTicks, TimeSpan lifetimeLimit, TimeSpan idleLimit, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        private async Task ReceiveLoopAsync()
        {
            var memoryBuffers = new List<Memory<byte>>();
            var buffer = new CountedBuffer(16 * 1024);

            {
                var e = buffer.GetAvailableSegments();
                do memoryBuffers.Add(e.Current.Memory);
                while (e.MoveToNextSegment());
            }
            ValueTask<int> readTask = _scatterGatherStream.ReadAsync(memoryBuffers, _cancellationToken);

            while (true)
            {
                int readLen = await readTask.ConfigureAwait(false);
                memoryBuffers.Clear();

                if (readLen == 0)
                {
                    // EOF reached.

                    if (buffer.ActiveLength != 0)
                    {
                        // Spare bytes in the stream but not enough to read a frame.
                        throw new Exception("Connection finished incomplete or corrupt.");
                    }

                    // TODO: buffer.ActiveLength == 0 and EOF. Close out the connection.
                    return;
                }

                buffer.Commit(readLen);

                {
                    var e = buffer.GetAvailableSegments();
                    do memoryBuffers.Add(e.Current.Memory);
                    while (e.MoveToNextSegment());
                }
                readTask = _scatterGatherStream.ReadAsync(memoryBuffers, _cancellationToken);

                ProcessReceiveLoop(buffer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessReceiveLoop(CountedBuffer buffer)
        {
            Span<byte> tmpBuffer = stackalloc byte[24];

            CountedBufferEnumerator e = buffer.ActiveSegments;
            do
            {
                CountedSegment s = e.Current;

                int consumed = ProcessReceiveLoop(s, s.Span, crossingSegmentBoundary: false);

                if (consumed == 0)
                {
                    return;
                }

                e.MoveForward(consumed);

                if (consumed < s.Length && e.RemainingBytes > e.RemainingSegmentBytes)
                {
                    // didn't consume the whole segment, and there is a segment available after this segment.
                    // to move past the segment seam, copy the sean into a temp buffer and process a single receive loop.

                    Span<byte> seamBuffer = tmpBuffer[..Math.Min(tmpBuffer.Length, e.RemainingBytes)];
                    e.PeekBytes(seamBuffer);

                    consumed = ProcessReceiveLoop(s, seamBuffer, crossingSegmentBoundary: true);

                    if (consumed == 0)
                    {
                        return;
                    }

                    e.MoveForward(consumed);
                }
            }
            while (e.RemainingBytes != 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] // remove some dead code from hot path -- have crossingSegmentBoundary be evaluated compile-time.
        private int ProcessReceiveLoop(in CountedSegment segment, ReadOnlySpan<byte> span, bool crossingSegmentBoundary)
        {
            ParseState state = _parseState;
            int payloadLength = _parsePayloadLeft, paddingLength = _parsePaddingLeft, streamId = _parseStreamId;
            bool headersDone = _parseHeadersDone, streamDone = _parseStreamDone;
            int originalLength = span.Length;

            Debug.Assert(crossingSegmentBoundary == false || state == ParseState.ReadFrame, $"{nameof(ProcessReceiveLoop)} should only ever be reading a frame when crossing boundary.");

            switch (state)
            {
                case ParseState.ReadFrame:
                readFrame:
                    if (span.Length < Http2Frame.FrameHeaderLength)
                    {
                        goto done;
                    }

                    int newPayloadLength = (int)(BinaryPrimitives.ReadUInt32BigEndian(span) >> 24);
                    byte type = span[3];
                    byte flags = span[4];
                    int newStreamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(span[5..]) & 0x7FFFFFFFu);

                    switch (type)
                    {
                        case Http2Frame.DataFrame:
                            if (span.Length < Http2Frame.DataFrameHeaderLength)
                            {
                                goto done;
                            }

                            // TODO: check bits and dynamically read.

                            paddingLength = span[9];
                            payloadLength = newPayloadLength - (Http2Frame.DataFrameHeaderLength - Http2Frame.FrameHeaderLength) - paddingLength;
                            streamId = newStreamId;
                            span = span[Http2Frame.DataFrameHeaderLength..];

                            state = ParseState.DataChunk;
                            if (!crossingSegmentBoundary) goto dataChunk;
                            else goto done;
                        case Http2Frame.HeadersFrame:
                            if (span.Length < Http2Frame.HeadersFrameHeaderLength)
                            {
                                goto done;
                            }

                            // TODO: check bits and dynamically read.

                            paddingLength = span[9];
                            payloadLength = newPayloadLength - (Http2Frame.HeadersFrameHeaderLength - Http2Frame.FrameHeaderLength) - paddingLength;
                            streamId = newStreamId;
                            span = span[Http2Frame.HeadersFrameHeaderLength..];
                            headersDone = (flags & 4) != 0;
                            streamDone = (flags & 1) != 0;

                            state = ParseState.HeadersChunk;

                            if (!crossingSegmentBoundary) goto headersChunk;
                            else goto done;
                        case Http2Frame.ResetStreamFrame:
                            if (payloadLength != 4)
                            {
                                goto error;
                            }

                            if (span.Length < Http2Frame.RstStreamFrameLength)
                            {
                                goto done;
                            }

                            uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(span[9..]);

                            OnRstStream(newStreamId, errorCode);

                            span = span[Http2Frame.RstStreamFrameLength..];

                            if (!crossingSegmentBoundary) goto readFrame;
                            else goto done;
                        case Http2Frame.SettingsFrame:
                            span = span[Http2Frame.FrameHeaderLength..];

                            if ((flags & 1) == 1)
                            {
                                if (newPayloadLength != 0)
                                {
                                    goto error;
                                }

                                OnSettingsAck();

                                if (!crossingSegmentBoundary) goto readFrame;
                                else goto done;
                            }

                            payloadLength = newPayloadLength;

                            state = ParseState.SettingsChunk;

                            if (crossingSegmentBoundary) goto settingsChunk;
                            else goto done;
                        case Http2Frame.PushPromiseFrame:
                            // TODO: customize error for push promise.
                            goto error;
                        case Http2Frame.PingFrame:
                            if (payloadLength != 8)
                            {
                                goto error;
                            }

                            if (span.Length < Http2Frame.PingFrameLength)
                            {
                                goto done;
                            }

                            ulong opaquePingData = BinaryPrimitives.ReadUInt64BigEndian(span[9..]);
                            OnPing(opaquePingData);

                            span = span[Http2Frame.PingFrameLength..];

                            if (!crossingSegmentBoundary) goto readFrame;
                            else goto done;
                        case Http2Frame.GoAwayFrame:
                            if (payloadLength < 8)
                            {
                                goto error;
                            }

                            if (span.Length < Http2Frame.GoAwayFrameHeaderLength)
                            {
                                goto done;
                            }

                            int lastStreamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(span[9..]) & 0x7FFFFFFFu);
                            errorCode = BinaryPrimitives.ReadUInt32BigEndian(span[13..]);
                            OnGoAway(lastStreamId, errorCode);

                            payloadLength = newPayloadLength - 8;
                            span = span[Http2Frame.GoAwayFrameHeaderLength..];

                            state = ParseState.SkipFrame;

                            if (!crossingSegmentBoundary) goto skipFrame;
                            else goto done;
                        case Http2Frame.WindowUpdateFrame:
                            if (payloadLength != 4)
                            {
                                goto error;
                            }

                            if (span.Length < Http2Frame.WindowUpdateFrameLength)
                            {
                                goto done;
                            }

                            int windowSizeIncrement = (int)(BinaryPrimitives.ReadUInt32BigEndian(span[9..]) & 0x7FFFFFFFu);
                            OnWindowSizeUpdate(newStreamId, windowSizeIncrement);

                            span = span[Http2Frame.WindowUpdateFrameLength..];

                            if (!crossingSegmentBoundary) goto readFrame;
                            else goto done;
                        case Http2Frame.ContinuationFrame:
                            if (newStreamId != streamId)
                            {
                                goto error;
                            }

                            if (span.Length < Http2Frame.HeadersFrameHeaderLength)
                            {
                                goto done;
                            }

                            paddingLength = span[9];
                            payloadLength = newPayloadLength - (Http2Frame.HeadersFrameHeaderLength - Http2Frame.FrameHeaderLength) - paddingLength;
                            span = span[Http2Frame.HeadersFrameHeaderLength..];

                            state = ParseState.HeadersChunk;

                            if (!crossingSegmentBoundary) goto headersChunk;
                            else goto done;
                        default:
                            paddingLength = 0;
                            payloadLength = newPayloadLength;
                            span = span[Http2Frame.HeadersFrameHeaderLength..];

                            state = ParseState.SkipFrame;

                            if (!crossingSegmentBoundary) goto skipFrame;
                            else goto done;
                    }
                case ParseState.HeadersChunk when !crossingSegmentBoundary:
                headersChunk:
                    if (span.Length == 0)
                    {
                        goto done;
                    }

                    if (payloadLength != 0)
                    {
                        int take = Math.Min(span.Length, payloadLength);

                        payloadLength -= take;
                        OnHeaders(streamId, headersDone && payloadLength == 0, streamDone && take == payloadLength, segment.UncountedSlice(segment.Length - span.Length, take));
                        span = span[take..];

                        if (payloadLength != 0)
                        {
                            goto done;
                        }
                    }

                    if (paddingLength != 0)
                    {
                        goto skipFrame;
                    }

                    state = ParseState.ReadFrame;
                    goto readFrame;
                case ParseState.DataChunk when !crossingSegmentBoundary:
                dataChunk:
                    if (span.Length == 0)
                    {
                        goto done;
                    }

                    if (payloadLength != 0)
                    {
                        int take = Math.Min(span.Length, payloadLength);

                        payloadLength -= take;
                        OnData(streamId, streamDone && payloadLength == 0, segment.UncountedSlice(segment.Length - span.Length, take));
                        span = span[take..];

                        if (payloadLength != 0)
                        {
                            goto done;
                        }
                    }

                    if (paddingLength != 0)
                    {
                        state = ParseState.SkipFrame;
                        goto skipFrame;
                    }

                    state = ParseState.ReadFrame;
                    goto readFrame;
                case ParseState.SettingsChunk when !crossingSegmentBoundary:
                settingsChunk:
                    while (payloadLength >= Http2Frame.SettingLength)
                    {
                        if (span.Length < Http2Frame.SettingLength)
                        {
                            goto done;
                        }

                        ushort settingId = BinaryPrimitives.ReadUInt16BigEndian(span);
                        uint settingValue = BinaryPrimitives.ReadUInt32BigEndian(span[2..]);

                        payloadLength -= Http2Frame.SettingLength;
                        span = span[Http2Frame.SettingLength..];

                        OnSetting(settingId, settingValue, payloadLength >= Http2Frame.SettingLength);
                    }

                    if (payloadLength != 0)
                    {
                        goto error;
                    }

                    state = ParseState.ReadFrame;
                    goto readFrame;
                case ParseState.SkipFrame when !crossingSegmentBoundary:
                skipFrame:
                    if (payloadLength <= span.Length)
                    {
                        span = span[payloadLength..];
                        state = ParseState.ReadFrame;
                        goto readFrame;
                    }

                    span = Span<byte>.Empty;
                    goto done;
                default:
                error:
                    throw new Exception("Invalid parse state.");
            }

        done:
            _parseState = state;
            _parsePayloadLeft = payloadLength;
            _parsePaddingLeft = paddingLength;
            _parseStreamId = streamId;
            _parseHeadersDone = headersDone;
            _parseStreamDone = streamDone;
            return originalLength - span.Length;
        }

        enum ParseState
        {
            ReadFrame,
            DataChunk,
            HeadersChunk,
            SettingsChunk,
            SkipFrame
        }

        private void OnHeaders(int streamId, bool endHeaders, bool endStream, CountedSegment segment)
        {
        }

        private void OnData(int streamId, bool endStream, CountedSegment segment)
        {
        }

        private void OnSetting(ushort settingId, uint settingValue, bool endSettings)
        {
        }

        private void OnSettingsAck()
        {
        }

        private void OnRstStream(int streamId, uint errorCode)
        {
        }

        private void OnPing(ulong opaquePingData)
        {
        }

        private void OnGoAway(int lastStreamId, uint errorCode)
        {
        }

        private void OnWindowSizeUpdate(int streamId, int windowSizeIncrement)
        {
        }
    }
}
