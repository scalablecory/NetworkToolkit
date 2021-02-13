using System;

namespace NetworkToolkit.Http.Primitives
{
    class HPackDynamicTable
    {
        public PreparedHeader Get(ulong headerIndex)
        {
            throw new NotImplementedException();
        }

        public PreparedHeader Push(ulong nameIndex, ReadOnlySpan<byte> value)
        {
            throw new NotImplementedException();
        }

        public PreparedHeader Push(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        {
            throw new NotImplementedException();
        }
    }
}
