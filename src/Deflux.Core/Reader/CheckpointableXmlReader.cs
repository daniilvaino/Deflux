using System;
using System.Collections.Generic;
using System.IO;
using Deflux.Core.Checkpoint;
using Deflux.Core.Decompression;
using Deflux.Core.Exceptions;
using Deflux.Core.Xml;
using Deflux.Core.Zip;

namespace Deflux.Core.Reader;

/// <summary>
/// Unified pipeline: ZIP → DEFLATE → XML with checkpoint support.
/// Forward-only XML reader over a DEFLATE-compressed ZIP entry.
/// Does NOT own the stream — caller manages its lifetime.
/// </summary>
public class CheckpointableXmlReader : IDisposable, ICheckpointable
{
    private const int InflateBufferSize = 8192;

    private readonly Stream _zipStream;
    private readonly string _entryName;

    private ZipNavigator _zip;
    private ZipEntry _entry;
    private Stream _entryStream;
    private CheckpointableInflater _inflater;
    private MinimalXmlParser _parser;
    private bool _disposed;

    private byte[] _inflateBuffer = new byte[InflateBufferSize];
    private int _inflateBufferPos;
    private int _inflateBufferLen;

    /// <summary>
    /// Create reader over a seekable ZIP stream.
    /// The reader does NOT own the stream — caller manages its lifetime.
    /// </summary>
    public CheckpointableXmlReader(Stream zipStream, string entryName)
    {
        if (!zipStream.CanSeek)
            throw new ArgumentException("Stream must be seekable", nameof(zipStream));

        _zipStream = zipStream;
        _entryName = entryName;

        _zip = new ZipNavigator(_zipStream);
        _entry = _zip.FindEntry(entryName);

        if (_entry.Method == CompressionMethod.Stored)
            throw new UnsupportedCompressionException(0);

        _entryStream = _zip.OpenEntryStream(_entry);
        _inflater = new CheckpointableInflater();
        _parser = new MinimalXmlParser();
    }

    // ── Current node ──

    public XmlNodeKind NodeKind => _parser.NodeKind;
    public string LocalName => _parser.LocalName;
    public string? Prefix => _parser.Prefix;
    public string? NamespaceUri => _parser.NamespaceUri;
    public string? Value => _parser.Value;
    public int Depth => _parser.Depth;
    public int AttributeCount => _parser.AttributeCount;

