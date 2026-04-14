using System.IO;
using System.Text;
using Deflux.Core;
using Deflux.Core.Reader;
using Deflux.Tests.Fixtures;
using Xunit;

namespace Deflux.Tests;

/// <summary>
/// Tests that XML parsing works correctly when tags are split across
/// DEFLATE chunk boundaries. The parser uses 8KB inflate buffers,
/// so we generate XML large enough to guarantee multi-chunk decompression
/// and verify every element is parsed.
/// </summary>
public class ChunkBoundaryTests
{
    /// <summary>
    /// XML with many elements that exceeds 8KB when compressed+decompressed,
    /// forcing tags to land on chunk boundaries.
    /// </summary>
    [Theory]
    [InlineData(500)]    // ~50KB XML — moderate
    [InlineData(5000)]   // ~500KB XML — guarantees many chunk splits
    public void LargeXml_AllElements_Parsed(int rowCount)
    {
        string xml = BuildLargeXml(rowCount);
        string zipPath = TestFixtures.CreateSimpleZipWithXml("data.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "data.xml");

            int elements = 0, endElements = 0, texts = 0;
            while (reader.Read())
            {
                switch (reader.NodeKind)
                {
                    case XmlNodeKind.Element: elements++; break;
                    case XmlNodeKind.EndElement: endElements++; break;
                    case XmlNodeKind.Text: texts++; break;
                }
            }

            // root + N*(row + 3*cell) = 1 + 4N elements
            int expectedElements = 1 + 4 * rowCount;
            Assert.Equal(expectedElements, elements);
            Assert.Equal(expectedElements, endElements);
            Assert.Equal(3 * rowCount, texts); // 3 text nodes per row
        }
        finally { File.Delete(zipPath); }
    }

    /// <summary>
    /// Long attribute values that push tag boundaries across chunks.
    /// </summary>
    [Fact]
    public void LongAttributes_ParsedCorrectly()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><root>");
        for (int i = 0; i < 200; i++)
        {
            // Attribute value ~200 chars — tag total ~250 chars
            string longVal = new string((char)('A' + (i % 26)), 200);
            sb.Append($"<item id=\"{i}\" data=\"{longVal}\" extra=\"{longVal}\"/>");
        }
        sb.Append("</root>");

