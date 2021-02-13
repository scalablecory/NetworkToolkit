using NetworkToolkit.Parsing;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NetworkToolkit.Http.Primitives
{
    internal static class Http2Frame
    {
        public const byte DataFrame = 0;
        public const byte HeadersFrame = 1;
        public const byte PriorityFrame = 2;
        public const byte ResetStreamFrame = 3;
        public const byte SettingsFrame = 4;
        public const byte PushPromiseFrame = 5;
        public const byte PingFrame = 6;
        public const byte GoAwayFrame = 7;
        public const byte WindowUpdateFrame = 8;
        public const byte ContinuationFrame = 9;

        public const int FrameHeaderLength = 9;

        public const int DataFrameHeaderLength = 10;
        public const int HeadersFrameHeaderLength = 16;
        public const int RstStreamFrameLength = 13;
        public const int InitialSettingsFrameLength = 33;
        public const int PingFrameLength = 17;
        public const int GoAwayFrameHeaderLength = 17;
        public const int WindowUpdateFrameLength = 13;

        public const int SettingLength = 6;

        public static void EncodeDataFrameHeader(uint payloadLength, Http2DataFrameFlags flags, uint streamId, Span<byte> buffer)
        {
            Debug.Assert(payloadLength <= 0xFFFFFF);
            Debug.Assert(!flags.HasFlag(Http2DataFrameFlags.Padded));
            Debug.Assert(streamId < 0x80000000);
            Debug.Assert(buffer.Length >= DataFrameHeaderLength);

            buffer[0] = (byte)(payloadLength >> 16);
            buffer[1] = (byte)(payloadLength >> 8);
            buffer[2] = (byte)payloadLength;
            buffer[3] = 0x0; // DATA frame.
            buffer[4] = (byte)flags;
            BinaryPrimitives.WriteUInt32BigEndian(buffer[5..], streamId);
            buffer[9] = 0; // pad length.
        }

        public static void EncodeHeadersFrameHeader(uint payloadLength, Http2HeadersFrameFlags flags, uint streamId, Span<byte> buffer)
        {
            Debug.Assert(payloadLength <= 0xFFFFFF);
            Debug.Assert(!flags.HasFlag(Http2HeadersFrameFlags.Padded));
            Debug.Assert(streamId < 0x80000000);
            Debug.Assert(buffer.Length >= HeadersFrameHeaderLength);

            buffer[0] = (byte)(payloadLength >> 16);
            buffer[1] = (byte)(payloadLength >> 8);
            buffer[2] = (byte)payloadLength;
            buffer[3] = 0x1; // HEADERS frame.
            buffer[4] = (byte)flags;
            buffer[5] = 0; // pad length.
            BinaryPrimitives.WriteUInt32BigEndian(buffer[6..], streamId);
            BitConverter.TryWriteBytes(buffer[10..], (uint)0); // pad length, stream dependency ABC
            BitConverter.TryWriteBytes(buffer[14..], (ushort)0); // stream dependency D, Weight
        }

        public static void EncodeRstStreamFrame(uint streamId, uint errorCode, Span<byte> buffer)
        {
            Debug.Assert(streamId < 0x80000000);
            Debug.Assert(buffer.Length >= RstStreamFrameLength);

            buffer[0] = 0; // payloadLength A
            buffer[1] = 0; // payloadLength B
            buffer[2] = 0; // payloadLength C
            buffer[3] = 0x3; // RST_STREAM frame.
            buffer[4] = 0; // flags
            BinaryPrimitives.WriteUInt32BigEndian(buffer[5..], streamId);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[9..], errorCode);
            BitConverter.TryWriteBytes(buffer[14..], (ushort)0); // stream dependency D, Weight
        }

        public static void EncodeInitialSettingsFrame(uint headerTableSize, uint maxFrameSize, uint maxHeaderListSize, Span<byte> buffer)
        {
            Debug.Assert(maxFrameSize < (1 << 24));
            Debug.Assert(buffer.Length >= InitialSettingsFrameLength);

            BinaryPrimitives.WriteUInt32BigEndian(buffer, 0x00001804); // payloadLength ABC, SETTINGS frame
            BitConverter.TryWriteBytes(buffer[4..], (ushort)0); // flags, streamId ABC
            buffer[8] = 0; // streamId D
            BinaryPrimitives.TryWriteUInt16BigEndian(buffer[9..], 0x1); // SETTINGS_HEADER_TABLE_SIZE
            BinaryPrimitives.TryWriteUInt32BigEndian(buffer[11..], headerTableSize);
            BinaryPrimitives.TryWriteUInt32BigEndian(buffer[15..], 0x00020000); // SETTINGS_ENABLE_PUSH, 0 AB
            BinaryPrimitives.TryWriteUInt32BigEndian(buffer[19..], 0x00000005); // 0 CD, SETTINGS_FRAME_MAX_SIZE
            BinaryPrimitives.TryWriteUInt32BigEndian(buffer[23..], maxFrameSize);
            BinaryPrimitives.TryWriteUInt16BigEndian(buffer[27..], 0x6); // SETTINGS_MAX_HEADER_LIST_SIZE
            BinaryPrimitives.TryWriteUInt32BigEndian(buffer[29..], maxHeaderListSize);
        }

        public static void EncodeSettingsAckFrame(Span<byte> buffer)
        {
            //Debug.Assert(buffer.Length >= SettingsAckFrameLength);

            BinaryPrimitives.WriteUInt32BigEndian(buffer, 0x00000004); // payloadLength ABC, SETTINGS frame
            BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], 0x01000000); // flags, streamId ABC
            buffer[8] = 0; // streamId D
        }

        public static void EncodePingAckFrame(ulong pingData, Span<byte> buffer)
        {
            Debug.Assert(buffer.Length >= PingFrameLength);

            BinaryPrimitives.WriteUInt32BigEndian(buffer, 0x00000806); // payloadLength ABC, PING frame
            BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], 0x01000000); // flags, streamId ABC
            buffer[8] = 0; // streamId D
            BitConverter.TryWriteBytes(buffer[9..], pingData);
        }

        public static void EncodeWindowUpdateFrame(uint windowSizeIncrement, uint streamId, Span<byte> buffer)
        {
            Debug.Assert(windowSizeIncrement > 0);
            Debug.Assert(windowSizeIncrement < 0x80000000);
            Debug.Assert(streamId < 0x80000000);
            Debug.Assert(buffer.Length >= WindowUpdateFrameLength);

            BinaryPrimitives.WriteUInt32BigEndian(buffer, 0x00000408); // payloadLength ABC, WINDOW_UPDATE frame
            buffer[4] = 0x00;
            BinaryPrimitives.WriteUInt32BigEndian(buffer[5..], streamId);
            BinaryPrimitives.WriteUInt32BigEndian(buffer[9..], windowSizeIncrement);
        }
    }

    internal enum Http2DataFrameFlags : byte
    {
        EndStream = 0x1,
        Padded = 0x8
    }

    internal enum Http2HeadersFrameFlags : byte
    {
        EndStream = 0x1,
        EndHeaders = 0x4,
        Padded = 0x8,
        Priority = 0x20
    }

    internal enum Http2ContinuationFrameFlags : byte
    {
        EndHeaders = 0x4
    }

    internal enum Http2SettingsFrameFlags : byte
    {
        Ack = 0x1
    }

    internal enum Http2PingFrameFlags : byte
    {
        Ack = 0x1
    }
}
