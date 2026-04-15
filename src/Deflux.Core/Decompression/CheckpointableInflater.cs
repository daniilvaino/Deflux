using System;
using System.IO;

namespace Deflux.Core.Decompression;

/// <summary>
/// Wraps Inflater with checkpoint save/restore capability.
/// Manages feeding compressed data from a SubStream and producing decompressed output.
/// </summary>
internal class CheckpointableInflater
{
    private const int InputBufferSize = 8192;
    private const int OutputBufferSize = 8192;

    private readonly Inflater _inflater;
    private readonly byte[] _inputBuffer = new byte[InputBufferSize];
    private long _totalBytesFeeded;

    public CheckpointableInflater()
    {
        _inflater = new Inflater();
    }

    public Inflater Inflater => _inflater;

    /// <summary>
    /// Total bytes fed from the source stream into the inflater.
    /// Used to compute adjustedCompressedOffset.
    /// </summary>
    public long TotalBytesFeeded => _totalBytesFeeded;

    public bool IsFinished => _inflater.IsFinished;

    /// <summary>
    /// Inflate next chunk of data from the source stream.
    /// Returns the number of decompressed bytes written to output.
    /// </summary>
    public int Inflate(Stream compressedStream, byte[] output, int offset, int count)
    {
        int totalDecompressed = 0;

        while (totalDecompressed < count)
        {
            int decompressed = _inflater.Inflate(output, offset + totalDecompressed, count - totalDecompressed);
            totalDecompressed += decompressed;

            if (_inflater.IsFinished || totalDecompressed >= count)
                break;

            if (_inflater.IsNeedingInput)
            {
                int read = compressedStream.Read(_inputBuffer, 0, InputBufferSize);
                if (read == 0)
                    break; // No more compressed data
                _inflater.SetInput(_inputBuffer, 0, read);
                _totalBytesFeeded += read;
            }
        }

        return totalDecompressed;
    }

    /// <summary>
    /// Computes the adjusted compressed offset accounting for read-ahead
    /// in the StreamManipulator. This is where the source stream should seek
    /// to upon restore.
    /// </summary>
    public long AdjustedCompressedOffset
    {
        get
        {
            var sm = _inflater.input;
            int unconsumedInSm = sm.WindowEnd - sm.WindowStart;
            return _totalBytesFeeded - unconsumedInSm;
        }
    }

    /// <summary>
    /// Saves the complete DEFLATE decompressor state.
    /// All arrays are deep-copied to prevent mutation after save.
    /// </summary>
    public InflaterState SaveState()
    {
        var inf = _inflater;
        var ow = inf.outputWindow;
        var sm = inf.input;

        // Deep copy OutputWindow
        byte[] windowCopy = new byte[OutputWindow.WindowSize];
        Array.Copy(ow.WindowData, 0, windowCopy, 0, OutputWindow.WindowSize);

        // Deep copy StreamManipulator window
        byte[] smWindowCopy;
        if (sm.Window != null && sm.Window.Length > 0)
        {
            smWindowCopy = new byte[sm.Window.Length];
            Array.Copy(sm.Window, 0, smWindowCopy, 0, sm.Window.Length);
        }
        else
        {
            smWindowCopy = Array.Empty<byte>();
        }

        // Deep copy Huffman trees (null for static/not-active)
        short[]? litlenTreeCopy = DeepCopyTree(inf.litlenTree);
        short[]? distTreeCopy = DeepCopyTree(inf.distTree);

        // DynHeader state (if mid-dynamic)
        InflaterDynHeaderState? dynHeaderState = null;
        if (inf.dynHeader != null && inf.mode == Inflater.DECODE_DYN_HEADER)
        {
            dynHeaderState = SaveDynHeaderState(inf.dynHeader);
        }

        return new InflaterState
        {
            Mode = inf.mode,
            NeededBits = inf.neededBits,
            RepLength = inf.repLength,
            RepDist = inf.repDist,
            UncomprLen = inf.uncomprLen,
            IsLastBlock = inf.isLastBlock,
            TotalOut = inf.totalOut,
            TotalIn = inf.totalIn,

            // OutputWindow
            WindowData = windowCopy,
            WindowEnd = ow.WindowEnd,
            WindowFilled = ow.WindowFilled,

            // StreamManipulator
            SmWindow = smWindowCopy,
            SmWindowStart = sm.WindowStart,
            SmWindowEnd = sm.WindowEnd,
            SmBuffer = sm.Buffer,
            SmBitsInBuffer = sm.BitsInBuffer,

            // Huffman trees
            LitlenTree = litlenTreeCopy,
            DistTree = distTreeCopy,

            // DynHeader
            DynHeaderState = dynHeaderState,

            // Adler32
            Adler32Value = inf.adler.CheckValue,

            // For seek
            AdjustedCompressedOffset = AdjustedCompressedOffset,
            TotalBytesFeeded = _totalBytesFeeded,
        };
    }