        string zipPath = TestFixtures.CreateSimpleZipWithXml("attrs.xml", sb.ToString());

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "attrs.xml");

            reader.SkipTo("root");
            int count = 0;
            foreach (var _ in reader.ReadElements("item"))
            {
                Assert.Equal(count.ToString(), reader.GetAttribute("id"));
                Assert.Equal(200, reader.GetAttribute("data")!.Length);
                count++;
            }
            Assert.Equal(200, count);
        }
        finally { File.Delete(zipPath); }
    }

    /// <summary>
    /// Deep nesting forces many open elements on the stack,
    /// and EndElements may split across chunks.
    /// </summary>
    [Fact]
    public void DeepNesting_DepthTrackedCorrectly()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        int depth = 100;
        for (int i = 0; i < depth; i++)
            sb.Append($"<level{i}>");
        sb.Append("<leaf>deep</leaf>");
        for (int i = depth - 1; i >= 0; i--)
            sb.Append($"</level{i}>");

        // Pad with many siblings at root to ensure large file
        var root = $"<root>{sb}</root>";
        var full = new StringBuilder();
        full.Append("<?xml version=\"1.0\"?><doc>");
        for (int i = 0; i < 50; i++)
            full.Append(root);
        full.Append("</doc>");

        string zipPath = TestFixtures.CreateSimpleZipWithXml("deep.xml", full.ToString());

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "deep.xml");

            int maxDepth = 0;
            int leafCount = 0;
            while (reader.Read())
            {
                if (reader.Depth > maxDepth)
                    maxDepth = reader.Depth;
                if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "leaf")
                    leafCount++;
            }

            Assert.Equal(50, leafCount);
            Assert.True(maxDepth >= depth, $"Max depth {maxDepth} should be >= {depth}");
        }
        finally { File.Delete(zipPath); }
    }

    /// <summary>
    /// Namespaced elements with long namespace URIs — realistic ODS-like scenario.
    /// </summary>
    [Fact]
    public void NamespacedElements_LargeFile()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>");
        sb.Append("<doc xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"");
        sb.Append(" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"");
        sb.Append(" xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\">");

        for (int i = 0; i < 1000; i++)
        {
            sb.Append($"<table:row table:index=\"{i}\">");
            sb.Append($"<table:cell office:value=\"{i}\"><text:p>val_{i}</text:p></table:cell>");
            sb.Append("</table:row>");
        }
        sb.Append("</doc>");

        string zipPath = TestFixtures.CreateSimpleZipWithXml("ns.xml", sb.ToString());

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "ns.xml");

            reader.SkipTo("doc");
            int rowCount = 0;
            foreach (var _ in reader.ReadElements("row"))
            {
                Assert.Equal("urn:oasis:names:tc:opendocument:xmlns:table:1.0", reader.NamespaceUri);
                Assert.Equal(rowCount.ToString(), reader.GetAttribute("index"));
                rowCount++;
            }
            Assert.Equal(1000, rowCount);
        }
        finally { File.Delete(zipPath); }
    }

    /// <summary>
    /// Checkpoint mid-stream on a large file — the classic use case.
    /// Verifies the invariant across chunk boundaries.
    /// </summary>
    [Fact]
    public void Checkpoint_LargeFile_RoundTrip()
    {
        string xml = BuildLargeXml(2000);
        string zipPath = TestFixtures.CreateSimpleZipWithXml("big.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);

            // Full read
            var fullValues = new System.Collections.Generic.List<string>();
            using (var reader = new CheckpointableXmlReader(fs, "big.xml"))
            {
                while (reader.Read())
                {
                    if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "cell")
                    {
                        reader.Read(); // text
                        fullValues.Add(reader.Value ?? "");
                    }
                }
            }

            // Partial + checkpoint + restore
            fs.Position = 0;
            byte[] cp = null!;
            var partialValues = new System.Collections.Generic.List<string>();
            using (var reader = new CheckpointableXmlReader(fs, "big.xml"))
            {
                int cellCount = 0;
                while (reader.Read())
                {
                    if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "cell")
                    {
                        reader.Read();
                        partialValues.Add(reader.Value ?? "");
                        cellCount++;
                        if (cellCount == fullValues.Count / 2)
                        {
                            cp = reader.SaveCheckpoint();
                            break;
                        }
                    }
                }

                reader.RestoreCheckpoint(cp);
                while (reader.Read())
                {
                    if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "cell")
                    {
                        reader.Read();
                        partialValues.Add(reader.Value ?? "");
                    }
                }
            }

            Assert.Equal(fullValues.Count, partialValues.Count);
            for (int i = 0; i < fullValues.Count; i++)
                Assert.Equal(fullValues[i], partialValues[i]);
        }
        finally { File.Delete(zipPath); }
    }

    /// <summary>
    /// Entities and CDATA split across chunks.
    /// </summary>
    [Fact]
    public void EntitiesAndCData_AcrossChunks()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><root>");
        for (int i = 0; i < 500; i++)
        {
            sb.Append($"<item>text &amp; more &lt;stuff&gt; &#65;&#x42; end</item>");
            sb.Append($"<cdata><![CDATA[raw <data> & more]]></cdata>");
        }
        sb.Append("</root>");

        string zipPath = TestFixtures.CreateSimpleZipWithXml("ent.xml", sb.ToString());

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "ent.xml");

            reader.SkipTo("root");
            int items = 0, cdatas = 0;
            while (reader.Read())
            {
                if (reader.NodeKind == XmlNodeKind.EndElement && reader.LocalName == "root")
                    break;

                if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "item")
                {
                    reader.Read(); // text
                    Assert.Equal("text & more <stuff> AB end", reader.Value);
                    items++;
                }
                else if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "cdata")
                {
                    reader.Read(); // CDATA
                    Assert.Equal(XmlNodeKind.CData, reader.NodeKind);
                    Assert.Equal("raw <data> & more", reader.Value);
                    cdatas++;
                }
            }

            Assert.Equal(500, items);
            Assert.Equal(500, cdatas);
        }
        finally { File.Delete(zipPath); }
    }

    // ── Helper ──

    private static string BuildLargeXml(int rowCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><data>");
        for (int i = 0; i < rowCount; i++)
        {
            sb.Append($"<row id=\"{i}\">");
            sb.Append($"<cell>name_{i}</cell>");
            sb.Append($"<cell>value_{i * 3}</cell>");
            sb.Append($"<cell>data_{i}_{new string('x', 50)}</cell>");
            sb.Append("</row>");
        }
        sb.Append("</data>");
        return sb.ToString();
    }
}
