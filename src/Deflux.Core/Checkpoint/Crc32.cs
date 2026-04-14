using System;

namespace Deflux.Core.Checkpoint;

/// <summary>
/// CRC-32 implementation for checkpoint integrity verification.
/// Uses the standard polynomial 0xEDB88320 (ISO 3309 / ITU-T V.42).
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;
        int end = offset + length;
        for (int i = offset; i < end; i++)
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        return crc ^ 0xFFFFFFFF;
    }

    public static uint Compute(byte[] data) => Compute(data, 0, data.Length);
}