    /// <summary>
    /// Restores the complete DEFLATE decompressor state.
    /// After restore, caller must seek the source stream to AdjustedCompressedOffset
    /// and feed remaining bytes through SetInput.
    /// </summary>
    public void RestoreState(InflaterState state)
    {
        var inf = _inflater;

        inf.mode = state.Mode;
        inf.neededBits = state.NeededBits;
        inf.repLength = state.RepLength;
        inf.repDist = state.RepDist;
        inf.uncomprLen = state.UncomprLen;
        inf.isLastBlock = state.IsLastBlock;
        inf.totalOut = state.TotalOut;
        inf.totalIn = state.TotalIn;

        // OutputWindow
        inf.outputWindow.RestoreState(state.WindowData, state.WindowEnd, state.WindowFilled);

        // StreamManipulator
        inf.input.RestoreState(state.SmWindow, state.SmWindowStart, state.SmWindowEnd,
            state.SmBuffer, state.SmBitsInBuffer);

        // Huffman trees
        // LitlenTree/DistTree are null in checkpoint when they were static (DefLitLenTree/DefDistTree)
        // or when not active (mode not in HUFFMAN range). Restore accordingly.
        if (state.LitlenTree != null)
        {
            inf.litlenTree = new InflaterHuffmanTree(Array.Empty<byte>());
            RestoreTreeDirect(inf.litlenTree, state.LitlenTree);
        }
        else if (state.Mode >= Inflater.DECODE_HUFFMAN && state.Mode <= Inflater.DECODE_HUFFMAN_DISTBITS)
        {
            // Trees were null in checkpoint = they were static
            inf.litlenTree = InflaterHuffmanTree.DefLitLenTree;
            inf.distTree = InflaterHuffmanTree.DefDistTree;
        }
        else
        {
            inf.litlenTree = null;
        }

        if (state.DistTree != null)
        {
            inf.distTree = new InflaterHuffmanTree(Array.Empty<byte>());
            RestoreTreeDirect(inf.distTree, state.DistTree);
        }
        else if (state.Mode < Inflater.DECODE_HUFFMAN || state.Mode > Inflater.DECODE_HUFFMAN_DISTBITS)
        {
            inf.distTree = null;
        }
        // else: distTree already set to DefDistTree above

        // DynHeader
        if (state.DynHeaderState != null)
        {
            inf.dynHeader = new InflaterDynHeader(inf.input);
            RestoreDynHeaderState(inf.dynHeader, state.DynHeaderState);
        }
        else
        {
            inf.dynHeader = null;
        }

        // Adler32
        inf.adler.RestoreState(state.Adler32Value);

        _totalBytesFeeded = state.TotalBytesFeeded;
    }

    private static bool IsStaticTree(int mode)
    {
        return mode >= Inflater.DECODE_HUFFMAN && mode <= Inflater.DECODE_HUFFMAN_DISTBITS;
    }

    private static short[]? DeepCopyTree(InflaterHuffmanTree? tree)
    {
        if (tree == null) return null;
        // Don't serialize static trees — they are rebuilt from constants
        if (ReferenceEquals(tree, InflaterHuffmanTree.DefLitLenTree) ||
            ReferenceEquals(tree, InflaterHuffmanTree.DefDistTree))
            return null;
        var src = tree.Tree;
        var copy = new short[src.Length];
        Array.Copy(src, 0, copy, 0, src.Length);
        return copy;
    }

