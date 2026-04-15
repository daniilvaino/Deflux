using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Deflux.Tests.Fixtures;

/// <summary>
/// Programmatic generation of test ZIP/XLSX/ODS files.
/// No external dependencies — uses System.IO.Compression for creating test data.
/// </summary>
public static class TestFixtures
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "deflux-tests");

    public static string EnsureTempDir()
    {
        Directory.CreateDirectory(TempDir);
        return TempDir;
    }

    public static string CreateSimpleZipWithXml(string entryName, string xmlContent)
        => CreateSimpleZipWithXml(entryName, xmlContent, CompressionLevel.Optimal);

    public static string CreateSimpleZipWithXml(string entryName, string xmlContent, CompressionLevel level)
    {
        var dir = EnsureTempDir();
        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.zip");
        using var fs = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, level);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(xmlContent);
        return path;
    }

    /// <summary>
    /// Creates a ZIP file containing a single XML entry and converts archive metadata to ZIP64.
    /// </summary>
    public static string CreateSimpleZip64WithXml(string entryName, string xmlContent)
    {
        string path = CreateSimpleZipWithXml(entryName, xmlContent);
        ConvertArchiveToZip64(path);
        return path;
    }

    /// <summary>
    /// Creates a ZIP with multiple XML entries.
    /// </summary>
    public static string CreateZipWithEntries(params (string name, string content)[] entries)
    {
        var dir = EnsureTempDir();
        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.zip");
        using var fs = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return path;
    }

    /// <summary>
    /// Converts an existing ZIP archive to ZIP64 metadata format in-place.
    /// Entry data is preserved byte-for-byte.
    /// </summary>
    public static void ConvertArchiveToZip64(string path)
    {
        byte[] src = File.ReadAllBytes(path);
        int eocdOffset = FindEocdOffset(src);

        int cdCount = ReadUInt16(src, eocdOffset + 10);
        uint cdSize32 = ReadUInt32(src, eocdOffset + 12);
        uint cdOffset32 = ReadUInt32(src, eocdOffset + 16);
        int commentLength = ReadUInt16(src, eocdOffset + 20);

        if (eocdOffset + 22 + commentLength != src.Length)
            throw new InvalidDataException("Unsupported ZIP layout for ZIP64 conversion");

        if (cdOffset32 > int.MaxValue || cdSize32 > int.MaxValue)
            throw new InvalidDataException("Central Directory bounds are too large");

        int cdOffset = (int)cdOffset32;
        int cdSize = (int)cdSize32;
        int cdEnd = cdOffset + cdSize;
        if (cdOffset < 0 || cdEnd > src.Length)
            throw new InvalidDataException("Invalid Central Directory bounds");

        using var rebuiltCd = new MemoryStream(cdSize + cdCount * 32);
        int pos = cdOffset;
        for (int i = 0; i < cdCount; i++)
        {
            if (pos + 46 > cdEnd)
                throw new InvalidDataException("Truncated Central Directory entry");
            if (ReadUInt32(src, pos) != 0x02014b50)
                throw new InvalidDataException("Invalid Central Directory entry signature");

            var header = new byte[46];
            Array.Copy(src, pos, header, 0, 46);

            uint compressedSize = ReadUInt32(header, 20);
            uint uncompressedSize = ReadUInt32(header, 24);
            uint localHeaderOffset = ReadUInt32(header, 42);

            int nameLength = ReadUInt16(header, 28);
            int extraLength = ReadUInt16(header, 30);
            int entryCommentLength = ReadUInt16(header, 32);
            int entryLength = 46 + nameLength + extraLength + entryCommentLength;
            if (pos + entryLength > cdEnd)
                throw new InvalidDataException("Corrupt Central Directory entry length");

            var name = new byte[nameLength];
            if (nameLength > 0)
                Array.Copy(src, pos + 46, name, 0, nameLength);

            var oldExtra = new byte[extraLength];
            if (extraLength > 0)
                Array.Copy(src, pos + 46 + nameLength, oldExtra, 0, extraLength);

            var comment = new byte[entryCommentLength];
            if (entryCommentLength > 0)
                Array.Copy(src, pos + 46 + nameLength + extraLength, comment, 0, entryCommentLength);

            var zip64Extra = new byte[28];
            WriteUInt16(zip64Extra, 0, 0x0001);
            WriteUInt16(zip64Extra, 2, 24);
            WriteUInt64(zip64Extra, 4, uncompressedSize);
            WriteUInt64(zip64Extra, 12, compressedSize);
            WriteUInt64(zip64Extra, 20, localHeaderOffset);

            int newExtraLength = zip64Extra.Length + oldExtra.Length;
            if (newExtraLength > ushort.MaxValue)
                throw new InvalidDataException("ZIP64 extra field too large");

            WriteUInt32(header, 20, 0xFFFFFFFFu);
            WriteUInt32(header, 24, 0xFFFFFFFFu);
            WriteUInt32(header, 42, 0xFFFFFFFFu);
            WriteUInt16(header, 30, (ushort)newExtraLength);

            rebuiltCd.Write(header, 0, header.Length);
            if (name.Length > 0) rebuiltCd.Write(name, 0, name.Length);
            rebuiltCd.Write(zip64Extra, 0, zip64Extra.Length);
            if (oldExtra.Length > 0) rebuiltCd.Write(oldExtra, 0, oldExtra.Length);
            if (comment.Length > 0) rebuiltCd.Write(comment, 0, comment.Length);

            pos += entryLength;
        }

        if (pos != cdEnd)
            throw new InvalidDataException("Central Directory parse ended at unexpected position");

        long newCdOffset = cdOffset;
        long newCdSize = rebuiltCd.Length;

        using var output = new MemoryStream(src.Length + (int)newCdSize + 128);
        output.Write(src, 0, cdOffset);
        rebuiltCd.Position = 0;
        rebuiltCd.CopyTo(output);

        long zip64EocdOffset = output.Position;
        WriteUInt32(output, 0x06064b50);
        WriteUInt64(output, 44);
        WriteUInt16(output, 45);
        WriteUInt16(output, 45);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        WriteUInt64(output, (ulong)cdCount);
        WriteUInt64(output, (ulong)cdCount);
        WriteUInt64(output, (ulong)newCdSize);
        WriteUInt64(output, (ulong)newCdOffset);

        WriteUInt32(output, 0x07064b50);
        WriteUInt32(output, 0);
        WriteUInt64(output, (ulong)zip64EocdOffset);
        WriteUInt32(output, 1);

        WriteUInt32(output, 0x06054b50);
        WriteUInt16(output, 0);
        WriteUInt16(output, 0);
        WriteUInt16(output, 0xFFFF);
        WriteUInt16(output, 0xFFFF);
        WriteUInt32(output, 0xFFFFFFFFu);
        WriteUInt32(output, 0xFFFFFFFFu);
        WriteUInt16(output, 0);

        File.WriteAllBytes(path, output.ToArray());
    }

    /// <summary>
    /// Creates a minimal valid XLSX file with given sheet data.
    /// Each sheet is (sheetName, rows), where each row is a string[] of cell values.
    /// </summary>
    public static string CreateXlsx(params (string sheetName, string[][] rows)[] sheets)
    {
        var dir = EnsureTempDir();
        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.xlsx");
        using var fs = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        // Collect all unique strings for shared strings table
        var sst = new System.Collections.Generic.List<string>();
        var sstIndex = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var (_, rows) in sheets)
        {
            foreach (var row in rows)
            {
                foreach (var cell in row)
                {
                    if (cell != null && !sstIndex.ContainsKey(cell))
                    {
                        sstIndex[cell] = sst.Count;
                        sst.Add(cell);
                    }
                }
            }
        }

        // [Content_Types].xml
        WriteEntry(archive, "[Content_Types].xml", BuildContentTypes(sheets.Length));

        // _rels/.rels
        WriteEntry(archive, "_rels/.rels", BuildRootRels());

        // xl/workbook.xml
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbook(sheets));

        // xl/_rels/workbook.xml.rels
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(sheets.Length));

        // xl/sharedStrings.xml
        WriteEntry(archive, "xl/sharedStrings.xml", BuildSharedStrings(sst));

        // xl/worksheets/sheet{i}.xml
        for (int i = 0; i < sheets.Length; i++)
        {
            WriteEntry(archive, $"xl/worksheets/sheet{i + 1}.xml",
                BuildSheet(sheets[i].rows, sstIndex));
        }

        return path;
    }

    /// <summary>
    /// Creates a minimal valid ODS file with given sheet data.
    /// </summary>
    public static string CreateOds(params (string sheetName, string[][] rows)[] sheets)
    {
        var dir = EnsureTempDir();
        var path = Path.Combine(dir, $"test-{Guid.NewGuid():N}.ods");
        using var fs = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        // mimetype (stored, must be first)
        WriteEntry(archive, "mimetype", "application/vnd.oasis.opendocument.spreadsheet",
            CompressionLevel.NoCompression);

        // META-INF/manifest.xml
        WriteEntry(archive, "META-INF/manifest.xml", BuildOdsManifest());

        // content.xml
        WriteEntry(archive, "content.xml", BuildOdsContent(sheets));

        return path;
    }

    public static void Cleanup()
    {
        if (Directory.Exists(TempDir))
            Directory.Delete(TempDir, true);
    }

    // ── XLSX builders ──

    private static string BuildContentTypes(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
        for (int i = 1; i <= sheetCount; i++)
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string BuildRootRels()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
               "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
               "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
               "</Relationships>";
    }

    private static string BuildWorkbook((string sheetName, string[][] rows)[] sheets)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sb.Append("<sheets>");
        for (int i = 0; i < sheets.Length; i++)
            sb.Append($"<sheet name=\"{EscapeXml(sheets[i].sheetName)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private static string BuildWorkbookRels(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 0; i < sheetCount; i++)
            sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string BuildSharedStrings(System.Collections.Generic.List<string> sst)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append($"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{sst.Count}\" uniqueCount=\"{sst.Count}\">");
        foreach (var s in sst)
            sb.Append($"<si><t>{EscapeXml(s)}</t></si>");
        sb.Append("</sst>");
        return sb.ToString();
    }

    private static string BuildSheet(string[][] rows, System.Collections.Generic.Dictionary<string, int> sstIndex)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetData>");
        for (int r = 0; r < rows.Length; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Length; c++)
            {
                string cellRef = GetCellRef(c, r + 1);
                string val = rows[r][c];
                if (val == null)
                    continue;

                // Try parse as number
                if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    sb.Append($"<c r=\"{cellRef}\"><v>{val}</v></c>");
                }
                else
                {
                    int idx = sstIndex[val];
                    sb.Append($"<c r=\"{cellRef}\" t=\"s\"><v>{idx}</v></c>");
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    // ── ODS builders ──

    private static string BuildOdsManifest()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
               "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">" +
               "<manifest:file-entry manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\" manifest:full-path=\"/\"/>" +
               "<manifest:file-entry manifest:media-type=\"text/xml\" manifest:full-path=\"content.xml\"/>" +
               "</manifest:manifest>";
    }

    private static string BuildOdsContent((string sheetName, string[][] rows)[] sheets)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<office:document-content");
        sb.Append(" xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"");
        sb.Append(" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"");
        sb.Append(" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"");
        sb.Append(" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"");
        sb.Append(" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"");
        sb.Append(">");
        sb.Append("<office:body><office:spreadsheet>");

        foreach (var (sheetName, rows) in sheets)
        {
            sb.Append($"<table:table table:name=\"{EscapeXml(sheetName)}\">");
            foreach (var row in rows)
            {
                sb.Append("<table:table-row>");
                foreach (var cell in row)
                {
                    if (cell == null)
                    {
                        sb.Append("<table:table-cell/>");
                        continue;
                    }

                    if (double.TryParse(cell, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double numVal))
                    {
                        sb.Append($"<table:table-cell office:value-type=\"float\" office:value=\"{cell}\">");
                        sb.Append($"<text:p>{EscapeXml(cell)}</text:p>");
                        sb.Append("</table:table-cell>");
                    }
                    else
                    {
                        sb.Append("<table:table-cell office:value-type=\"string\">");
                        sb.Append($"<text:p>{EscapeXml(cell)}</text:p>");
                        sb.Append("</table:table-cell>");
                    }
                }
                sb.Append("</table:table-row>");
            }
            sb.Append("</table:table>");
        }

        sb.Append("</office:spreadsheet></office:body></office:document-content>");
        return sb.ToString();
    }

    // ── Helpers ──

    private static string GetCellRef(int col, int row)
    {
        string colStr = "";
        int c = col;
        do
        {
            colStr = (char)('A' + c % 26) + colStr;
            c = c / 26 - 1;
        } while (c >= 0);
        return colStr + row;
    }

    private static string EscapeXml(string s)
    {
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(name, level);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static int FindEocdOffset(byte[] data)
    {
        for (int i = data.Length - 22; i >= 0; i--)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4b && data[i + 2] == 0x05 && data[i + 3] == 0x06)
                return i;
        }
        throw new InvalidDataException("EOCD not found");
    }

    private static int ReadUInt16(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8);

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt64(byte[] data, int offset, ulong value)
    {
        WriteUInt32(data, offset, (uint)(value & 0xFFFFFFFFu));
        WriteUInt32(data, offset + 4, (uint)(value >> 32));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        WriteUInt32(stream, (uint)(value & 0xFFFFFFFFu));
        WriteUInt32(stream, (uint)(value >> 32));
    }
}
