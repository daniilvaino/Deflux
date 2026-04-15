using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Deflux.Core;
using Deflux.Core.Exceptions;
using Deflux.Core.Reader;
using Deflux.Ods;
using Deflux.Xlsx;
using Deflux.Tests.Fixtures;
using Xunit;

namespace Deflux.Tests;

public class EdgeCaseTests
{
    // T1: Checkpoint after self-closing element (<foo/>)
    [Fact]
    public void Checkpoint_AfterSelfClosingElement_RestoresCorrectly()
    {
        string xml = "<?xml version=\"1.0\"?><root><a/><b/><target id=\"1\"/><c>text</c></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("test.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);

            var fullEvents = ReadAllEvents(fs, "test.xml");

            fs.Position = 0;
            byte[] checkpoint = null!;
            var partialEvents = new List<XmlEvent>();

            using (var reader = new CheckpointableXmlReader(fs, "test.xml"))
            {
                while (reader.Read())
                {
                    partialEvents.Add(Capture(reader));
                    if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "target")
                    {
                        checkpoint = reader.SaveCheckpoint();
                        break;
                    }
                }
                reader.RestoreCheckpoint(checkpoint);
                while (reader.Read())
                    partialEvents.Add(Capture(reader));
            }

            Assert.Equal(fullEvents.Count, partialEvents.Count);
            for (int i = 0; i < fullEvents.Count; i++)
            {
                Assert.Equal(fullEvents[i].Kind, partialEvents[i].Kind);
                Assert.Equal(fullEvents[i].LocalName, partialEvents[i].LocalName);
                Assert.Equal(fullEvents[i].Depth, partialEvents[i].Depth);
            }
        }
        finally { File.Delete(zipPath); }
    }

    // T2: Malformed XML with mismatched tags
    [Fact]
    public void MismatchedEndTag_ThrowsXmlParseException()
    {
        string xml = "<?xml version=\"1.0\"?><root><a></b></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("bad.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "bad.xml");

            Assert.Throws<XmlParseException>(() =>
            {
                while (reader.Read()) { }
            });
        }
        finally { File.Delete(zipPath); }
    }

    // T3: ODS row index after checkpoint restore
    [Fact]
    public void OdsCheckpoint_PreservesRowIndex()
    {
        var rows = new string[30][];
        for (int i = 0; i < 30; i++)
            rows[i] = new[] { $"Item_{i}", $"{i}" };

        var path = TestFixtures.CreateOds(("Data", rows));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new OdsReader(fs);
            reader.OpenSheet(0);

            for (int i = 0; i < 15; i++)
                Assert.True(reader.ReadRow(out _));

            byte[] cp = reader.SaveCheckpoint();
            reader.RestoreCheckpoint(cp);

            Assert.True(reader.ReadRow(out Row row));
            Assert.Equal(15, row.RowIndex);
        }
        finally { File.Delete(path); }
    }

    // T4: ObjectDisposedException after Dispose
    [Fact]
    public void CheckpointableXmlReader_ThrowsAfterDispose()
    {
        string xml = "<?xml version=\"1.0\"?><root><a/></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("test.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            var reader = new CheckpointableXmlReader(fs, "test.xml");
            reader.Read();
            reader.Dispose();

            Assert.Throws<ObjectDisposedException>(() => reader.Read());
            Assert.Throws<ObjectDisposedException>(() => reader.SaveCheckpoint());
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void XlsxReader_ThrowsAfterDispose()
    {
        var path = TestFixtures.CreateXlsx(("Sheet1", new[] { new[] { "a" } }));

        try
        {
            using var fs = File.OpenRead(path);
            var reader = new XlsxReader(fs);
            reader.Dispose();

            Assert.Throws<ObjectDisposedException>(() => reader.OpenSheet(0));
            Assert.Throws<ObjectDisposedException>(() => reader.ReadRow(out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OdsReader_ThrowsAfterDispose()
    {
        var path = TestFixtures.CreateOds(("Sheet1", new[] { new[] { "a" } }));

        try
        {
            using var fs = File.OpenRead(path);
            var reader = new OdsReader(fs);
            reader.Dispose();

            Assert.Throws<ObjectDisposedException>(() => reader.ScanSheets());
            Assert.Throws<ObjectDisposedException>(() => reader.ReadRow(out _));
        }
        finally { File.Delete(path); }
    }

    // T5: Corrupted/truncated checkpoint blob
    [Fact]
    public void CorruptedCheckpoint_ThrowsOnRestore()
    {
        string xml = "<?xml version=\"1.0\"?><root><row id=\"1\"><cell>v</cell></row></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("test.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            byte[] validCp;
            using (var reader = new CheckpointableXmlReader(fs, "test.xml"))
            {
                reader.Read();
                validCp = reader.SaveCheckpoint();
            }

            // Truncated
            byte[] truncated = new byte[validCp.Length / 2];
            Array.Copy(validCp, truncated, truncated.Length);
            fs.Position = 0;
            using (var reader = new CheckpointableXmlReader(fs, "test.xml"))
            {
                Assert.ThrowsAny<Exception>(() => reader.RestoreCheckpoint(truncated));
            }

            // Wrong version
            byte[] wrongVer = (byte[])validCp.Clone();
            wrongVer[0] = 0xFF;
            fs.Position = 0;
            using (var reader = new CheckpointableXmlReader(fs, "test.xml"))
            {
                Assert.Throws<CheckpointVersionException>(() => reader.RestoreCheckpoint(wrongVer));
            }

            // Corrupted payload (flip bytes after header)
            byte[] corrupted = (byte[])validCp.Clone();
            for (int i = 9; i < Math.Min(corrupted.Length, 20); i++)
                corrupted[i] ^= 0xFF;
            fs.Position = 0;
            using (var reader = new CheckpointableXmlReader(fs, "test.xml"))
            {
                Assert.Throws<CheckpointMismatchException>(() => reader.RestoreCheckpoint(corrupted));
            }

            // Empty
            fs.Position = 0;
            using (var reader = new CheckpointableXmlReader(fs, "test.xml"))
            {
                Assert.ThrowsAny<Exception>(() => reader.RestoreCheckpoint(Array.Empty<byte>()));
            }
        }
        finally { File.Delete(zipPath); }
    }

    // T6: Multiple checkpoint save/restore cycles
    [Fact]
    public void MultipleCheckpointCycles_ProducesSameOutput()
    {
        string xml = BuildXml(200);
        string zipPath = TestFixtures.CreateSimpleZipWithXml("data.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            var fullEvents = ReadAllEvents(fs, "data.xml");

            fs.Position = 0;
            var cycleEvents = new List<XmlEvent>();
            int[] checkpointAtEvents = { 50, 150, 300, 500, 700 };
            int eventIdx = 0;

            using (var reader = new CheckpointableXmlReader(fs, "data.xml"))
            {
                int nextCpIdx = 0;
                while (reader.Read())
                {
                    cycleEvents.Add(Capture(reader));
                    eventIdx++;

                    if (nextCpIdx < checkpointAtEvents.Length && eventIdx == checkpointAtEvents[nextCpIdx])
                    {
                        byte[] cp = reader.SaveCheckpoint();
                        reader.RestoreCheckpoint(cp);
                        nextCpIdx++;
                    }
                }
            }

            Assert.Equal(fullEvents.Count, cycleEvents.Count);
            for (int i = 0; i < fullEvents.Count; i++)
            {
                Assert.Equal(fullEvents[i].Kind, cycleEvents[i].Kind);
                Assert.Equal(fullEvents[i].LocalName, cycleEvents[i].LocalName);
            }
        }
        finally { File.Delete(zipPath); }
    }

    // T7: Stored (uncompressed) ZIP entries
    [Fact]
    public void StoredZipEntry_ReadAndCheckpoint()
    {
        string xml = "<?xml version=\"1.0\"?><root><a id=\"1\"/><b id=\"2\"/><c id=\"3\">text</c></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("stored.xml", xml, CompressionLevel.NoCompression);

        try
        {
            using var fs = File.OpenRead(zipPath);
            var fullEvents = ReadAllEvents(fs, "stored.xml");
            Assert.True(fullEvents.Count > 0);

            fs.Position = 0;
            var partialEvents = new List<XmlEvent>();
            using (var reader = new CheckpointableXmlReader(fs, "stored.xml"))
            {
                reader.Read();
                partialEvents.Add(Capture(reader));
                reader.Read();
                partialEvents.Add(Capture(reader));

                byte[] cp = reader.SaveCheckpoint();
                reader.RestoreCheckpoint(cp);
                while (reader.Read())
                    partialEvents.Add(Capture(reader));
            }

            Assert.Equal(fullEvents.Count, partialEvents.Count);
            for (int i = 0; i < fullEvents.Count; i++)
            {
                Assert.Equal(fullEvents[i].Kind, partialEvents[i].Kind);
                Assert.Equal(fullEvents[i].LocalName, partialEvents[i].LocalName);
            }
        }
        finally { File.Delete(zipPath); }
    }

    // T8: Empty shared strings table
    [Fact]
    public void Xlsx_EmptySharedStrings_NumericOnly()
    {
        var path = TestFixtures.CreateXlsx(("Numbers", new[] {
            new[] { "100", "200", "3.14" },
            new[] { "42", "0", "99.9" },
        }));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new XlsxReader(fs);
            reader.OpenSheet(0);

            Assert.True(reader.ReadRow(out Row r1));
            Assert.Equal(3, r1.Cells.Length);
            Assert.Equal("100", r1.Cells.Span[0].Value);
            Assert.Equal(CellType.Number, r1.Cells.Span[0].Type);
            Assert.Equal("3.14", r1.Cells.Span[2].Value);

            Assert.True(reader.ReadRow(out Row r2));
            Assert.Equal("42", r2.Cells.Span[0].Value);

            Assert.False(reader.ReadRow(out _));
        }
        finally { File.Delete(path); }
    }

    // T9: Very deep nesting checkpoint
    [Fact]
    public void Checkpoint_DeepNesting_150Levels()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        int depth = 150;
        for (int i = 0; i < depth; i++)
            sb.Append($"<l{i}>");
        sb.Append("<leaf>deep</leaf>");
        for (int i = depth - 1; i >= 0; i--)
            sb.Append($"</l{i}>");

        string zipPath = TestFixtures.CreateSimpleZipWithXml("deep.xml", sb.ToString());

        try
        {
            using var fs = File.OpenRead(zipPath);
            var fullEvents = ReadAllEvents(fs, "deep.xml");

            fs.Position = 0;
            byte[] cp = null!;
            var partialEvents = new List<XmlEvent>();
            using (var reader = new CheckpointableXmlReader(fs, "deep.xml"))
            {
                int count = 0;
                while (reader.Read())
                {
                    partialEvents.Add(Capture(reader));
                    count++;
                    if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "leaf")
                    {
                        Assert.True(reader.Depth >= depth);
                        cp = reader.SaveCheckpoint();
                        break;
                    }
                }
                reader.RestoreCheckpoint(cp);
                while (reader.Read())
                    partialEvents.Add(Capture(reader));
            }

            Assert.Equal(fullEvents.Count, partialEvents.Count);
            for (int i = 0; i < fullEvents.Count; i++)
            {
                Assert.Equal(fullEvents[i].Kind, partialEvents[i].Kind);
                Assert.Equal(fullEvents[i].LocalName, partialEvents[i].LocalName);
                Assert.Equal(fullEvents[i].Depth, partialEvents[i].Depth);
            }
        }
        finally { File.Delete(zipPath); }
    }

    // ── Helpers ──

    private static string BuildXml(int rowCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><data>");
        for (int i = 0; i < rowCount; i++)
            sb.Append($"<row id=\"{i}\"><cell>value_{i}</cell></row>");
        sb.Append("</data>");
        return sb.ToString();
    }

    private static List<XmlEvent> ReadAllEvents(Stream stream, string entryName)
    {
        stream.Position = 0;
        var events = new List<XmlEvent>();
        using var reader = new CheckpointableXmlReader(stream, entryName);
        while (reader.Read())
            events.Add(Capture(reader));
        return events;
    }

    private static XmlEvent Capture(CheckpointableXmlReader reader)
    {
        return new XmlEvent
        {
            Kind = reader.NodeKind,
            LocalName = reader.LocalName,
            Value = reader.Value,
            Depth = reader.Depth,
        };
    }

    private struct XmlEvent
    {
        public XmlNodeKind Kind;
        public string LocalName;
        public string? Value;
        public int Depth;
    }
}
