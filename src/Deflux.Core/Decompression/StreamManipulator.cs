// Originally from SharpZipLib (MIT License)
// https://github.com/icsharpcode/SharpZipLib
// Namespace changed, external dependencies removed for Deflux.

using System;

namespace Deflux.Core.Decompression;

/// <summary>
/// Bit-stream reader optimized for DEFLATE decompression.
/// Extracts specified bit sequences from an input buffer.
/// </summary>
internal class StreamManipulator
{
    private byte[] window_ = Array.Empty<byte>();
    private int windowStart_;
    private int windowEnd_;
    private uint buffer_;
    private int bitsInBuffer_;

    public int PeekBits(int bitCount)
    {
        if (bitsInBuffer_ < bitCount)
        {
            if (windowStart_ == windowEnd_)
                return -1;

            buffer_ |= (uint)((window_[windowStart_++] & 0xff |
                             (window_[windowStart_++] & 0xff) << 8) << bitsInBuffer_);
            bitsInBuffer_ += 16;
        }
        return (int)(buffer_ & ((1 << bitCount) - 1));
    }

    public bool TryGetBits(int bitCount, ref int output, int outputOffset = 0)
    {
        var bits = PeekBits(bitCount);
        if (bits < 0)
            return false;
        output = bits + outputOffset;
        DropBits(bitCount);
        return true;
    }

    public bool TryGetBits(int bitCount, ref byte[] array, int index)
    {
        var bits = PeekBits(bitCount);
        if (bits < 0)
            return false;
        array[index] = (byte)bits;
        DropBits(bitCount);
        return true;
    }

    public void DropBits(int bitCount)
    {
        buffer_ >>= bitCount;
        bitsInBuffer_ -= bitCount;
    }

    public int GetBits(int bitCount)
    {
        int bits = PeekBits(bitCount);
        if (bits >= 0)
            DropBits(bitCount);
        return bits;
    }

    public int AvailableBits => bitsInBuffer_;

    public int AvailableBytes => windowEnd_ - windowStart_ + (bitsInBuffer_ >> 3);

    public void SkipToByteBoundary()
    {
        buffer_ >>= (bitsInBuffer_ & 7);
        bitsInBuffer_ &= ~7;
    }

    public bool IsNeedingInput => windowStart_ == windowEnd_;

    public int CopyBytes(byte[] output, int offset, int length)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        if ((bitsInBuffer_ & 7) != 0)
            throw new InvalidOperationException("Bit buffer is not byte aligned!");

        int count = 0;
        while ((bitsInBuffer_ > 0) && (length > 0))
        {
            output[offset++] = (byte)buffer_;
            buffer_ >>= 8;
            bitsInBuffer_ -= 8;
            length--;
            count++;
        }

        if (length == 0)
            return count;

        int avail = windowEnd_ - windowStart_;
        if (length > avail)
            length = avail;
        Array.Copy(window_, windowStart_, output, offset, length);
        windowStart_ += length;

        if (((windowStart_ - windowEnd_) & 1) != 0)
        {
            buffer_ = (uint)(window_[windowStart_++] & 0xff);
            bitsInBuffer_ = 8;
        }
        return count + length;
    }

    public void Reset()
    {
        buffer_ = 0;
        windowStart_ = windowEnd_ = bitsInBuffer_ = 0;
    }

    public void SetInput(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (windowStart_ < windowEnd_)
            throw new InvalidOperationException("Old input was not completely processed");

        int end = offset + count;
        if ((offset > end) || (end > buffer.Length))
            throw new ArgumentOutOfRangeException(nameof(count));

        if ((count & 1) != 0)
        {
            buffer_ |= (uint)((buffer[offset++] & 0xff) << bitsInBuffer_);
            bitsInBuffer_ += 8;
        }

        window_ = buffer;
        windowStart_ = offset;
        windowEnd_ = end;
    }

    // ── Checkpoint support ──

    internal byte[] Window => window_;
    internal int WindowStart => windowStart_;
    internal int WindowEnd => windowEnd_;
    internal uint Buffer => buffer_;
    internal int BitsInBuffer => bitsInBuffer_;

    internal void RestoreState(byte[] window, int windowStart, int windowEnd, uint buffer, int bitsInBuffer)
    {
        window_ = window;
        windowStart_ = windowStart;
        windowEnd_ = windowEnd;
        buffer_ = buffer;
        bitsInBuffer_ = bitsInBuffer;
    }
}
