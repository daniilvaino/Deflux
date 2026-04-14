// Originally from SharpZipLib (MIT License)
// https://github.com/icsharpcode/SharpZipLib
// Namespace changed for Deflux.

using System;

namespace Deflux.Core.Decompression;

/// <summary>
/// 32KB sliding window for DEFLATE decompression output.
/// </summary>
internal class OutputWindow
{
    internal const int WindowSize = 1 << 15;
    private const int WindowMask = WindowSize - 1;

    private byte[] window = new byte[WindowSize];
    private int windowEnd;
    private int windowFilled;

    public void Write(int value)
    {
        if (windowFilled++ == WindowSize)
            throw new InvalidOperationException("Window full");
        window[windowEnd++] = (byte)value;
        windowEnd &= WindowMask;
    }

    private void SlowRepeat(int repStart, int length, int distance)
    {
        while (length-- > 0)
        {
            window[windowEnd++] = window[repStart++];
            windowEnd &= WindowMask;
            repStart &= WindowMask;
        }
    }

    public void Repeat(int length, int distance)
    {
        if ((windowFilled += length) > WindowSize)
            throw new InvalidOperationException("Window full");

        int repStart = (windowEnd - distance) & WindowMask;
        int border = WindowSize - length;
        if ((repStart <= border) && (windowEnd < border))
        {
            if (length <= distance)
            {
                Array.Copy(window, repStart, window, windowEnd, length);
                windowEnd += length;
            }
            else
            {
                while (length-- > 0)
                    window[windowEnd++] = window[repStart++];
            }
        }
        else
        {
            SlowRepeat(repStart, length, distance);
        }
    }

    public int CopyStored(StreamManipulator input, int length)
    {
        length = Math.Min(Math.Min(length, WindowSize - windowFilled), input.AvailableBytes);
        int copied;

        int tailLen = WindowSize - windowEnd;
        if (length > tailLen)
        {
            copied = input.CopyBytes(window, windowEnd, tailLen);
            if (copied == tailLen)
                copied += input.CopyBytes(window, 0, length - tailLen);
        }
        else
        {
            copied = input.CopyBytes(window, windowEnd, length);
        }

        windowEnd = (windowEnd + copied) & WindowMask;
        windowFilled += copied;
        return copied;
    }

    public void CopyDict(byte[] dictionary, int offset, int length)
    {
        if (dictionary == null)
            throw new ArgumentNullException(nameof(dictionary));
        if (windowFilled > 0)
            throw new InvalidOperationException();

        if (length > WindowSize)
        {
            offset += length - WindowSize;
            length = WindowSize;
        }
        Array.Copy(dictionary, offset, window, 0, length);
        windowEnd = length & WindowMask;
    }

    public int GetFreeSpace() => WindowSize - windowFilled;

    public int GetAvailable() => windowFilled;

    public int CopyOutput(byte[] output, int offset, int len)
    {
        int copyEnd = windowEnd;
        if (len > windowFilled)
            len = windowFilled;
        else
            copyEnd = (windowEnd - windowFilled + len) & WindowMask;

        int copied = len;
        int tailLen = len - copyEnd;

        if (tailLen > 0)
        {
            Array.Copy(window, WindowSize - tailLen, output, offset, tailLen);
            offset += tailLen;
            len = copyEnd;
        }
        Array.Copy(window, copyEnd - len, output, offset, len);
        windowFilled -= copied;
        if (windowFilled < 0)
            throw new InvalidOperationException();
        return copied;
    }

    public void Reset()
    {
        windowFilled = windowEnd = 0;
    }

    // ── Checkpoint support ──

    internal byte[] WindowData => window;
    internal int WindowEnd => windowEnd;
    internal int WindowFilled => windowFilled;

    internal void RestoreState(byte[] windowData, int windowEnd, int windowFilled)
    {
        Array.Copy(windowData, 0, window, 0, WindowSize);
        this.windowEnd = windowEnd;
        this.windowFilled = windowFilled;
    }
}