    private static void RestoreTreeDirect(InflaterHuffmanTree tree, short[] data)
    {
        var dest = tree.Tree;
        if (dest.Length != data.Length)
        {
            // Rebuild with correct size
            tree.Tree = new short[data.Length];
        }
        Array.Copy(data, 0, tree.Tree, 0, data.Length);
    }

    private static InflaterDynHeaderState SaveDynHeaderState(InflaterDynHeader dh)
    {
        byte[] codeLengthsCopy = new byte[dh.CodeLengths.Length];
        Array.Copy(dh.CodeLengths, 0, codeLengthsCopy, 0, dh.CodeLengths.Length);

        return new InflaterDynHeaderState
        {
            Stage = dh.Stage,
            LitLenCodeCount = dh.LitLenCodeCount,
            DistanceCodeCount = dh.DistanceCodeCount,
            MetaCodeCount = dh.MetaCodeCount,
            CodeLengths = codeLengthsCopy,
            LoopIndex = dh.LoopIndex,
            RepeatCount = dh.RepeatCount,
            RepeatCodeLength = dh.RepeatCodeLength,
            DataCodeCount = dh.DataCodeCount,
            PendingSymbol = dh.PendingSymbol,
        };
    }

    private static void RestoreDynHeaderState(InflaterDynHeader dh, InflaterDynHeaderState state)
    {
        dh.Stage = state.Stage;
        dh.LitLenCodeCount = state.LitLenCodeCount;
        dh.DistanceCodeCount = state.DistanceCodeCount;
        dh.MetaCodeCount = state.MetaCodeCount;
        Array.Copy(state.CodeLengths, 0, dh.CodeLengths, 0, state.CodeLengths.Length);
        dh.LoopIndex = state.LoopIndex;
        dh.RepeatCount = state.RepeatCount;
        dh.RepeatCodeLength = state.RepeatCodeLength;
        dh.DataCodeCount = state.DataCodeCount;
        dh.RestorePendingSymbol(state.PendingSymbol);

        // If we're past ReadMetaLengths, we need the meta tree rebuilt
        if (state.Stage > DynHeaderStage.ReadMetaLengths)
        {
            var metaTree = new InflaterHuffmanTree(state.CodeLengths);
            dh.SetMetaCodeTree(metaTree);
        }
    }
}

/// <summary>
/// Complete serializable state of the DEFLATE decompressor.
/// </summary>
internal class InflaterState
{
    // Inflater fields
    public int Mode;
    public int NeededBits;
    public int RepLength;
    public int RepDist;
    public int UncomprLen;
    public bool IsLastBlock;
    public long TotalOut;
    public long TotalIn;

    // OutputWindow (~32KB)
    public byte[] WindowData = Array.Empty<byte>();
    public int WindowEnd;
    public int WindowFilled;

    // StreamManipulator
    public byte[] SmWindow = Array.Empty<byte>();
    public int SmWindowStart;
    public int SmWindowEnd;
    public uint SmBuffer;
    public int SmBitsInBuffer;

    // Huffman trees (null = static/not-active)
    public short[]? LitlenTree;
    public short[]? DistTree;

    // DynHeader (null if not mid-dynamic)
    public InflaterDynHeaderState? DynHeaderState;

    // Adler32
    public uint Adler32Value;

    // Seek position
    public long AdjustedCompressedOffset;
    public long TotalBytesFeeded;
}

/// <summary>
/// Serializable state of InflaterDynHeader.
/// </summary>
internal class InflaterDynHeaderState
{
    public DynHeaderStage Stage;
    public int LitLenCodeCount;
    public int DistanceCodeCount;
    public int MetaCodeCount;
    public byte[] CodeLengths = null!;
    public int LoopIndex;
    public int RepeatCount;
    public byte RepeatCodeLength;
    public int DataCodeCount;
    public int PendingSymbol;
}
