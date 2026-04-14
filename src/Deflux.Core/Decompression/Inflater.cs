// Originally from SharpZipLib (MIT License)
// https://github.com/icsharpcode/SharpZipLib
// Adapted for Deflux: noHeader always true (raw DEFLATE for ZIP entries),
// external dependencies removed, checkpoint accessors added.

using System;
using Deflux.Core.Exceptions;

namespace Deflux.Core.Decompression;

/// <summary>
/// DEFLATE decompressor (RFC 1951). Always operates in raw mode (no zlib header)
/// since ZIP entries use raw DEFLATE streams.
/// </summary>
internal class Inflater
{
    // Copy lengths for literal codes 257..285
    private static readonly int[] CPLENS = {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
    };

    // Extra bits for literal codes 257..285
    private static readonly int[] CPLEXT = {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
    };

    // Copy offsets for distance codes 0..29
    private static readonly int[] CPDIST = {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
        8193, 12289, 16385, 24577
    };

    // Extra bits for distance codes
    private static readonly int[] CPDEXT = {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
    };

    // State constants
    internal const int DECODE_BLOCKS = 2;
    internal const int DECODE_STORED_LEN1 = 3;
    internal const int DECODE_STORED_LEN2 = 4;
    internal const int DECODE_STORED = 5;
    internal const int DECODE_DYN_HEADER = 6;
    internal const int DECODE_HUFFMAN = 7;
    internal const int DECODE_HUFFMAN_LENBITS = 8;
    internal const int DECODE_HUFFMAN_DIST = 9;
    internal const int DECODE_HUFFMAN_DISTBITS = 10;
    internal const int FINISHED = 12;

    // Block type constants
    private const int STORED_BLOCK = 0;
    private const int STATIC_TREES = 1;
    private const int DYN_TREES = 2;

    // Instance fields
    internal int mode;
    internal int neededBits;
    internal int repLength;
    internal int repDist;
    internal int uncomprLen;
    internal bool isLastBlock;
    internal long totalOut;
    internal long totalIn;

    internal readonly StreamManipulator input;
    internal readonly OutputWindow outputWindow;
    internal InflaterDynHeader? dynHeader;
    internal InflaterHuffmanTree? litlenTree;
    internal InflaterHuffmanTree? distTree;
    internal readonly Adler32 adler;

    public Inflater()
    {
        input = new StreamManipulator();
        outputWindow = new OutputWindow();
        adler = new Adler32();
        mode = DECODE_BLOCKS;
    }

    public void Reset()
    {
        mode = DECODE_BLOCKS;
        totalIn = 0;
        totalOut = 0;
        input.Reset();
        outputWindow.Reset();
        dynHeader = null;
        litlenTree = null;
        distTree = null;
        isLastBlock = false;
        adler.Reset();
    }

    private bool DecodeHuffman()
    {
        int free = outputWindow.GetFreeSpace();
        while (free >= 258)
        {
            int symbol;
            switch (mode)
            {
                case DECODE_HUFFMAN:
                    while (((symbol = litlenTree!.GetSymbol(input)) & ~0xff) == 0)
                    {
                        outputWindow.Write(symbol);
                        if (--free < 258)
                            return true;
                    }

                    if (symbol < 257)
                    {
                        if (symbol < 0)
                            return false;
                        // symbol == 256: end of block
                        distTree = null;
                        litlenTree = null;
                        mode = DECODE_BLOCKS;
                        return true;
                    }

                    try
                    {
                        repLength = CPLENS[symbol - 257];
                        neededBits = CPLEXT[symbol - 257];
                    }
                    catch (Exception)
                    {
                        throw new DecompressionException("Illegal rep length code");
                    }
                    goto case DECODE_HUFFMAN_LENBITS;

                case DECODE_HUFFMAN_LENBITS:
                    if (neededBits > 0)
                    {
                        mode = DECODE_HUFFMAN_LENBITS;
                        int i = input.PeekBits(neededBits);
                        if (i < 0)
                            return false;
                        input.DropBits(neededBits);
                        repLength += i;
                    }
                    mode = DECODE_HUFFMAN_DIST;
                    goto case DECODE_HUFFMAN_DIST;

                case DECODE_HUFFMAN_DIST:
                    symbol = distTree!.GetSymbol(input);
                    if (symbol < 0)
                        return false;

                    try
                    {
                        repDist = CPDIST[symbol];
                        neededBits = CPDEXT[symbol];
                    }
                    catch (Exception)
                    {
                        throw new DecompressionException("Illegal rep dist code");
                    }
                    goto case DECODE_HUFFMAN_DISTBITS;

                case DECODE_HUFFMAN_DISTBITS:
                    if (neededBits > 0)
                    {
                        mode = DECODE_HUFFMAN_DISTBITS;
                        int i = input.PeekBits(neededBits);
                        if (i < 0)
                            return false;
                        input.DropBits(neededBits);
                        repDist += i;
                    }
                    outputWindow.Repeat(repLength, repDist);
                    free -= repLength;
                    mode = DECODE_HUFFMAN;
                    break;

                default:
                    throw new DecompressionException("Inflater unknown mode");
            }
        }
        return true;
    }

