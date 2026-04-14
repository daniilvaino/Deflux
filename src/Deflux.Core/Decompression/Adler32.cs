// Originally from SharpZipLib (MIT License)
// https://github.com/icsharpcode/SharpZipLib
// Namespace changed, IChecksum dependency removed for Deflux.

using System;

namespace Deflux.Core.Decompression;

/// <summary>
/// Computes Adler32 checksum per RFC 1950.
/// </summary>
internal sealed class Adler32
{
    private const uint BASE = 65521;

    private uint checkValue;

    public Adler32()
    {
        Reset();
    }

    public void Reset()
    {
        checkValue = 1;
    }

    public long Value => checkValue;

    public void Update(int bval)
    {
        uint s1 = checkValue & 0xFFFF;
        uint s2 = checkValue >> 16;

        s1 = (s1 + ((uint)bval & 0xFF)) % BASE;
        s2 = (s1 + s2) % BASE;

        checkValue = (s2 << 16) + s1;
    }

    public void Update(ArraySegment<byte> segment)
    {
        uint s1 = checkValue & 0xFFFF;
        uint s2 = checkValue >> 16;
        var count = segment.Count;
        var offset = segment.Offset;
        while (count > 0)
        {
            int n = 3800;
            if (n > count)
                n = count;
            count -= n;
            while (--n >= 0)
            {
                s1 = s1 + (uint)(segment.Array![offset++] & 0xff);
                s2 = s2 + s1;
            }
            s1 %= BASE;
            s2 %= BASE;
        }
        checkValue = (s2 << 16) | s1;
    }

    // ── Checkpoint support ──

    internal uint CheckValue => checkValue;

    internal void RestoreState(uint value)
    {
        checkValue = value;
    }
}
