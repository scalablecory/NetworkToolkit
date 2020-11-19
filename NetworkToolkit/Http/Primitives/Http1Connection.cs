using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit.Http.Primitives
{
    /// <summary>
    /// A HTTP/1 connection.
    /// </summary>
    public partial class Http1Connection : HttpBaseConnection
    {
        private static ushort s_encodedCrLf => BitConverter.IsLittleEndian ? 0x0A0D : 0x0D0A; // "\r\n"
        private static ushort s_encodedColonSpace => BitConverter.IsLittleEndian ? 0x203A : 0x3A20; // ": "
        private static ulong s_encodedNewLineHostPrefix => BitConverter.IsLittleEndian ? 0x203A74736F480A0DUL : 0x0D0A486F73743A20UL; // "\r\nHost: "
        private static ulong s_encodedNewLineTransferEncodingChunked_0 => BitConverter.IsLittleEndian ? 0x66736E6172540A0D : 0x0D0A5472616E7366; // "\r\nTransf"
        private static ulong s_encodedNewLineTransferEncodingChunked_1 => BitConverter.IsLittleEndian ? 0x646F636E452D7265 : 0x65722D456E636F64; // "er-Encod"
        private static ulong s_encodedNewLineTransferEncodingChunked_2 => BitConverter.IsLittleEndian ? 0x756863203A676E69 : 0x696E673A20636875; // "ing: chu"
        private static uint s_encodedNewLineTransferEncodingChunked_3 => BitConverter.IsLittleEndian ? 0x64656B6E : 0x6E6B6564; // "nked"
        private static ulong s_encodedConnectPrefix => BitConverter.IsLittleEndian ? 0x205443454E4E4F43UL : 0x434F4E4E45435420UL; // "CONNECT "
        private static readonly ReadOnlyMemory<byte> s_encodedCRLFMemory = new byte[] { (byte)'\r', (byte)'\n' };
        private static ReadOnlySpan<byte> s_EncodedTransferEncodingName => new byte[] { (byte)'t', (byte)'r', (byte)'a', (byte)'n', (byte)'s', (byte)'f', (byte)'e', (byte)'r', (byte)'-', (byte)'e', (byte)'n', (byte)'c', (byte)'o', (byte)'d', (byte)'i', (byte)'n', (byte)'g' };
        private static ReadOnlySpan<byte> s_EncodedTransferEncodingChunkedValue => new byte[] { (byte)'c', (byte)'h', (byte)'u', (byte)'n', (byte)'k', (byte)'e', (byte)'d' };
        private static ReadOnlySpan<byte> s_EncodedContentLengthName => new byte[] { (byte)'c', (byte)'o', (byte)'n', (byte)'t', (byte)'e', (byte)'n', (byte)'t', (byte)'-', (byte)'l', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h' };

        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_ReadResponse = (c, r, t) => c.ReadResponseAsync(r, t);
        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_ReadToHeaders = (c, r, t) => { c._readFunc = s_SkipHeaders; r.SetCurrentReadType(HttpReadType.Headers); return new ValueTask<HttpReadType>(HttpReadType.Headers); };
        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_SkipHeaders = (c, r, t) => c.SkipHeadersAsync(r, t);
        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_ReadToContent = (c, r, t) => c.ReadToContentAsync(r);
        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_SkipContent = (c, r, t) => c.SkipContentAsync(r, t);
        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_ReadToTrailingHeaders = (c, r, t) => { c._readFunc = s_SkipTrailingHeaders; r.SetCurrentReadType(HttpReadType.TrailingHeaders); return new ValueTask<HttpReadType>(HttpReadType.TrailingHeaders); };
        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_SkipTrailingHeaders = (c, r, t) => c.SkipHeadersAsync(r, t);
        private static readonly Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>> s_ReadToEndOfStream = (c, r, t) => { r.SetCurrentReadType(HttpReadType.EndOfStream); return new ValueTask<HttpReadType>(HttpReadType.EndOfStream); };

        internal readonly Stream _stream;
        internal readonly IGatheringStream _gatheringStream;
        internal VectorArrayBuffer _readBuffer;
        internal ArrayBuffer _writeBuffer;
        private readonly List<ReadOnlyMemory<byte>> _gatheredWriteBuffer = new List<ReadOnlyMemory<byte>>(3);
        private bool _requestIsChunked, _responseHasContentLength, _responseIsChunked, _readingFirstResponseChunk;
        private WriteState _writeState;
        private ulong _responseContentBytesRemaining, _responseChunkBytesRemaining;
        private Func<Http1Connection, Http1Request, CancellationToken, ValueTask<HttpReadType>>? _readFunc;

        internal Http1Request? _request;

        private enum WriteState : byte
        {
            Unstarted,
            RequestWritten,
            HeadersWritten,
            HeadersFlushed,
            ContentWritten,
            TrailingHeadersWritten,
            Finished
        }

        /// <summary>
        /// Instantiates a new <see cref="Http1Connection"/> over a given <see cref="Stream"/>.
        /// </summary>
        /// <param name="stream">The <see cref="Stream"/> to read and write to.</param>
        public Http1Connection(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (stream is not IGatheringStream gatheringStream)
            {
                var bufferingStream = new WriteBufferingStream(stream);
                stream = bufferingStream;
                gatheringStream = bufferingStream;
            }

            _stream = stream;
            _gatheringStream = gatheringStream;
            _readBuffer = new VectorArrayBuffer(initialSize: 4096);
            _writeBuffer = new ArrayBuffer(initialSize: 64);
        }

        /// <inheritdoc/>
        public override async ValueTask DisposeAsync(CancellationToken cancellationToken)
        {
            await _stream.DisposeAsync(cancellationToken).ConfigureAwait(false);
            _readBuffer.Dispose();
            _writeBuffer.Dispose();
        }

        /// <inheritdoc/>
        public override ValueTask<ValueHttpRequest?> CreateNewRequestAsync(HttpPrimitiveVersion version, HttpVersionPolicy versionPolicy, CancellationToken cancellationToken = default)
        {
            if (_writeBuffer.ActiveLength != 0 || _responseContentBytesRemaining != 0)
            {
                return ValueTask.FromException<ValueHttpRequest?>(ExceptionDispatchInfo.SetCurrentStackTrace(new Exception("Unable to create request stream with a request already pending.")));
            }

            if (version.Major != 1)
            {
                if (versionPolicy == HttpVersionPolicy.RequestVersionOrLower)
                {
                    version = HttpPrimitiveVersion.Version11;
                }
                return ValueTask.FromException<ValueHttpRequest?>(ExceptionDispatchInfo.SetCurrentStackTrace(new Exception($"Unable to create request for HTTP/{version.Major}.{version.Minor} with a {nameof(Http1Connection)}.")));
            }

            _writeState = WriteState.Unstarted;
            _requestIsChunked = true;
            _responseHasContentLength = false;
            _responseIsChunked = false;
            _readingFirstResponseChunk = false;
            _readFunc = s_ReadResponse;

            if (Interlocked.Exchange(ref _request, null) is Http1Request request)
            {
                request.Init(this, version);
            }
            else
            {
                request = new Http1Request(this, version);
            }

            return new ValueTask<ValueHttpRequest?>(request.GetValueRequest());
        }

        /// <inheritdoc/>
        public override ValueTask PrunePoolsAsync(long curTicks, TimeSpan lifetimeLimit, TimeSpan idleLimit, CancellationToken cancellationToken = default) =>
            default;

        internal void ConfigureRequest(long? contentLength, bool hasTrailingHeaders)
        {
            _requestIsChunked = contentLength == null || hasTrailingHeaders;
        }

        internal void WriteConnectRequest(ReadOnlySpan<byte> authority, HttpPrimitiveVersion version)
        {
            if (_writeState != WriteState.Unstarted)
            {
                throw new InvalidOperationException();
            }

            if (authority.Length == 0)
            {
                throw new ArgumentException();
            }

            _requestIsChunked = false;

            int len = GetEncodeConnectRequestLength(authority);
            _writeBuffer.EnsureAvailableSpace(len);
            EncodeConnectRequest(authority, version, _writeBuffer.AvailableSpan);
            _writeBuffer.Commit(len);
            _writeState = WriteState.RequestWritten;
        }

        internal static int GetEncodeConnectRequestLength(ReadOnlySpan<byte> authority)
        {
            return authority.Length + 21; // CONNECT {authority} HTTP/x.x\r\n\r\n
        }

        internal static unsafe void EncodeConnectRequest(ReadOnlySpan<byte> authority, HttpPrimitiveVersion version, Span<byte> buffer)
        {
            Debug.Assert(authority.Length > 0);
            Debug.Assert(buffer.Length >= GetEncodeConnectRequestLength(authority));

            ref byte pBuf = ref MemoryMarshal.GetReference(buffer);

            Unsafe.WriteUnaligned(ref pBuf, s_encodedConnectPrefix); // "CONNECT "
            pBuf = ref Unsafe.Add(ref pBuf, 8);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(authority), (uint)authority.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)authority.Length);

            pBuf = (byte)' ';
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 1), version._encoded); // "HTTP/x.x"
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 9), s_encodedCrLf);
            pBuf = ref Unsafe.Add(ref pBuf, 11);

            int length = Tools.UnsafeByteOffset(buffer, ref pBuf);
            Debug.Assert(length == GetEncodeConnectRequestLength(authority));
        }

        internal void WriteRequest(ReadOnlySpan<byte> method, ReadOnlySpan<byte> scheme, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery, HttpPrimitiveVersion version)
        {
            if (_writeState != WriteState.Unstarted)
            {
                throw new InvalidOperationException();
            }

            if (method.Length == 0 || authority.Length == 0 || pathAndQuery.Length == 0 || version._encoded == 0)
            {
                throw new ArgumentException("All parameters must be specified.");
            }

            int len = GetEncodeRequestLength(method, authority, pathAndQuery);
            _writeBuffer.EnsureAvailableSpace(len);
            EncodeRequest(method, authority, pathAndQuery, version, _writeBuffer.AvailableSpan);
            _writeBuffer.Commit(len);
            _writeState = WriteState.RequestWritten;
        }

        internal int GetEncodeRequestLength(ReadOnlySpan<byte> method, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery) =>
            method.Length + pathAndQuery.Length + authority.Length + (!_requestIsChunked ? 20 : 48); // "{method} {uri} HTTP/x.x\r\nHost: {origin}\r\nTransfer-Encoding: chunked\r\n"

        internal unsafe void EncodeRequest(ReadOnlySpan<byte> method, ReadOnlySpan<byte> authority, ReadOnlySpan<byte> pathAndQuery, HttpPrimitiveVersion version, Span<byte> buffer)
        {
            Debug.Assert(authority.Length > 0);
            Debug.Assert(method.Length > 0);
            Debug.Assert(pathAndQuery.Length > 0);
            Debug.Assert(buffer.Length >= GetEncodeRequestLength(authority, method, pathAndQuery));

            ref byte pBuf = ref MemoryMarshal.GetReference(buffer);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(method), (uint)method.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)method.Length);

            pBuf = (byte)' ';
            pBuf = ref Unsafe.Add(ref pBuf, 1);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(pathAndQuery), (uint)pathAndQuery.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)pathAndQuery.Length);

            pBuf = (byte)' ';
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 1), version._encoded); // "HTTP/x.x"
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 9), s_encodedNewLineHostPrefix); // "\r\nHost: "
            pBuf = ref Unsafe.Add(ref pBuf, 17);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(authority), (uint)authority.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)authority.Length);

            if (!_requestIsChunked)
            {
                // \r\n (end of headers)
                Unsafe.WriteUnaligned(ref pBuf, s_encodedCrLf);
                pBuf = ref Unsafe.Add(ref pBuf, 2);
            }
            else
            {
                // \r\nTransfer-Encoding: chunked\r\n
                Unsafe.WriteUnaligned(ref pBuf, s_encodedNewLineTransferEncodingChunked_0);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 8), s_encodedNewLineTransferEncodingChunked_1);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 16), s_encodedNewLineTransferEncodingChunked_2);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 24), s_encodedNewLineTransferEncodingChunked_3);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pBuf, 28), s_encodedCrLf);
                pBuf = ref Unsafe.Add(ref pBuf, 30);
            }

            int length = Tools.UnsafeByteOffset(buffer, ref pBuf);
            Debug.Assert(length == GetEncodeRequestLength(authority, method, pathAndQuery));
        }

        internal void WriteHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            if (name.Length == 0)
            {
                throw new ArgumentException();
            }

            switch (_writeState)
            {
                case WriteState.Unstarted:
                    throw new InvalidOperationException($"Must call {nameof(ValueHttpRequest)}.{nameof(HttpRequest.WriteRequest)} prior to writing headers.");
                case WriteState.RequestWritten:
                    _writeState = WriteState.HeadersWritten;
                    break;
                case WriteState.HeadersWritten:
                    break;
                default:
                    throw new InvalidOperationException("Can not write headers after headers have been flushed.");
            }

            int len = GetEncodeHeaderLength(name, value);
            _writeBuffer.EnsureAvailableSpace(len);
            EncodeHeader(name, value, _writeBuffer.AvailableSpan);
            _writeBuffer.Commit(len);
        }

        internal void WriteHeader(PreparedHeaderSet headers)
        {
            if (headers == null) throw new ArgumentNullException(nameof(headers));

            switch (_writeState)
            {
                case WriteState.Unstarted:
                    throw new InvalidOperationException($"Must call {nameof(ValueHttpRequest)}.{nameof(HttpRequest.WriteRequest)} prior to writing headers.");
                case WriteState.RequestWritten:
                    _writeState = WriteState.HeadersWritten;
                    break;
                case WriteState.HeadersWritten:
                    break;
                default:
                    throw new InvalidOperationException("Can not write headers after headers have been flushed.");
            }

            byte[] headersBuffer = headers.Http1Value;

            _writeBuffer.EnsureAvailableSpace(headersBuffer.Length);
            headersBuffer.AsSpan().CopyTo(_writeBuffer.AvailableSpan);
            _writeBuffer.Commit(headersBuffer.Length);
        }

        internal void WriteTrailingHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            if (!_requestIsChunked)
            {
                throw new InvalidOperationException($"Can not write trailing headers without first enabling them during {nameof(ValueHttpRequest)}.{nameof(ValueHttpRequest.ConfigureRequest)}.");
            }

            if (name.Length == 0)
            {
                throw new ArgumentException();
            }

            switch (_writeState)
            {
                case WriteState.Unstarted:
                    throw new InvalidOperationException($"Must call {nameof(ValueHttpRequest)}.{nameof(HttpRequest.WriteRequest)} prior to writing headers.");
                case WriteState.RequestWritten:
                case WriteState.HeadersWritten:
                    WriteCRLF(); // end of headers.
                    goto case WriteState.ContentWritten;
                case WriteState.HeadersFlushed:
                case WriteState.ContentWritten:
                    // The first trailing header is being written. End chunked content with a 0-length chunk.
                    WriteChunkEnvelopeStart(0);
                    _writeState = WriteState.TrailingHeadersWritten;
                    break;
                case WriteState.TrailingHeadersWritten:
                    break;
                default: // case WriteState.Finished:
                    Debug.Assert(_writeState == WriteState.Finished);
                    throw new InvalidOperationException("Can not write headers after the request has been completed.");
            }

            int len = GetEncodeHeaderLength(name, value);
            _writeBuffer.EnsureAvailableSpace(len);
            EncodeHeader(name, value, _writeBuffer.AvailableSpan);
            _writeBuffer.Commit(len);
        }

        internal static int GetEncodeHeaderLength(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            Debug.Assert(name.Length > 0);
            return name.Length + value.Length + 4; // {name}: {value}\r\n
        }

        internal static unsafe int EncodeHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value, Span<byte> buffer)
        {
            Debug.Assert(name.Length > 0);
            Debug.Assert(buffer.Length >= GetEncodeHeaderLength(name, value));

            ref byte pBuf = ref MemoryMarshal.GetReference(buffer);

            Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(name), (uint)name.Length);
            pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)name.Length);

            Unsafe.WriteUnaligned(ref pBuf, s_encodedColonSpace);
            pBuf = ref Unsafe.Add(ref pBuf, 2);

            if (value.Length != 0)
            {
                Unsafe.CopyBlockUnaligned(ref pBuf, ref MemoryMarshal.GetReference(value), (uint)value.Length);
                pBuf = ref Unsafe.Add(ref pBuf, (IntPtr)(uint)value.Length);
            }

            Unsafe.WriteUnaligned(ref pBuf, s_encodedCrLf);
            pBuf = ref Unsafe.Add(ref pBuf, 2);

            int length = (int)(void*)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(buffer), ref pBuf);
            Debug.Assert(length == GetEncodeHeaderLength(name, value));

            return length;
        }

        private void WriteCRLF()
        {
            _writeBuffer.EnsureAvailableSpace(2);

            ref byte pBuf = ref MemoryMarshal.GetReference(_writeBuffer.AvailableSpan);
            Unsafe.WriteUnaligned(ref pBuf, s_encodedCrLf);

            _writeBuffer.Commit(2);
        }

        /// <param name="completingRequest">If true, the entire request side is completed. If false, only flushing headers.</param>
        private void FlushHeadersHelper(bool completingRequest)
        {
            switch (_writeState)
            {
                case WriteState.Unstarted:
                    throw new InvalidOperationException($"{nameof(ValueHttpRequest)}.{nameof(HttpRequest.WriteRequest)} must be called prior to calling this.");
                case WriteState.RequestWritten:
                case WriteState.HeadersWritten:
                    if (!completingRequest)
                    {
                        _writeState = WriteState.HeadersFlushed;
                        // end of headers to be written below.
                    }
                    else
                    {
                        _writeState = WriteState.Finished;
                        if (_requestIsChunked)
                        {
                            // end headers.
                            WriteCRLF();

                            // end of content.
                            WriteChunkEnvelopeStart(0);

                            // end of trailing headers to be written below.
                        }
                    }
                    break;
                case WriteState.HeadersFlushed:
                case WriteState.ContentWritten:
                    if (!_requestIsChunked)
                    {
                        if (completingRequest)
                        {
                            Debug.Assert(_writeBuffer.ActiveLength == 0);
                            _writeState = WriteState.Finished;
                            return;
                        }

                        throw new InvalidOperationException("Can not write headers after content without trailing headers being enabled.");
                    }

                    // 0-length chunk to end content is normally written with the first trailing
                    // header written, otherwise it needs to be written here.
                    WriteChunkEnvelopeStart(0);
                    _writeState = WriteState.Finished;
                    break;
                case WriteState.TrailingHeadersWritten:
                    _writeState = WriteState.Finished;
                    // end of trailing headers to be written below.
                    break;
                default: //case WriteState.Finished:
                    Debug.Assert(_writeState == WriteState.Finished);

                    if (completingRequest)
                    {
                        Debug.Assert(_writeBuffer.ActiveLength == 0);
                        return;
                    }

                    throw new InvalidOperationException("Cannot flush headers on an already completed request.");
            }

            WriteCRLF(); // CRLF to end headers.
        }

        internal async ValueTask FlushHeadersAsync(CancellationToken cancellationToken)
        {
            FlushHeadersHelper(completingRequest: false);
            await _stream.WriteAsync(_writeBuffer.ActiveMemory, cancellationToken).ConfigureAwait(false);
            _writeBuffer.Discard(_writeBuffer.ActiveLength);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal ValueTask CompleteRequestAsync(CancellationToken cancellationToken)
        {
            FlushHeadersHelper(completingRequest: true);
            if (_writeBuffer.ActiveLength != 0)
            {
                return CompleteRequestAsyncSlow(cancellationToken);
            }
            else
            {
                return new ValueTask(_stream.FlushAsync(cancellationToken));
            }
        }

        private async ValueTask CompleteRequestAsyncSlow(CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(_writeBuffer.ActiveMemory, cancellationToken).ConfigureAwait(false);
            _writeBuffer.Discard(_writeBuffer.ActiveLength);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal ValueTask WriteContentAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            return WriteContentAsync(buffer, buffers: null, cancellationToken);
        }

        internal ValueTask WriteContentAsync(IReadOnlyList<ReadOnlyMemory<byte>> buffers, CancellationToken cancellationToken)
        {
            return WriteContentAsync(buffer: default, buffers, cancellationToken);
        }

        private ValueTask WriteContentAsync(ReadOnlyMemory<byte> buffer, IReadOnlyList<ReadOnlyMemory<byte>>? buffers, CancellationToken cancellationToken)
        {
            switch (_writeState)
            {
                case WriteState.RequestWritten:
                case WriteState.HeadersWritten:
                    return WriteHeadersAndContentAsync(buffer, buffers, cancellationToken);
                case WriteState.HeadersFlushed:
                case WriteState.ContentWritten:
                    _writeState = WriteState.ContentWritten;
                    if (!_requestIsChunked)
                    {
                        if (buffers == null) return _stream.WriteAsync(buffer, cancellationToken);
                        else return _gatheringStream.WriteAsync(buffers, cancellationToken);
                    }
                    else return WriteChunkedContentAsync(buffer, buffers, cancellationToken);
                default:
                    Debug.Assert(_writeState == WriteState.TrailingHeadersWritten || _writeState == WriteState.Finished);
                    return new ValueTask(Task.FromException(ExceptionDispatchInfo.SetCurrentStackTrace(new InvalidOperationException("Content can not be written after the request has been completed or trailing headers have been written."))));
            }
        }

        private ValueTask WriteChunkedContentAsync(ReadOnlyMemory<byte> buffer, IReadOnlyList<ReadOnlyMemory<byte>>? buffers, CancellationToken cancellationToken)
        {
            ulong bufferLength = 0;

            if (buffers != null)
            {
                for (int i = 0, count = buffers.Count; i < count; ++i)
                {
                    bufferLength += (uint)buffers[i].Length;
                }
            }
            else
            {
                bufferLength = (uint)buffer.Length;
            }

            WriteChunkEnvelopeStart(bufferLength);

            _gatheredWriteBuffer.Clear();
            _gatheredWriteBuffer.Add(_writeBuffer.ActiveMemory);

            if (buffers != null)
            {
                for (int i = 0, count = buffers.Count; i < count; ++i)
                {
                    _gatheredWriteBuffer.Add(buffers[i]);
                }
            }
            else
            {
                _gatheredWriteBuffer.Add(buffer);
            }

            _gatheredWriteBuffer.Add(s_encodedCRLFMemory);

            // It looks weird to discard the buffer before sending has completed, but it works fine.
            // This is done to elide an async state machine allocation that would otherwise be needed to await the write and then discard.
            _writeBuffer.Discard(_writeBuffer.ActiveLength);

            return _gatheringStream.WriteAsync(_gatheredWriteBuffer, cancellationToken);
        }

        private void WriteChunkEnvelopeStart(ulong chunkLength)
        {
            _writeBuffer.EnsureAvailableSpace(18);

            Span<byte> buffer = _writeBuffer.AvailableSpan;

            bool success = Utf8Formatter.TryFormat(chunkLength, buffer, out int envelopeLength, format: 'X');
            Debug.Assert(success == true);

            Debug.Assert(envelopeLength <= 16);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), envelopeLength), s_encodedCrLf);

            _writeBuffer.Commit(envelopeLength + 2);
        }

        private ValueTask WriteHeadersAndContentAsync(ReadOnlyMemory<byte> buffer, IReadOnlyList<ReadOnlyMemory<byte>>? buffers, CancellationToken cancellationToken)
        {
            FlushHeadersHelper(completingRequest: false);

            if (!_requestIsChunked)
            {
                _gatheredWriteBuffer.Clear();
                _gatheredWriteBuffer.Add(_writeBuffer.ActiveMemory);

                if (buffers != null)
                {
                    for (int i = 0, count = buffers.Count; i != count; ++i)
                    {
                        _gatheredWriteBuffer.Add(buffers[i]);
                    }
                }
                else
                {
                    _gatheredWriteBuffer.Add(buffer);
                }

                // It looks weird to discard the buffer before sending has completed, but it works fine.
                // This is done to elide an async state machine allocation that would otherwise be needed to await the write and then discard.
                _writeBuffer.Discard(_writeBuffer.ActiveLength);

                return _gatheringStream.WriteAsync(_gatheredWriteBuffer, cancellationToken);
            }
            else
            {
                return WriteChunkedContentAsync(buffer, buffers, cancellationToken);
            }
        }

        internal ValueTask FlushContentAsync(CancellationToken cancellationToken)
        {
            return new ValueTask(_stream.FlushAsync(cancellationToken));
        }

        internal ValueTask<HttpReadType> ReadAsync(Http1Request requestStream, CancellationToken cancellationToken)
        {
            return _readFunc!(this, requestStream, cancellationToken);
        }

        private async ValueTask<HttpReadType> ReadResponseAsync(Http1Request requestStream, CancellationToken cancellationToken)
        {
            while (true)
            {
                HttpReadType? nodeType = TryReadResponse(requestStream);
                if (nodeType != null)
                {
                    requestStream.SetCurrentReadType(nodeType.GetValueOrDefault());
                    return nodeType.GetValueOrDefault();
                }

                _readBuffer.EnsureAvailableSpace(1);

                int readBytes = await _stream.ReadAsync(_readBuffer.AvailableMemory, cancellationToken).ConfigureAwait(false);
                if (readBytes == 0) throw new Exception("Unexpected EOF");

                _readBuffer.Commit(readBytes);
            }
        }

        private HttpReadType? TryReadResponse(Http1Request requestStream)
        {
            if (_readBuffer.ActiveLength == 0)
            {
                return null;
            }

            Version version;
            System.Net.HttpStatusCode statusCode;

            ReadOnlySpan<byte> buffer = _readBuffer.ActiveSpan;

            if (buffer.Length >= 9 && buffer[8] == ' ')
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(buffer));

                if (x == HttpPrimitiveVersion.Version11._encoded)
                {
                    version = System.Net.HttpVersion.Version11;
                }
                else if (x == HttpPrimitiveVersion.Version10._encoded)
                {
                    version = System.Net.HttpVersion.Version10;
                }
                else
                {
                    goto unknownVersion;
                }

                buffer = buffer.Slice(9);
                goto parseStatusCode;
            }
            else
            {
                goto unknownVersion;
            }

        parseStatusCode:
            if (buffer.Length < 4)
            {
                return null;
            }

            uint s100, s10, s1;
            if ((s100 = buffer[0] - (uint)'0') > 9 || (s10 = buffer[1] - (uint)'0') > 9 || (s1 = buffer[2] - (uint)'0') > 9 || buffer[3] != (byte)' ')
            {
                throw new Exception("invalid status code.");
            }

            statusCode = (System.Net.HttpStatusCode)(s100 * 100u + s10 * 10u + s1);
            buffer = buffer.Slice(4);

            int idx = buffer.IndexOf((byte)'\n');
            if (idx == -1)
            {
                return null;
            }

            buffer = buffer.Slice(idx + 1);
            _readBuffer.Discard(_readBuffer.ActiveLength - buffer.Length);

            requestStream.SetCurrentResponseLine(version, statusCode);

            // reset values from previous requests.
            _responseContentBytesRemaining = 0;
            _responseChunkBytesRemaining = 0;
            _responseHasContentLength = false;
            _responseIsChunked = false;
            _readingFirstResponseChunk = true;

            _readFunc = s_ReadToHeaders;
            return HttpReadType.Response;

        unknownVersion:
            idx = buffer.IndexOf((byte)' ');
            if (idx == -1) return null;
            version = System.Net.HttpVersion.Unknown;
            buffer = buffer.Slice(idx + 1);
            goto parseStatusCode;
        }

        private async ValueTask<HttpReadType> SkipHeadersAsync(Http1Request requestStream, CancellationToken cancellationToken)
        {
            await ReadHeadersAsync(requestStream, NullHttpHeaderSink.Instance, state: null, cancellationToken).ConfigureAwait(false);
            return await _readFunc!(this, requestStream, cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask ReadHeadersAsync(Http1Request requestStream, IHttpHeadersSink headersSink, object? state, CancellationToken cancellationToken = default)
        {
            if (_readFunc != s_SkipHeaders && _readFunc != s_SkipTrailingHeaders)
            {
                return;
            }

            int bytesConsumed;
            while (!ReadHeadersImpl(_readBuffer.ActiveSpan, headersSink, state, out bytesConsumed))
            {
                _readBuffer.Discard(bytesConsumed);
                _readBuffer.EnsureAvailableSpace(1);
                
                int readBytes = await _stream.ReadAsync(_readBuffer.AvailableMemory, cancellationToken).ConfigureAwait(false);

                if (readBytes == 0)
                {
                    throw new Exception("Unexpected EOF.");
                }

                _readBuffer.Commit(readBytes);
            }
            _readBuffer.Discard(bytesConsumed);

            _readFunc =
                _readingFirstResponseChunk == false ? s_ReadToEndOfStream : // Last header of chunked encoding.
                (int)requestStream.StatusCode < 200 ? s_ReadResponse : // Informational status code. more responses coming.
                s_ReadToContent; // Move to content.
        }

        partial void ProcessKnownHeaders(ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue)
        {
            if (HeaderNamesEqual(headerName, s_EncodedContentLengthName))
            {
                if (!TryParseDecimalInteger(headerValue, out _responseContentBytesRemaining))
                {
                    throw new Exception("Response Content-Length header contains a malformed or too-large value.");
                }
                _responseHasContentLength = true;
            }
            else if (HeaderNamesEqual(headerName, s_EncodedTransferEncodingName))
            {
                if (headerValue.SequenceEqual(s_EncodedTransferEncodingChunkedValue))
                {
                    _responseIsChunked = true;
                }
            }
        }

        private static bool HeaderNamesEqual(ReadOnlySpan<byte> wireValue, ReadOnlySpan<byte> expectedValueLowerCase)
        {
            if (wireValue.Length != expectedValueLowerCase.Length) return false;

            ref byte xRef = ref MemoryMarshal.GetReference(wireValue);
            ref byte yRef = ref MemoryMarshal.GetReference(expectedValueLowerCase);

            for (uint i = 0; i < (uint)wireValue.Length; ++i)
            {
                byte xv = Unsafe.Add(ref xRef, (IntPtr)i);

                if ((xv - (uint)'A') <= ('Z' - 'A'))
                {
                    xv |= 0x20;
                }

                if (xv != Unsafe.Add(ref yRef, (IntPtr)i)) return false;
            }

            return true;
        }

        private ValueTask<HttpReadType> ReadToContentAsync(Http1Request requestStream)
        {
            HttpReadType readType;

            if (_responseIsChunked || !_responseHasContentLength || _responseContentBytesRemaining != 0)
            {
                readType = HttpReadType.Content;
                _readFunc = s_SkipContent;
            }
            else
            {
                readType = HttpReadType.EndOfStream;
            }

            requestStream.SetCurrentReadType(readType);
            return new ValueTask<HttpReadType>(readType);
        }

        private async ValueTask<HttpReadType> SkipContentAsync(Http1Request requestStream, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
            while ((await ReadContentAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0) ;
            ArrayPool<byte>.Shared.Return(buffer);

            return await _readFunc!(this, requestStream, cancellationToken).ConfigureAwait(false);
        }

        internal ValueTask<int> ReadContentAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_readFunc != s_SkipContent)
            {
                return new ValueTask<int>(0);
            }
            else if (!_responseIsChunked)
            {
                return ReadUnenvelopedContentAsync(buffer, cancellationToken);
            }
            else
            {
                return ReadChunkedContentAsync(buffer, cancellationToken);
            }
        }

        private async ValueTask<int> ReadChunkedContentAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_responseChunkBytesRemaining == 0)
            {
                while (!TryReadNextChunkSize(out _responseChunkBytesRemaining))
                {
                    _readBuffer.EnsureAvailableSpace(1);

                    int readBytes = await _stream.ReadAsync(_readBuffer.AvailableMemory, cancellationToken).ConfigureAwait(false);
                    if (readBytes == 0) throw new Exception("Unexpected EOF, expected chunk envelope.");

                    _readBuffer.Commit(readBytes);
                }

                if (_responseChunkBytesRemaining == 0)
                {
                    // Final chunk. Move to trailing headers.
                    _readFunc = s_ReadToTrailingHeaders;
                    return 0;
                }
            }

            int maxReadLength = (int)Math.Min((uint)buffer.Length, _responseChunkBytesRemaining);

            int takeLength;

            int bufferedLength = _readBuffer.ActiveLength;
            if (bufferedLength != 0)
            {
                takeLength = Math.Min(bufferedLength, maxReadLength);
                if (takeLength != 0)
                {
                    _readBuffer.ActiveSpan.Slice(0, takeLength).CopyTo(buffer.Span);
                    _readBuffer.Discard(takeLength);
                }
            }
            else
            {
                takeLength = await _stream.ReadAsync(buffer.Slice(0, maxReadLength), cancellationToken).ConfigureAwait(false);
                if (takeLength == 0 && buffer.Length != 0) throw new Exception("Unexpected EOF");
            }

            ulong takeLengthExtended = (uint)takeLength;
            _responseChunkBytesRemaining -= takeLengthExtended;

            if (_responseHasContentLength)
            {
                _responseContentBytesRemaining -= takeLengthExtended;
            }

            return takeLength;
        }

        private async ValueTask<int> ReadUnenvelopedContentAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            int maxReadLength = buffer.Length;

            if (_responseHasContentLength && _responseContentBytesRemaining <= (uint)maxReadLength)
            {
                if (_responseContentBytesRemaining == 0)
                {
                    // End of content reached, or empty buffer.
                    _readFunc = s_ReadToEndOfStream;
                    return 0;
                }

                maxReadLength = (int)_responseContentBytesRemaining;
            }

            Debug.Assert(maxReadLength > 0 || buffer.Length == 0);

            int takeLength;

            int bufferedLength = _readBuffer.ActiveLength;
            if (bufferedLength != 0)
            {
                takeLength = Math.Min(bufferedLength, maxReadLength);
                if (takeLength != 0)
                {
                    _readBuffer.ActiveSpan.Slice(0, takeLength).CopyTo(buffer.Span);
                    _readBuffer.Discard(takeLength);
                }
            }
            else
            {
                takeLength = await _stream.ReadAsync(buffer.Slice(0, maxReadLength), cancellationToken).ConfigureAwait(false);
                if (takeLength == 0 && buffer.Length != 0)
                {
                    if (_responseHasContentLength) throw new Exception("Unexpected EOF");
                    _readFunc = s_ReadToEndOfStream;
                }
            }

            if (_responseHasContentLength)
            {
                _responseContentBytesRemaining -= (uint)takeLength;
            }

            return takeLength;
        }

        private bool TryReadNextChunkSize(out ulong chunkSize)
        {
            Span<byte> buffer = _readBuffer.ActiveSpan;
            int previousChunkNewlineLength = 0;

            if (!_readingFirstResponseChunk)
            {
                if (buffer.Length == 0)
                {
                    goto needMore;
                }

                byte b = buffer[0];
                if (b == '\r')
                {
                    if (buffer.Length == 1)
                    {
                        goto needMore;
                    }

                    if (buffer[1] == '\n')
                    {
                        buffer = buffer[2..];
                        previousChunkNewlineLength = 2;
                    }
                    else
                    {
                        throw new Exception("Invalid chunked envelope; expected newline to end chunk.");
                    }
                }
                else if (b == '\n')
                {
                    buffer = buffer[1..];
                    previousChunkNewlineLength = 1;
                }
                else
                {
                    throw new Exception("Invalid chunked envelope; expected newline to end chunk.");
                }
            }

            int lfIdx = buffer.IndexOf((byte)'\n');
            int valueEndIdx = lfIdx;

            if (lfIdx == -1)
            {
                goto needMore;
            }

            if (lfIdx != 0 && buffer[lfIdx - 1] == '\r')
            {
                valueEndIdx = lfIdx - 1;
            }

            if (valueEndIdx != 0)
            {
                int chunkExtBeginIdx = buffer.Slice(0, valueEndIdx).IndexOf((byte)';');
                if (chunkExtBeginIdx != -1) valueEndIdx = chunkExtBeginIdx;
            }

            if (valueEndIdx == 0 || !TryParseHexInteger(buffer.Slice(0, valueEndIdx), out chunkSize))
            {
                throw new Exception($"Invalid chunked envelope; expected chunk size. idx: {valueEndIdx}.");
            }

            _readBuffer.Discard(previousChunkNewlineLength + lfIdx + 1);
            _readingFirstResponseChunk = false;

            return true;

        needMore:
            chunkSize = 0;
            return false;
        }

        private static bool TryParseHexInteger(ReadOnlySpan<byte> buffer, out ulong value)
        {
            if (buffer.Length == 0)
            {
                value = default;
                return false;
            }

            ulong contentLength = 0;
            foreach (byte b in buffer)
            {
                uint digit;

                if ((digit = b - (uint)'0') <= 9)
                {
                }
                else if ((digit = b - (uint)'a') <= 'f' - 'a')
                {
                    digit += 10;
                }
                else if ((digit = b - (uint)'A') <= 'F' - 'A')
                {
                    digit += 10;
                }
                else
                {
                    value = default;
                    return false;
                }

                try
                {
                    contentLength = checked(contentLength * 16u + digit);
                }
                catch (OverflowException)
                {
                    value = default;
                    return false;
                }
            }

            value = contentLength;
            return true;
        }

        private static bool TryParseDecimalInteger(ReadOnlySpan<byte> buffer, out ulong value)
        {
            if (buffer.Length == 0)
            {
                value = default;
                return false;
            }

            ulong contentLength = 0;
            foreach (byte b in buffer)
            {
                uint digit = b - (uint)'0';
                if (digit > 9)
                {
                    value = default;
                    return false;
                }

                try
                {
                    contentLength = checked(contentLength * 10u + digit);
                }
                catch (OverflowException)
                {
                    value = default;
                    return false;
                }
            }

            value = contentLength;
            return true;
        }
    }
}
