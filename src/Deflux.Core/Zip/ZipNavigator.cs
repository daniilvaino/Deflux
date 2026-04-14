using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Deflux.Core.Exceptions;

namespace Deflux.Core.Zip;

/// <summary>
/// Minimal ZIP navigator. Reads Central Directory for metadata,
/// opens entry data streams. Read-only, forward to SubStream.
/// Does NOT own the stream — caller is responsible for its lifetime.
/// Stream must be seekable.
/// </summary>
public class ZipNavigator : IDisposable
{
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64EocdSignature = 0x06064b50;
    private const uint Zip64EocdLocatorSignature = 0x07064b50;
    private const uint CentralDirSignature = 0x02014b50;
    private const uint LocalHeaderSignature = 0x04034b50;
    private const ushort Zip64ExtraFieldId = 0x0001;
    private const int MaxEocdSearch = 65557;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly List<ZipEntry> _entries;
    private bool _disposed;

    public IReadOnlyList<ZipEntry> Entries => _entries;

    /// <summary>
    /// Create navigator over a seekable stream.
    /// The navigator does NOT own the stream — caller manages its lifetime.
    /// </summary>
    public ZipNavigator(Stream stream) : this(stream, ownsStream: false) { }

    internal ZipNavigator(Stream stream, bool ownsStream)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable", nameof(stream));
        _stream = stream;
        _ownsStream = ownsStream;
        _entries = new List<ZipEntry>();
        ReadCentralDirectory();
    }

    public ZipEntry FindEntry(string name)
    {
        name = name.Replace('\\', '/');
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Name == name)
                return _entries[i];
        }
        throw new ZipFormatException($"Entry not found: {name}");
    }

    public Stream OpenEntryStream(string name) => OpenEntryStream(FindEntry(name));

    public Stream OpenEntryStream(ZipEntry entry)
    {
        if (entry.Method != CompressionMethod.Stored && entry.Method != CompressionMethod.Deflate)
            throw new UnsupportedCompressionException((int)entry.Method);

        long dataOffset = GetEntryDataOffset(entry.LocalHeaderOffset);
        return new SubStream(_stream, dataOffset, entry.CompressedSize);
    }

    private void ReadCentralDirectory()
    {
        var (cdOffset, cdCount) = FindEocd();
        _stream.Seek(cdOffset, SeekOrigin.Begin);

        byte[] header = new byte[46];
        for (int i = 0; i < cdCount; i++)
        {
            ReadExact(header, 0, 46);

            uint sig = ReadUInt32(header, 0);
            if (sig != CentralDirSignature)
                throw new ZipFormatException($"Invalid Central Directory entry signature: 0x{sig:X8}");

            int method = ReadUInt16(header, 10);
            if (method != 0 && method != 8)
                throw new UnsupportedCompressionException(method);

            uint crc32 = ReadUInt32(header, 16);
            uint compressedSize32 = ReadUInt32(header, 20);
            uint uncompressedSize32 = ReadUInt32(header, 24);
            int nameLength = ReadUInt16(header, 28);
            int extraLength = ReadUInt16(header, 30);
            int commentLength = ReadUInt16(header, 32);
            uint localHeaderOffset32 = ReadUInt32(header, 42);

            byte[] nameBytes = new byte[nameLength];
            ReadExact(nameBytes, 0, nameLength);
            string name = Encoding.UTF8.GetString(nameBytes).Replace('\\', '/');

            byte[] extra = extraLength > 0 ? new byte[extraLength] : Array.Empty<byte>();
            if (extraLength > 0)
                ReadExact(extra, 0, extraLength);

            if (commentLength > 0)
                _stream.Seek(commentLength, SeekOrigin.Current);

            long compressedSize = compressedSize32;
            long uncompressedSize = uncompressedSize32;
            long localHeaderOffset = localHeaderOffset32;

            bool needsZip64 =
                compressedSize32 == 0xFFFFFFFFu ||
                uncompressedSize32 == 0xFFFFFFFFu ||
                localHeaderOffset32 == 0xFFFFFFFFu;

            if (needsZip64)
            {
                (uncompressedSize, compressedSize, localHeaderOffset) = ParseZip64ExtraField(
                    extra,
                    needUncompressedSize: uncompressedSize32 == 0xFFFFFFFFu,
                    needCompressedSize: compressedSize32 == 0xFFFFFFFFu,
                    needLocalHeaderOffset: localHeaderOffset32 == 0xFFFFFFFFu,
                    fallbackUncompressedSize: uncompressedSize,
                    fallbackCompressedSize: compressedSize,
                    fallbackLocalHeaderOffset: localHeaderOffset);
            }

            _entries.Add(new ZipEntry(
                name,
                (CompressionMethod)method,
                crc32,
                compressedSize,
                uncompressedSize,
                localHeaderOffset));
        }
    }

    private (long cdOffset, int cdCount) FindEocd()
    {
        long fileLength = _stream.Length;
        if (fileLength < 22)
            throw new ZipFormatException("File too small to be a ZIP archive");

        long searchStart = Math.Max(0, fileLength - MaxEocdSearch);
        int searchLen = (int)(fileLength - searchStart);
        byte[] buffer = new byte[searchLen];

        _stream.Seek(searchStart, SeekOrigin.Begin);
        ReadExact(buffer, 0, searchLen);

        for (int i = searchLen - 22; i >= 0; i--)
        {
            if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b &&
                buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
            {
                int thisDisk = ReadUInt16(buffer, i + 4);
                int cdStartDisk = ReadUInt16(buffer, i + 6);
                int entriesThisDisk = ReadUInt16(buffer, i + 8);
                int entriesTotal = ReadUInt16(buffer, i + 10);
                uint cdSize32 = ReadUInt32(buffer, i + 12);
                uint cdOffset32 = ReadUInt32(buffer, i + 16);

                if (thisDisk != 0 || cdStartDisk != 0)
                    throw new ZipFormatException("Multi-disk ZIP archives are not supported");

                long eocdOffset = searchStart + i;

                if (TryReadZip64DirectoryInfo(eocdOffset, out long zip64CdOffset, out long zip64CdCount))
                {
                    if (zip64CdCount > int.MaxValue)
                        throw new ZipFormatException("ZIP Central Directory entry count is too large");
                    return (zip64CdOffset, (int)zip64CdCount);
                }

                bool requiresZip64 =
                    entriesThisDisk == 0xFFFF ||
                    entriesTotal == 0xFFFF ||
                    cdSize32 == 0xFFFFFFFFu ||
                    cdOffset32 == 0xFFFFFFFFu;

                if (requiresZip64)
                    throw new ZipFormatException("ZIP64 End of Central Directory required but not found");

                return (cdOffset32, entriesTotal);
            }
        }

        throw new ZipFormatException("End of Central Directory record not found");
    }

    private bool TryReadZip64DirectoryInfo(long eocdOffset, out long cdOffset, out long cdCount)
    {
        cdOffset = 0;
        cdCount = 0;

        if (eocdOffset < 20)
            return false;

        _stream.Seek(eocdOffset - 20, SeekOrigin.Begin);
        byte[] locator = new byte[20];
        ReadExact(locator, 0, locator.Length);

        if (ReadUInt32(locator, 0) != Zip64EocdLocatorSignature)
            return false;

        uint zip64EocdDisk = ReadUInt32(locator, 4);
        long zip64EocdOffset = ReadInt64(locator, 8);
        uint totalDisks = ReadUInt32(locator, 16);

        if (zip64EocdDisk != 0 || totalDisks != 1)
            throw new ZipFormatException("Multi-disk ZIP64 archives are not supported");

        _stream.Seek(zip64EocdOffset, SeekOrigin.Begin);

        byte[] zip64Header = new byte[56];
        ReadExact(zip64Header, 0, zip64Header.Length);

        uint sig = ReadUInt32(zip64Header, 0);
        if (sig != Zip64EocdSignature)
            throw new ZipFormatException($"Invalid ZIP64 EOCD signature: 0x{sig:X8}");

        ulong zip64RecordSize = ReadUInt64(zip64Header, 4);
        if (zip64RecordSize < 44)
            throw new ZipFormatException("Invalid ZIP64 EOCD size");

        uint thisDisk = ReadUInt32(zip64Header, 16);
        uint cdStartDisk = ReadUInt32(zip64Header, 20);
        ulong entriesThisDisk = ReadUInt64(zip64Header, 24);
        ulong entriesTotal = ReadUInt64(zip64Header, 32);
        ulong cdOffset64 = ReadUInt64(zip64Header, 48);

        if (thisDisk != 0 || cdStartDisk != 0)
            throw new ZipFormatException("Multi-disk ZIP64 archives are not supported");
        if (entriesThisDisk != entriesTotal)
            throw new ZipFormatException("ZIP64 archive has inconsistent Central Directory entry counts");
        if (entriesTotal > int.MaxValue)
            throw new ZipFormatException("ZIP64 Central Directory entry count is too large");
        if (cdOffset64 > long.MaxValue)
            throw new ZipFormatException("ZIP64 Central Directory offset is too large");

        cdOffset = (long)cdOffset64;
        cdCount = (long)entriesTotal;
        return true;
    }

    private static (long uncompressedSize, long compressedSize, long localHeaderOffset) ParseZip64ExtraField(
        byte[] extra,
        bool needUncompressedSize,
        bool needCompressedSize,
        bool needLocalHeaderOffset,
        long fallbackUncompressedSize,
        long fallbackCompressedSize,
        long fallbackLocalHeaderOffset)
    {
        int pos = 0;
        while (pos + 4 <= extra.Length)
        {
            int headerId = ReadUInt16(extra, pos);
            int dataSize = ReadUInt16(extra, pos + 2);
            pos += 4;

            if (pos + dataSize > extra.Length)
                throw new ZipFormatException("Invalid ZIP extra field length");

            if (headerId == Zip64ExtraFieldId)
            {
                int p = pos;
                int end = pos + dataSize;

                long uncompressedSize = fallbackUncompressedSize;
                long compressedSize = fallbackCompressedSize;
                long localHeaderOffset = fallbackLocalHeaderOffset;

                if (needUncompressedSize)
                {
                    if (p + 8 > end) throw new ZipFormatException("ZIP64 extra field is missing uncompressed size");
                    uncompressedSize = ReadInt64(extra, p);
                    p += 8;
                }

                if (needCompressedSize)
                {
                    if (p + 8 > end) throw new ZipFormatException("ZIP64 extra field is missing compressed size");
                    compressedSize = ReadInt64(extra, p);
                    p += 8;
                }

                if (needLocalHeaderOffset)
                {
                    if (p + 8 > end) throw new ZipFormatException("ZIP64 extra field is missing local header offset");
                    localHeaderOffset = ReadInt64(extra, p);
                    p += 8;
                }

                return (uncompressedSize, compressedSize, localHeaderOffset);
            }

            pos += dataSize;
        }

        throw new ZipFormatException("Required ZIP64 extended information missing in Central Directory entry");
    }

    private long GetEntryDataOffset(long localHeaderOffset)
    {
        _stream.Seek(localHeaderOffset, SeekOrigin.Begin);
        byte[] header = new byte[30];
        ReadExact(header, 0, 30);

        uint sig = ReadUInt32(header, 0);
        if (sig != LocalHeaderSignature)
            throw new ZipFormatException($"Invalid Local Header signature: 0x{sig:X8}");

        int nameLength = ReadUInt16(header, 26);
        int extraLength = ReadUInt16(header, 28);

        return localHeaderOffset + 30 + nameLength + extraLength;
    }

    private void ReadExact(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = _stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
                throw new ZipFormatException("Unexpected end of ZIP file");
            totalRead += read;
        }
    }

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        (uint)(buffer[offset] | buffer[offset + 1] << 8 |
               buffer[offset + 2] << 16 | buffer[offset + 3] << 24);

    private static ulong ReadUInt64(byte[] buffer, int offset)
    {
        uint low = ReadUInt32(buffer, offset);
        uint high = ReadUInt32(buffer, offset + 4);
        return ((ulong)high << 32) | low;
    }

    private static long ReadInt64(byte[] buffer, int offset)
    {
        ulong value = ReadUInt64(buffer, offset);
        if (value > long.MaxValue)
            throw new ZipFormatException("ZIP64 value exceeds supported Int64 range");
        return (long)value;
    }

    private static int ReadUInt16(byte[] buffer, int offset) =>
        buffer[offset] | buffer[offset + 1] << 8;

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsStream)
                _stream.Dispose();
            _disposed = true;
        }
    }
}
