using System;

namespace Deflux.Core;

// ── Enums ──

public enum XmlNodeKind { Element, EndElement, Text, CData, None }

public enum CellType { String, Number, Boolean, Error, Blank }

public enum CompressionMethod { Stored = 0, Deflate = 8 }

// ── Value types ──

public readonly record struct Cell(int ColumnIndex, string? Value, CellType Type);

public readonly record struct Row(int RowIndex, ReadOnlyMemory<Cell> Cells);

public readonly record struct ZipEntry(
    string Name,
    CompressionMethod Method,
    uint Crc32,
    long CompressedSize,
    long UncompressedSize,
    long LocalHeaderOffset);

/// <summary>
/// Sheet name paired with a checkpoint blob for instant OpenSheet() restore.
/// </summary>
public readonly record struct SheetInfo(string Name, byte[] Checkpoint);

// ── Interfaces ──

public interface ICheckpointable
{
    byte[] SaveCheckpoint();
    void RestoreCheckpoint(byte[] data);
}
