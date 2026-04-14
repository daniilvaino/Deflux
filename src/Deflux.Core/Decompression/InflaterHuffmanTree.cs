// Originally from SharpZipLib (MIT License)
// https://github.com/icsharpcode/SharpZipLib
// Namespace changed, DeflaterHuffman.BitReverse inlined for Deflux.

using System;
using System.Collections.Generic;
using Deflux.Core.Exceptions;

namespace Deflux.Core.Decompression;

/// <summary>
/// Huffman tree used for DEFLATE inflation.
/// </summary>
internal class InflaterHuffmanTree
{
    private const int MAX_BITLEN = 15;

    private static readonly byte[] Bit4Reverse = {
        0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15
    };

    internal short[] Tree { get; set; }

    public static readonly InflaterHuffmanTree DefLitLenTree;
    public static readonly InflaterHuffmanTree DefDistTree;

    static InflaterHuffmanTree()
    {
        byte[] codeLengths = new byte[288];
        int i = 0;
        while (i < 144) codeLengths[i++] = 8;
        while (i < 256) codeLengths[i++] = 9;
        while (i < 280) codeLengths[i++] = 7;
        while (i < 288) codeLengths[i++] = 8;
        DefLitLenTree = new InflaterHuffmanTree(codeLengths);

        codeLengths = new byte[32];
        i = 0;
        while (i < 32) codeLengths[i++] = 5;
        DefDistTree = new InflaterHuffmanTree(codeLengths);
    }

    public InflaterHuffmanTree(IList<byte> codeLengths)
    {
        Tree = null!;
        BuildTree(codeLengths);
    }

    internal static short BitReverse(int toReverse)
    {
        return (short)(Bit4Reverse[toReverse & 0xF] << 12 |
                       Bit4Reverse[(toReverse >> 4) & 0xF] << 8 |
                       Bit4Reverse[(toReverse >> 8) & 0xF] << 4 |
                       Bit4Reverse[toReverse >> 12]);
    }

    private void BuildTree(IList<byte> codeLengths)
    {
        int[] blCount = new int[MAX_BITLEN + 1];
        int[] nextCode = new int[MAX_BITLEN + 1];

        for (int i = 0; i < codeLengths.Count; i++)
        {
            int bits = codeLengths[i];
            if (bits > 0)
                blCount[bits]++;
        }

        int code = 0;
        int treeSize = 512;
        for (int bits = 1; bits <= MAX_BITLEN; bits++)
        {
            nextCode[bits] = code;
            code += blCount[bits] << (16 - bits);
            if (bits >= 10)
            {
                int start = nextCode[bits] & 0x1ff80;
                int end = code & 0x1ff80;
                treeSize += (end - start) >> (16 - bits);
            }
        }

        Tree = new short[treeSize];
        int treePtr = 512;
        for (int bits = MAX_BITLEN; bits >= 10; bits--)
        {
            int end = code & 0x1ff80;
            code -= blCount[bits] << (16 - bits);
            int start = code & 0x1ff80;
            for (int i = start; i < end; i += 1 << 7)
            {
                Tree[BitReverse(i)] = (short)((-treePtr << 4) | bits);
                treePtr += 1 << (bits - 9);
            }
        }

        for (int i = 0; i < codeLengths.Count; i++)
        {
            int bits = codeLengths[i];
            if (bits == 0)
                continue;
            code = nextCode[bits];
            int revcode = BitReverse(code);
            if (bits <= 9)
            {
                do
                {
                    Tree[revcode] = (short)((i << 4) | bits);
                    revcode += 1 << bits;
                } while (revcode < 512);
            }
            else
            {
                int subTree = Tree[revcode & 511];
                int treeLen = 1 << (subTree & 15);
                subTree = -(subTree >> 4);
                do
                {
                    Tree[subTree | (revcode >> 9)] = (short)((i << 4) | bits);
                    revcode += 1 << bits;
                } while (revcode < treeLen);
            }
            nextCode[bits] = code + (1 << (16 - bits));
        }
    }

    public int GetSymbol(StreamManipulator input)
    {
        int lookahead, symbol;
        if ((lookahead = input.PeekBits(9)) >= 0)
        {
            symbol = Tree[lookahead];
            int bitlen = symbol & 15;

            if (symbol >= 0)
            {
                if (bitlen == 0)
                    throw new DecompressionException("Encountered invalid codelength 0");
                input.DropBits(bitlen);
                return symbol >> 4;
            }
            int subtree = -(symbol >> 4);
            if ((lookahead = input.PeekBits(bitlen)) >= 0)
            {
                symbol = Tree[subtree | (lookahead >> 9)];
                input.DropBits(symbol & 15);
                return symbol >> 4;
            }
            else
            {
                int bits = input.AvailableBits;
                lookahead = input.PeekBits(bits);
                symbol = Tree[subtree | (lookahead >> 9)];
                if ((symbol & 15) <= bits)
                {
                    input.DropBits(symbol & 15);
                    return symbol >> 4;
                }
                else
                {
                    return -1;
                }
            }
        }
        else
        {
            int bits = input.AvailableBits;
            lookahead = input.PeekBits(bits);
            symbol = Tree[lookahead];
            if (symbol >= 0 && (symbol & 15) <= bits)
            {
                input.DropBits(symbol & 15);
                return symbol >> 4;
            }
            else
            {
                return -1;
            }
        }
    }
}
