using System;
using System.Numerics;

namespace NetworkToolkit.Http.Primitives
{
    internal static class HPackDecoder
    {
        const int RfcEntrySizeAdjust = 32;

        public static int Decode(ReadOnlySpan<byte> buffer, IHttpHeadersSink sink, object? state)
        {
            int originalLength = buffer.Length;

            while (buffer.Length != 0)
            {
                HttpHeaderFlags flags;
                ulong nameIndex;
                int headerLength;
                byte prefixMask;

                byte firstByte = buffer[0];

                switch (BitOperations.LeadingZeroCount(firstByte) - 24)
                {
                    case 0:
                        if (HPack.TryDecodeIndexedHeader(firstByte, buffer, out nameIndex, out headerLength))
                        {
                            buffer = buffer.Slice(headerLength);
                            OnHeader(sink, state, nameIndex);
                            continue;
                        }
                        else
                        {
                            return originalLength - buffer.Length;
                        }
                    case 1:
                        prefixMask = HPack.IncrementalIndexingMask;
                        flags = HttpHeaderFlags.None;
                        break;
                    case 2: // Dynamic table size update.
                        if (HPack.TryDecodeDynamicTableSizeUpdate(firstByte, buffer, out nameIndex, out headerLength))
                        {
                            buffer = buffer.Slice(headerLength);
                            OnDynamicTableSizeUpdate(nameIndex);
                            continue;
                        }
                        else
                        {
                            return originalLength - buffer.Length;
                        }
                    case 3: // Literal header never indexed.
                        prefixMask = HPack.WithoutIndexingOrNeverIndexMask;
                        flags = HttpHeaderFlags.NeverCompressed;
                        break;
                    default: // Literal header without indexing.
                        prefixMask = HPack.WithoutIndexingOrNeverIndexMask;
                        flags = HttpHeaderFlags.None;
                        break;
                }

                if (!HPack.TryDecodeHeader(prefixMask, flags, firstByte, buffer, out nameIndex, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value, out flags, out headerLength))
                {
                    return originalLength - buffer.Length;
                }

                buffer = buffer.Slice(headerLength);

                if (nameIndex != 0)
                {
                    OnHeader(sink, state, nameIndex, value, flags);
                }
                else
                {
                    OnHeader(sink, state, name, value, flags);
                }
            }

            return originalLength - buffer.Length;
        }

        private static void OnDynamicTableSizeUpdate(ulong newSize)
        {
            throw new Exception("Dynamic table update not supported.");
        }

        private static void OnHeader(IHttpHeadersSink sink, object? state, ulong headerIndex)
        {
            PreparedHeader v = GetHeaderForIndex(headerIndex);
            OnHeader(sink, state, v._name._http2Encoded, v._value, HttpHeaderFlags.None);
        }

        private static void OnHeader(IHttpHeadersSink sink, object? state, ulong headerNameIndex, ReadOnlySpan<byte> headerValue, HttpHeaderFlags flags)
        {
            PreparedHeader v = GetHeaderForIndex(headerNameIndex);
            OnHeader(sink, state, v._name._http2Encoded, headerValue, flags);
        }

        private static void OnHeader(IHttpHeadersSink sink, object? state, ReadOnlySpan<byte> headerName, ReadOnlySpan<byte> headerValue, HttpHeaderFlags flags)
        {
            if (headerName.Length > 1 && headerName[0] == ':')
            {
                // TODO: check for pseudo headers.
            }

            sink.OnHeader(state, headerName, headerValue, flags);
        }

        private static PreparedHeader GetHeaderForIndex(ulong headerIndex)
        {
            if (headerIndex is > 0 and <= HPack.StaticTableMaxIndex)
            {
                return HPack.GetStaticHeader((uint)headerIndex);
            }

            throw new Exception($"Invalid header index {headerIndex}");
        }
    }
}
