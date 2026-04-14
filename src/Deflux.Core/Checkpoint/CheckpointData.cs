using Deflux.Core.Decompression;
using Deflux.Core.Xml;

namespace Deflux.Core.Checkpoint;

/// <summary>
/// Complete checkpoint: entry identification + DEFLATE state + pending bytes + XML state.
/// </summary>
internal class CheckpointData
{
    public const byte CurrentVersion = 1;

    // Entry identification
    public string EntryName = null!;
    public uint EntryCrc32;
    public long EntryCompressedSize;
    public long AdjustedCompressedOffset;

    // DEFLATE state
    public InflaterState DeflateState = null!;

    // Pending decompressed bytes (inflater produced but XML parser hasn't consumed)
    public byte[] PendingDecompressedBytes = System.Array.Empty<byte>();

    // XML parser state
    public XmlParserState XmlState = null!;
}