    private bool Decode()
    {
        switch (mode)
        {
            case DECODE_BLOCKS:
                if (isLastBlock)
                {
                    mode = FINISHED;
                    return false;
                }

                int type = input.PeekBits(3);
                if (type < 0)
                    return false;
                input.DropBits(3);

                isLastBlock |= (type & 1) != 0;
                switch (type >> 1)
                {
                    case STORED_BLOCK:
                        input.SkipToByteBoundary();
                        mode = DECODE_STORED_LEN1;
                        break;
                    case STATIC_TREES:
                        litlenTree = InflaterHuffmanTree.DefLitLenTree;
                        distTree = InflaterHuffmanTree.DefDistTree;
                        mode = DECODE_HUFFMAN;
                        break;
                    case DYN_TREES:
                        dynHeader = new InflaterDynHeader(input);
                        mode = DECODE_DYN_HEADER;
                        break;
                    default:
                        throw new DecompressionException("Unknown block type " + type);
                }
                return true;

            case DECODE_STORED_LEN1:
            {
                if ((uncomprLen = input.PeekBits(16)) < 0)
                    return false;
                input.DropBits(16);
                mode = DECODE_STORED_LEN2;
            }
            goto case DECODE_STORED_LEN2;

            case DECODE_STORED_LEN2:
            {
                int nlen = input.PeekBits(16);
                if (nlen < 0)
                    return false;
                input.DropBits(16);
                if (nlen != (uncomprLen ^ 0xffff))
                    throw new DecompressionException("Broken uncompressed block");
                mode = DECODE_STORED;
            }
            goto case DECODE_STORED;

            case DECODE_STORED:
            {
                int more = outputWindow.CopyStored(input, uncomprLen);
                uncomprLen -= more;
                if (uncomprLen == 0)
                {
                    mode = DECODE_BLOCKS;
                    return true;
                }
                return !input.IsNeedingInput;
            }

            case DECODE_DYN_HEADER:
                if (!dynHeader!.AttemptRead())
                    return false;
                litlenTree = dynHeader.LiteralLengthTree;
                distTree = dynHeader.DistanceTree;
                mode = DECODE_HUFFMAN;
                goto case DECODE_HUFFMAN;

            case DECODE_HUFFMAN:
            case DECODE_HUFFMAN_LENBITS:
            case DECODE_HUFFMAN_DIST:
            case DECODE_HUFFMAN_DISTBITS:
                return DecodeHuffman();

            case FINISHED:
                return false;

            default:
                throw new DecompressionException("Inflater.Decode unknown mode");
        }
    }

    public void SetInput(byte[] buffer, int index, int count)
    {
        input.SetInput(buffer, index, count);
        totalIn += count;
    }

    public int Inflate(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset + count > buffer.Length)
            throw new ArgumentException("count exceeds buffer bounds");

        if (count == 0)
        {
            if (!IsFinished)
                Decode();
            return 0;
        }

        int bytesCopied = 0;
        do
        {
            int more = outputWindow.CopyOutput(buffer, offset, count);
            if (more > 0)
            {
                offset += more;
                bytesCopied += more;
                totalOut += more;
                count -= more;
                if (count == 0)
                    return bytesCopied;
            }
        } while (Decode() || (outputWindow.GetAvailable() > 0));
        return bytesCopied;
    }

    public bool IsNeedingInput => input.IsNeedingInput;

    public bool IsFinished => mode == FINISHED && outputWindow.GetAvailable() == 0;

    public long TotalOut => totalOut;

    public long TotalIn => totalIn - input.AvailableBytes;

    public int RemainingInput => input.AvailableBytes;
}
