using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkToolkit
{
    internal static class Tools
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int UnsafeByteOffset(ReadOnlySpan<byte> start, ReadOnlySpan<byte> end) =>
            (int)(void*)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(start), ref MemoryMarshal.GetReference(end));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int UnsafeByteOffset(ReadOnlySpan<byte> start, ref byte end) =>
            (int)(void*)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(start), ref end);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe int UnsafeByteOffset(ref byte start, ReadOnlySpan<byte> end) =>
            (int)(void*)Unsafe.ByteOffset(ref start, ref MemoryMarshal.GetReference(end));

        public static bool TimeoutExpired(long curTicks, long fromTicks, TimeSpan timeoutLimit)
        {
            return timeoutLimit != Timeout.InfiniteTimeSpan && new TimeSpan((curTicks - fromTicks) * TimeSpan.TicksPerMillisecond) > timeoutLimit;
        }

        public static string EscapeIdnHost(string hostName) =>
            new UriBuilder() { Scheme = Uri.UriSchemeHttp, Host = hostName, Port = 80 }.Uri.IdnHost;

        public static void BlockForResult(ValueTask task)
        {
            if (task.IsCompleted)
            {
                task.GetAwaiter().GetResult();
            }
            else
            {
                task.AsTask().GetAwaiter().GetResult();
            }
        }

        public static T BlockForResult<T>(ValueTask<T> task)
        {
            return task.IsCompleted ? task.GetAwaiter().GetResult() : task.AsTask().GetAwaiter().GetResult();
        }
    }
}
