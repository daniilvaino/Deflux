// Originally from SharpZipLib (MIT License)
// https://github.com/icsharpcode/SharpZipLib
// Refactored: yield return state machine → explicit DynHeaderStage enum for checkpoint support.

using System;
using Deflux.Core.Exceptions;

namespace Deflux.Core.Decompression;

/// <summary>
/// Stages of dynamic Huffman header parsing.
/// Explicit enum replaces compiler-generated IEnumerator state machine,
/// making the state fully serializable for checkpoint support.
/// </summary>
internal enum DynHeaderStage
{
    ReadLitLenCount,
    ReadDistCount,
    ReadMetaCount,
    ReadMetaLengths,
    BuildMetaTree,
    DecodeDataLengths,
    BuildTrees,
    Complete
}

/// <summary>
/// Parses dynamic DEFLATE block headers (type 2 blocks).
/// Uses explicit state machine instead of yield return for checkpoint serialization.
/// </summary>
internal class InflaterDynHeader
{
    private const int LITLEN_MAX = 286;
    private const int DIST_MAX = 30;
    private const int CODELEN_MAX = LITLEN_MAX + DIST_MAX;
    private const int META_MAX = 19;

    private static readonly int[] MetaCodeLengthIndex =
        { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private readonly StreamManipulator input;

    // State machine fields
    internal DynHeaderStage Stage;
    internal int LitLenCodeCount;
    internal int DistanceCodeCount;
    internal int MetaCodeCount;
    internal byte[] CodeLengths = new byte[CODELEN_MAX];
    internal int LoopIndex;
    internal int RepeatCount;
    internal byte RepeatCodeLength;
    internal int DataCodeCount;

    // Sub-state for DecodeDataLengths
    private int pendingSymbol = -1;

    private InflaterHuffmanTree? metaCodeTree;
    private InflaterHuffmanTree? litLenTree;
    private InflaterHuffmanTree? distTree;

    public InflaterDynHeader(StreamManipulator input)
    {
        this.input = input;
        Stage = DynHeaderStage.ReadLitLenCount;
    }

    public InflaterHuffmanTree LiteralLengthTree
        => litLenTree ?? throw new DecompressionException("Header not yet fully read");

    public InflaterHuffmanTree DistanceTree
        => distTree ?? throw new DecompressionException("Header not yet fully read");

    /// <summary>
    /// Attempts to read/continue reading the dynamic header.
    /// Returns true when complete, false when more input is needed.
    /// </summary>
    public bool AttemptRead()
    {
        while (true)
        {
            switch (Stage)
            {
                case DynHeaderStage.ReadLitLenCount:
                    if (!input.TryGetBits(5, ref LitLenCodeCount, 257))
                        return false;
                    if (LitLenCodeCount > LITLEN_MAX)
                        throw new DecompressionException($"Invalid literal/length code count: {LitLenCodeCount}");
                    Stage = DynHeaderStage.ReadDistCount;
                    break;

                case DynHeaderStage.ReadDistCount:
                    if (!input.TryGetBits(5, ref DistanceCodeCount, 1))
                        return false;
                    if (DistanceCodeCount > DIST_MAX)
                        throw new DecompressionException($"Invalid distance code count: {DistanceCodeCount}");
                    DataCodeCount = LitLenCodeCount + DistanceCodeCount;
                    Stage = DynHeaderStage.ReadMetaCount;
                    break;

                case DynHeaderStage.ReadMetaCount:
                    if (!input.TryGetBits(4, ref MetaCodeCount, 4))
                        return false;
                    if (MetaCodeCount > META_MAX)
                        throw new DecompressionException($"Invalid meta code count: {MetaCodeCount}");
                    LoopIndex = 0;
                    Stage = DynHeaderStage.ReadMetaLengths;
                    break;

                case DynHeaderStage.ReadMetaLengths:
                    while (LoopIndex < MetaCodeCount)
                    {
                        if (!input.TryGetBits(3, ref CodeLengths, MetaCodeLengthIndex[LoopIndex]))
                            return false;
                        LoopIndex++;
                    }
                    Stage = DynHeaderStage.BuildMetaTree;
                    break;

                case DynHeaderStage.BuildMetaTree:
                    metaCodeTree = new InflaterHuffmanTree(CodeLengths);
                    LoopIndex = 0;
                    pendingSymbol = -1;
                    Stage = DynHeaderStage.DecodeDataLengths;
                    break;

                case DynHeaderStage.DecodeDataLengths:
                    if (!DecodeDataLengths())
                        return false;
                    Stage = DynHeaderStage.BuildTrees;
                    break;

                case DynHeaderStage.BuildTrees:
                    if (CodeLengths[256] == 0)
                        throw new DecompressionException("Dynamic header: end-of-block code missing");

                    litLenTree = new InflaterHuffmanTree(new ArraySegment<byte>(CodeLengths, 0, LitLenCodeCount));
                    distTree = new InflaterHuffmanTree(new ArraySegment<byte>(CodeLengths, LitLenCodeCount, DistanceCodeCount));
                    Stage = DynHeaderStage.Complete;
                    return true;

                case DynHeaderStage.Complete:
                    return true;

                default:
                    throw new DecompressionException($"Unknown DynHeader stage: {Stage}");
            }
        }
    }

    private bool DecodeDataLengths()
    {
        // If we have a pending repeat operation, finish it first
        if (RepeatCount > 0)
        {
            if (LoopIndex + RepeatCount > DataCodeCount)
                throw new DecompressionException("Code lengths overflow data code count");
            while (RepeatCount-- > 0)
                CodeLengths[LoopIndex++] = RepeatCodeLength;
        }

        while (LoopIndex < DataCodeCount)
        {
            int symbol;
            if (pendingSymbol >= 0)
            {
                symbol = pendingSymbol;
                pendingSymbol = -1;
            }
            else
            {
                symbol = metaCodeTree!.GetSymbol(input);
                if (symbol < 0)
                    return false;
            }

            if (symbol < 16)
            {
                CodeLengths[LoopIndex++] = (byte)symbol;
            }
            else
            {
                RepeatCount = 0;

                if (symbol == 16)
                {
                    if (LoopIndex == 0)
                        throw new DecompressionException("Cannot repeat previous code length at index 0");
                    RepeatCodeLength = CodeLengths[LoopIndex - 1];
                    if (!input.TryGetBits(2, ref RepeatCount, 3))
                    {
                        pendingSymbol = symbol;
                        return false;
                    }
                }
                else if (symbol == 17)
                {
                    RepeatCodeLength = 0;
                    if (!input.TryGetBits(3, ref RepeatCount, 3))
                    {
                        pendingSymbol = symbol;
                        return false;
                    }
                }
                else // symbol == 18
                {
                    RepeatCodeLength = 0;
                    if (!input.TryGetBits(7, ref RepeatCount, 11))
                    {
                        pendingSymbol = symbol;
                        return false;
                    }
                }

                if (LoopIndex + RepeatCount > DataCodeCount)
                    throw new DecompressionException("Code lengths overflow data code count");
                while (RepeatCount-- > 0)
                    CodeLengths[LoopIndex++] = RepeatCodeLength;
            }
        }
        return true;
    }

    // ── Checkpoint support ──

    internal int PendingSymbol => pendingSymbol;

    internal void RestorePendingSymbol(int value) => pendingSymbol = value;

    internal void SetMetaCodeTree(InflaterHuffmanTree tree) => metaCodeTree = tree;
}
