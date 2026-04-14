using System;
using System.IO;
using System.Text;
using Deflux.Core.Decompression;
using Deflux.Core.Exceptions;
using Deflux.Core.Xml;

namespace Deflux.Core.Checkpoint;

/// <summary>
/// Binary serializer for checkpoint data.
/// Format: version (1B) + totalLength (4B) + payloadCrc32 (4B) + payload.
/// All multi-byte values are little-endian via BinaryWriter/BinaryReader.
/// </summary>
internal static class CheckpointSerializer
{
    public static byte[] Serialize(CheckpointData data)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Placeholder: version + totalLength + payloadCrc32
        bw.Write(CheckpointData.CurrentVersion);    // offset 0
        bw.Write((uint)0);                          // offset 1: totalLength placeholder
        bw.Write((uint)0);                          // offset 5: payloadCrc32 placeholder

        // ── Entry identification ──
        WriteString(bw, data.EntryName);
        bw.Write(data.EntryCrc32);
        bw.Write(data.EntryCompressedSize);
        bw.Write(data.AdjustedCompressedOffset);

        // ── DEFLATE state section ──
        WriteDeflateState(bw, data.DeflateState);

        // ── Pending decompressed bytes ──
        WriteByteArray(bw, data.PendingDecompressedBytes);

        // ── XML parser state section ──
        WriteXmlState(bw, data.XmlState);

        bw.Flush();

        byte[] result = ms.ToArray();

        // Fill totalLength
        uint totalLength = (uint)result.Length;
        BitConverter.TryWriteBytes(result.AsSpan(1), totalLength);

        // Compute CRC-32 of payload (everything after offset 9)
        uint payloadCrc = Crc32.Compute(result, 9, result.Length - 9);
        BitConverter.TryWriteBytes(result.AsSpan(5), payloadCrc);