    public string? GetAttribute(string localName) => _parser.GetAttribute(localName);
    public string? GetAttribute(string localName, string ns) => _parser.GetAttribute(localName, ns);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CheckpointableXmlReader));
    }

    // ── Forward-only reading ──

    public bool Read()
    {
        ThrowIfDisposed();
        while (true)
        {
            if (_parser.Read())
                return true;

            if (_parser.IsEof)
                return false;

            if (!FeedParser())
                return false;
        }
    }

    private bool FeedParser()
    {
        if (_inflateBufferPos < _inflateBufferLen)
        {
            int remaining = _inflateBufferLen - _inflateBufferPos;
            _parser.Feed(_inflateBuffer.AsSpan(_inflateBufferPos, remaining));
            _inflateBufferPos = _inflateBufferLen;
            return true;
        }

        if (_inflater.IsFinished)
        {
            _parser.FeedEof();
            return false;
        }

        int decompressed = _inflater.Inflate(_entryStream, _inflateBuffer, 0, InflateBufferSize);
        if (decompressed == 0)
        {
            _parser.FeedEof();
            return false;
        }

        _inflateBufferPos = decompressed;
        _inflateBufferLen = decompressed;
        _parser.Feed(_inflateBuffer.AsSpan(0, decompressed));
        return true;
    }

    // ── Navigation / Filtering ──

    public bool SkipTo(string localName, string? namespaceUri = null)
    {
        while (Read())
        {
            if (NodeKind == XmlNodeKind.Element &&
                LocalName == localName &&
                (namespaceUri == null || NamespaceUri == namespaceUri))
                return true;
        }
        return false;
    }

    public bool SkipToEndElement()
    {
        int targetDepth = Depth;
        while (Read())
        {
            if (NodeKind == XmlNodeKind.EndElement && Depth == targetDepth)
                return true;
        }
        return false;
    }

    public void SkipChildren()
    {
        int startDepth = Depth;
        while (Read())
        {
            if (NodeKind == XmlNodeKind.EndElement && Depth == startDepth)
                return;
        }
    }

    public IEnumerable<bool> ReadElements(string localName, string? ns = null)
    {
        int parentDepth = Depth;
        while (Read())
        {
            if (NodeKind == XmlNodeKind.Element &&
                LocalName == localName &&
                (ns == null || NamespaceUri == ns))
            {
                yield return true;
            }
            else if (NodeKind == XmlNodeKind.EndElement && Depth < parentDepth)
            {
                yield break;
            }
        }
    }

    // ── Checkpoints ──

    /// <summary>
    /// Peeks the entry name stored in a checkpoint blob without fully deserializing.
    /// </summary>
    public static string PeekCheckpointEntryName(byte[] data)
    {
        var cp = CheckpointSerializer.Deserialize(data);
        return cp.EntryName;
    }

    /// <summary>
    /// Save checkpoint. Must be called on a node boundary (after Read() returned true).
    /// </summary>
    public byte[] SaveCheckpoint()
    {
        ThrowIfDisposed();
        if (NodeKind == XmlNodeKind.None)
            throw new InvalidOperationException("SaveCheckpoint must be called on a node boundary (after Read())");

        var deflateState = _inflater.SaveState();
        var xmlState = _parser.SaveState();

        // Pending = inflate buffer remainder + parser unconsumed chars (re-encoded to UTF-8)
        byte[] inflateUnconsumed;
        int inflateRemaining = _inflateBufferLen - _inflateBufferPos;
        if (inflateRemaining > 0)
        {
            inflateUnconsumed = new byte[inflateRemaining];
            Array.Copy(_inflateBuffer, _inflateBufferPos, inflateUnconsumed, 0, inflateRemaining);
        }
        else
        {
            inflateUnconsumed = Array.Empty<byte>();
        }

        byte[] parserUnconsumed = _parser.GetUnconsumedBytes();

        byte[] pending;
        if (parserUnconsumed.Length > 0 || inflateUnconsumed.Length > 0)
        {
            pending = new byte[parserUnconsumed.Length + inflateUnconsumed.Length];
            Array.Copy(parserUnconsumed, 0, pending, 0, parserUnconsumed.Length);
            Array.Copy(inflateUnconsumed, 0, pending, parserUnconsumed.Length, inflateUnconsumed.Length);
        }
        else
        {
            pending = Array.Empty<byte>();
        }

        var checkpointData = new CheckpointData
        {
            EntryName = _entryName,
            EntryCrc32 = _entry.Crc32,
            EntryCompressedSize = _entry.CompressedSize,
            AdjustedCompressedOffset = deflateState.AdjustedCompressedOffset,
            DeflateState = deflateState,
            PendingDecompressedBytes = pending,
            XmlState = xmlState,
        };

        return CheckpointSerializer.Serialize(checkpointData);
    }

    /// <summary>
    /// Restore checkpoint. After this call, the next Read() continues
    /// from where SaveCheckpoint() was called.
    /// Uses the same underlying stream (re-seeks it).
    /// </summary>
    public void RestoreCheckpoint(byte[] data)
    {
        ThrowIfDisposed();
        var cp = CheckpointSerializer.Deserialize(data);

        if (cp.EntryName != _entryName)
            throw new CheckpointMismatchException(
                $"Checkpoint entry name mismatch: expected '{_entryName}', got '{cp.EntryName}'");

        // Re-read ZIP Central Directory from same stream (re-seek)
        _zip = new ZipNavigator(_zipStream);
        _entry = _zip.FindEntry(_entryName);

        if (_entry.Crc32 != cp.EntryCrc32)
            throw new CheckpointMismatchException("File modified: CRC32 mismatch",
                cp.EntryCrc32, _entry.Crc32);
        if (_entry.CompressedSize != cp.EntryCompressedSize)
            throw new CheckpointMismatchException("File modified: compressed size mismatch");

        _entryStream = _zip.OpenEntryStream(_entry);

        // Restore DEFLATE
        _inflater = new CheckpointableInflater();
        _inflater.RestoreState(cp.DeflateState);
        // StreamManipulator state already contains the unread compressed tail from the
        // last fed chunk. Seeking to AdjustedCompressedOffset would replay that tail and
        // corrupt DEFLATE state on large restores; continue from the original stream head.
        _entryStream.Seek(cp.DeflateState.TotalBytesFeeded, SeekOrigin.Begin);

        // Restore XML parser
        _parser = new MinimalXmlParser();
        _parser.RestoreState(cp.XmlState);
        if (cp.PendingDecompressedBytes.Length > 0)
            _parser.Feed(cp.PendingDecompressedBytes);

        _inflateBufferPos = 0;
        _inflateBufferLen = 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // We don't own the stream — don't dispose it
            _disposed = true;
        }
    }
}
