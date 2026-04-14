using System.Collections.Generic;
using System.IO;
using Deflux.Core;
using Deflux.Core.Reader;
using Deflux.Tests.Fixtures;
using Xunit;

namespace Deflux.Tests;

public class CheckpointRoundTripTests
{
    [Fact]
    public void Checkpoint_RoundTrip_ProducesSameOutput()
    {
        string xml = BuildTestXml(100);
        string zipPath = TestFixtures.CreateSimpleZipWithXml("data.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);

            var fullEvents = ReadAllEvents(fs, "data.xml");

            int checkpointAt = fullEvents.Count / 2;
            byte[] checkpoint = null!;
            var partialEvents = new List<XmlEvent>();

            fs.Position = 0;
            using (var reader = new CheckpointableXmlReader(fs, "data.xml"))
            {
                int count = 0;
                while (reader.Read())
                {
                    partialEvents.Add(CaptureEvent(reader));
                    if (++count == checkpointAt)
                    {
                        checkpoint = reader.SaveCheckpoint();
                        break;
                    }
                }
                reader.RestoreCheckpoint(checkpoint);
                while (reader.Read())
                    partialEvents.Add(CaptureEvent(reader));
            }

            Assert.Equal(fullEvents.Count, partialEvents.Count);
            for (int i = 0; i < fullEvents.Count; i++)
            {
                Assert.Equal(fullEvents[i].Kind, partialEvents[i].Kind);
                Assert.Equal(fullEvents[i].LocalName, partialEvents[i].LocalName);
                Assert.Equal(fullEvents[i].Value, partialEvents[i].Value);
                Assert.Equal(fullEvents[i].Depth, partialEvents[i].Depth);
            }
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Checkpoint_RoundTrip_Zip64Archive_ProducesSameOutput()
    {
        string xml = BuildTestXml(100);
        string zipPath = TestFixtures.CreateSimpleZip64WithXml("data.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);

            var fullEvents = ReadAllEvents(fs, "data.xml");

            int checkpointAt = fullEvents.Count / 2;
            byte[] checkpoint = null!;
            var partialEvents = new List<XmlEvent>();

            fs.Position = 0;
            using (var reader = new CheckpointableXmlReader(fs, "data.xml"))
            {
                int count = 0;
                while (reader.Read())
                {
                    partialEvents.Add(CaptureEvent(reader));
                    if (++count == checkpointAt)
                    {
                        checkpoint = reader.SaveCheckpoint();
                        break;
                    }
                }
                reader.RestoreCheckpoint(checkpoint);
                while (reader.Read())
                    partialEvents.Add(CaptureEvent(reader));
            }

            Assert.Equal(fullEvents.Count, partialEvents.Count);
            for (int i = 0; i < fullEvents.Count; i++)
            {
                Assert.Equal(fullEvents[i].Kind, partialEvents[i].Kind);
                Assert.Equal(fullEvents[i].LocalName, partialEvents[i].LocalName);
                Assert.Equal(fullEvents[i].Value, partialEvents[i].Value);
                Assert.Equal(fullEvents[i].Depth, partialEvents[i].Depth);
            }
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Checkpoint_RestoreFromDifferentInstance_Works()
    {
        string xml = BuildTestXml(50);
        string zipPath = TestFixtures.CreateSimpleZipWithXml("payload.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            byte[] checkpoint;

            using (var reader = new CheckpointableXmlReader(fs, "payload.xml"))
            {
                for (int i = 0; i < 10; i++)
                    reader.Read();
                checkpoint = reader.SaveCheckpoint();
            }

            // New reader, same stream
            fs.Position = 0;
            var events = new List<XmlEvent>();
            using (var reader = new CheckpointableXmlReader(fs, "payload.xml"))
            {
                reader.RestoreCheckpoint(checkpoint);
                while (reader.Read())
                    events.Add(CaptureEvent(reader));
            }

            Assert.True(events.Count > 0);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void SkipTo_FindsElement()
    {
        string xml = "<?xml version=\"1.0\"?><root><a/><b/><target id=\"1\"><child/></target><c/></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("test.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "test.xml");
            Assert.True(reader.SkipTo("target"));
            Assert.Equal("target", reader.LocalName);
            Assert.Equal("1", reader.GetAttribute("id"));
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void ReadElements_IteratesSiblings()
    {
        string xml = "<?xml version=\"1.0\"?><root><item v=\"1\"/><item v=\"2\"/><item v=\"3\"/></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("test.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "test.xml");
            reader.SkipTo("root");

            var values = new List<string>();
            foreach (var _ in reader.ReadElements("item"))
                values.Add(reader.GetAttribute("v")!);

            Assert.Equal(3, values.Count);
            Assert.Equal("1", values[0]);
            Assert.Equal("2", values[1]);
            Assert.Equal("3", values[2]);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Entities_ResolvedCorrectly()
    {
        string xml = "<?xml version=\"1.0\"?><root><v>a&amp;b&lt;c&gt;d&quot;e&apos;f&#65;&#x42;</v></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("test.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "test.xml");
            reader.SkipTo("v");
            reader.Read();
            Assert.Equal(XmlNodeKind.Text, reader.NodeKind);
            Assert.Equal("a&b<c>d\"e'fAB", reader.Value);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public void Namespaces_ResolvedCorrectly()
    {
        string xml = "<?xml version=\"1.0\"?><root xmlns=\"http://example.com\" xmlns:x=\"http://x.com\"><x:item x:id=\"1\"/></root>";
        string zipPath = TestFixtures.CreateSimpleZipWithXml("test.xml", xml);

        try
        {
            using var fs = File.OpenRead(zipPath);
            using var reader = new CheckpointableXmlReader(fs, "test.xml");

            reader.Read(); // root
            Assert.Equal("root", reader.LocalName);
            Assert.Equal("http://example.com", reader.NamespaceUri);

            reader.Read(); // x:item
            Assert.Equal("item", reader.LocalName);
            Assert.Equal("x", reader.Prefix);
            Assert.Equal("http://x.com", reader.NamespaceUri);
        }
        finally { File.Delete(zipPath); }
    }

    // ── Helpers ──

    private static string BuildTestXml(int rowCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><data>");
        for (int i = 0; i < rowCount; i++)
            sb.Append($"<row id=\"{i}\"><cell>value_{i}</cell><cell>data_{i * 2}</cell></row>");
        sb.Append("</data>");
        return sb.ToString();
    }

    private static List<XmlEvent> ReadAllEvents(Stream stream, string entryName)
    {
        stream.Position = 0;
        var events = new List<XmlEvent>();
        using var reader = new CheckpointableXmlReader(stream, entryName);
        while (reader.Read())
            events.Add(CaptureEvent(reader));
        return events;
    }

    private static XmlEvent CaptureEvent(CheckpointableXmlReader reader)
    {
        return new XmlEvent
        {
            Kind = reader.NodeKind,
            LocalName = reader.LocalName,
            Prefix = reader.Prefix,
            NamespaceUri = reader.NamespaceUri,
            Value = reader.Value,
            Depth = reader.Depth,
        };
    }

    private struct XmlEvent
    {
        public XmlNodeKind Kind;
        public string LocalName;
        public string? Prefix;
        public string? NamespaceUri;
        public string? Value;
        public int Depth;
    }
}