        return result;
    }

    public static CheckpointData Deserialize(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 9)
            throw new CheckpointMismatchException("Checkpoint data too short");

        // Version check
        byte version = bytes[0];
        if (version != CheckpointData.CurrentVersion)
            throw new CheckpointVersionException(CheckpointData.CurrentVersion, version);

        // Total length check
        uint totalLength = BitConverter.ToUInt32(bytes, 1);
        if (totalLength != bytes.Length)
            throw new CheckpointMismatchException("Checkpoint length mismatch");

        // CRC-32 integrity check
        uint storedCrc = BitConverter.ToUInt32(bytes, 5);
        uint computedCrc = Crc32.Compute(bytes, 9, bytes.Length - 9);
        if (storedCrc != computedCrc)
            throw new CheckpointMismatchException("Checkpoint payload corrupted", storedCrc, computedCrc);

        using var ms = new MemoryStream(bytes, 9, bytes.Length - 9);
        using var br = new BinaryReader(ms, Encoding.UTF8);

        var data = new CheckpointData();

        // ── Entry identification ──
        data.EntryName = ReadString(br);
        data.EntryCrc32 = br.ReadUInt32();
        data.EntryCompressedSize = br.ReadInt64();
        data.AdjustedCompressedOffset = br.ReadInt64();

        // ── DEFLATE state ──
        data.DeflateState = ReadDeflateState(br);

        // ── Pending decompressed bytes ──
        data.PendingDecompressedBytes = ReadByteArray(br);

        // ── XML parser state ──
        data.XmlState = ReadXmlState(br);

        return data;
    }

    // ── DEFLATE state ──

    private static void WriteDeflateState(BinaryWriter bw, InflaterState s)
    {
        // Mark section start for length prefix
        long sectionStart = bw.BaseStream.Position;
        bw.Write((uint)0); // section length placeholder

        bw.Write(s.Mode);
        bw.Write(s.NeededBits);
        bw.Write(s.RepLength);
        bw.Write(s.RepDist);
        bw.Write(s.UncomprLen);
        bw.Write(s.IsLastBlock);
        bw.Write(s.TotalOut);
        bw.Write(s.TotalIn);

        // OutputWindow
        WriteByteArray(bw, s.WindowData);
        bw.Write(s.WindowEnd);
        bw.Write(s.WindowFilled);

        // StreamManipulator
        WriteByteArray(bw, s.SmWindow);
        bw.Write(s.SmWindowStart);
        bw.Write(s.SmWindowEnd);
        bw.Write(s.SmBuffer);
        bw.Write(s.SmBitsInBuffer);

        // Huffman trees
        WriteShortArray(bw, s.LitlenTree);
        WriteShortArray(bw, s.DistTree);

        // DynHeader
        bw.Write(s.DynHeaderState != null);
        if (s.DynHeaderState != null)
            WriteDynHeaderState(bw, s.DynHeaderState);

        // Adler32
        bw.Write(s.Adler32Value);

        // Seek info
        bw.Write(s.AdjustedCompressedOffset);
        bw.Write(s.TotalBytesFeeded);

        // Fill section length
        long sectionEnd = bw.BaseStream.Position;
        uint sectionLen = (uint)(sectionEnd - sectionStart - 4);
        bw.BaseStream.Seek(sectionStart, SeekOrigin.Begin);
        bw.Write(sectionLen);
        bw.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
    }

    private static InflaterState ReadDeflateState(BinaryReader br)
    {
        uint sectionLen = br.ReadUInt32(); // read but don't enforce — we read fields sequentially

        var s = new InflaterState();
        s.Mode = br.ReadInt32();
        s.NeededBits = br.ReadInt32();
        s.RepLength = br.ReadInt32();
        s.RepDist = br.ReadInt32();
        s.UncomprLen = br.ReadInt32();
        s.IsLastBlock = br.ReadBoolean();
        s.TotalOut = br.ReadInt64();
        s.TotalIn = br.ReadInt64();

        // OutputWindow
        s.WindowData = ReadByteArray(br);
        s.WindowEnd = br.ReadInt32();
        s.WindowFilled = br.ReadInt32();

        // StreamManipulator
        s.SmWindow = ReadByteArray(br);
        s.SmWindowStart = br.ReadInt32();
        s.SmWindowEnd = br.ReadInt32();
        s.SmBuffer = br.ReadUInt32();
        s.SmBitsInBuffer = br.ReadInt32();

        // Huffman trees
        s.LitlenTree = ReadShortArray(br);
        s.DistTree = ReadShortArray(br);

        // DynHeader
        bool hasDynHeader = br.ReadBoolean();
        if (hasDynHeader)
            s.DynHeaderState = ReadDynHeaderState(br);

        // Adler32
        s.Adler32Value = br.ReadUInt32();

        // Seek info
        s.AdjustedCompressedOffset = br.ReadInt64();
        s.TotalBytesFeeded = br.ReadInt64();

        return s;
    }

    // ── DynHeader state ──

    private static void WriteDynHeaderState(BinaryWriter bw, InflaterDynHeaderState s)
    {
        bw.Write((byte)s.Stage);
        bw.Write(s.LitLenCodeCount);
        bw.Write(s.DistanceCodeCount);
        bw.Write(s.MetaCodeCount);
        WriteByteArray(bw, s.CodeLengths);
        bw.Write(s.LoopIndex);
        bw.Write(s.RepeatCount);
        bw.Write(s.RepeatCodeLength);
        bw.Write(s.DataCodeCount);
        bw.Write(s.PendingSymbol);
    }

    private static InflaterDynHeaderState ReadDynHeaderState(BinaryReader br)
    {
        return new InflaterDynHeaderState
        {
            Stage = (DynHeaderStage)br.ReadByte(),
            LitLenCodeCount = br.ReadInt32(),
            DistanceCodeCount = br.ReadInt32(),
            MetaCodeCount = br.ReadInt32(),
            CodeLengths = ReadByteArray(br),
            LoopIndex = br.ReadInt32(),
            RepeatCount = br.ReadInt32(),
            RepeatCodeLength = br.ReadByte(),
            DataCodeCount = br.ReadInt32(),
            PendingSymbol = br.ReadInt32(),
        };
    }

    // ── XML state ──

    private static void WriteXmlState(BinaryWriter bw, XmlParserState s)
    {
        long sectionStart = bw.BaseStream.Position;
        bw.Write((uint)0); // section length placeholder

        bw.Write((byte)s.CurrentState);
        bw.Write(s.Depth);

        // Element stack
        bw.Write(s.ElementStack.Length);
        foreach (var frame in s.ElementStack)
        {
            WriteString(bw, frame.LocalName);
            WriteNullableString(bw, frame.Prefix);
            WriteNullableString(bw, frame.NamespaceUri);
            bw.Write(frame.Depth);
        }

        // Namespace bindings
        bw.Write(s.NamespaceBindings.Length);
        foreach (var binding in s.NamespaceBindings)
        {
            WriteString(bw, binding.Prefix);
            WriteString(bw, binding.Uri);
            bw.Write(binding.ScopeDepth);
        }

        // Pending text
        WriteNullableString(bw, s.PendingText);

        // Line/column
        bw.Write(s.LineNumber);
        bw.Write(s.ColumnNumber);

        // Incomplete UTF-8
        WriteByteArray(bw, s.IncompleteUtf8);

        // Flags
        bw.Write(s.BomSkipped);
        bw.Write(s.XmlDeclParsed);

        // Fill section length
        long sectionEnd = bw.BaseStream.Position;
        uint sectionLen = (uint)(sectionEnd - sectionStart - 4);
        bw.BaseStream.Seek(sectionStart, SeekOrigin.Begin);
        bw.Write(sectionLen);
        bw.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
    }

    private static XmlParserState ReadXmlState(BinaryReader br)
    {
        uint sectionLen = br.ReadUInt32();

        var s = new XmlParserState();
        s.CurrentState = (ParseState)br.ReadByte();
        s.Depth = br.ReadInt32();

        // Element stack
        int stackCount = br.ReadInt32();
        s.ElementStack = new ElementFrame[stackCount];
        for (int i = 0; i < stackCount; i++)
        {
            s.ElementStack[i] = new ElementFrame
            {
                LocalName = ReadString(br),
                Prefix = ReadNullableString(br),
                NamespaceUri = ReadNullableString(br),
                Depth = br.ReadInt32(),
            };
        }

        // Namespace bindings
        int bindingCount = br.ReadInt32();
        s.NamespaceBindings = new NamespaceBinding[bindingCount];
        for (int i = 0; i < bindingCount; i++)
        {
            s.NamespaceBindings[i] = new NamespaceBinding
            {
                Prefix = ReadString(br),
                Uri = ReadString(br),
                ScopeDepth = br.ReadInt32(),
            };
        }

        // Pending text
        s.PendingText = ReadNullableString(br);

        // Line/column
        s.LineNumber = br.ReadInt32();
        s.ColumnNumber = br.ReadInt32();

        // Incomplete UTF-8
        s.IncompleteUtf8 = ReadByteArray(br);

        // Flags
        s.BomSkipped = br.ReadBoolean();
        s.XmlDeclParsed = br.ReadBoolean();

        return s;
    }

    // ── Primitive helpers ──

    private static void WriteString(BinaryWriter bw, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        bw.Write((ushort)bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadString(BinaryReader br)
    {
        int len = br.ReadUInt16();
        byte[] bytes = br.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteNullableString(BinaryWriter bw, string? value)
    {
        if (value == null)
        {
            bw.Write(false);
        }
        else
        {
            bw.Write(true);
            WriteString(bw, value);
        }
    }

    private static string? ReadNullableString(BinaryReader br)
    {
        bool hasValue = br.ReadBoolean();
        return hasValue ? ReadString(br) : null;
    }

    private static void WriteByteArray(BinaryWriter bw, byte[] data)
    {
        bw.Write(data.Length);
        if (data.Length > 0)
            bw.Write(data);
    }

    private static byte[] ReadByteArray(BinaryReader br)
    {
        int len = br.ReadInt32();
        return len > 0 ? br.ReadBytes(len) : Array.Empty<byte>();
    }

    private static void WriteShortArray(BinaryWriter bw, short[]? data)
    {
        if (data == null)
        {
            bw.Write(0);
            return;
        }
        bw.Write(data.Length);
        for (int i = 0; i < data.Length; i++)
            bw.Write(data[i]);
    }

    private static short[]? ReadShortArray(BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len == 0) return null;
        var arr = new short[len];
        for (int i = 0; i < len; i++)
            arr[i] = br.ReadInt16();
        return arr;
    }
}
