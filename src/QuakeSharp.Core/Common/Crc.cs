namespace QuakeSharp.Core.Common;

/// <summary>
/// crc.c: a 16-bit, non-reflected CRC with the polynomial 0x1021 (the CCITT standard CRC used by XMODEM).
/// </summary>
public static partial class Crc
{
    private const ushort CRC_INIT_VALUE = 0xffff;
    private const ushort CRC_XOR_VALUE = 0x0000;

    // crc.c: CRC_Init
    public static void Init(out ushort crcvalue) => crcvalue = CRC_INIT_VALUE;

    // crc.c: CRC_ProcessByte
    public static void ProcessByte(ref ushort crcvalue, byte data) =>
        crcvalue = (ushort)((crcvalue << 8) ^ crctable[(crcvalue >> 8) ^ data]);

    // crc.c: CRC_Value
    public static ushort Value(ushort crcvalue) => (ushort)(crcvalue ^ CRC_XOR_VALUE);
}
