using System.Collections.Generic;
using System.IO;
using Deflux.Core;
using Deflux.Ods;
using Deflux.Tests.Fixtures;
using Xunit;

namespace Deflux.Tests;

public class OdsReaderTests
{
    [Fact]
    public void ReadSheet_BasicData()
    {
        var path = TestFixtures.CreateOds(
            ("Sheet1", new[] {
                new[] { "Name", "Age", "City" },
                new[] { "Alice", "30", "NYC" },
                new[] { "Bob", "25", "LA" },
            }));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new OdsReader(fs);
            var names = reader.GetSheetNames();
            Assert.Single(names);
            Assert.Equal("Sheet1", names[0]);

            reader.OpenSheet(0);

            Assert.True(reader.ReadRow(out Row r1));
            Assert.Equal(3, r1.Cells.Length);
            Assert.Equal("Name", r1.Cells.Span[0].Value);
            Assert.Equal(CellType.String, r1.Cells.Span[0].Type);

            Assert.True(reader.ReadRow(out Row r2));
            Assert.Equal("Alice", r2.Cells.Span[0].Value);
            Assert.Equal("30", r2.Cells.Span[1].Value);
            Assert.Equal(CellType.Number, r2.Cells.Span[1].Type);

            Assert.True(reader.ReadRow(out Row r3));
            Assert.Equal("Bob", r3.Cells.Span[0].Value);

            Assert.False(reader.ReadRow(out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MultipleSheets()
    {
        var path = TestFixtures.CreateOds(
            ("Data", new[] { new[] { "a", "b" } }),
            ("Summary", new[] { new[] { "x", "y" } }));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new OdsReader(fs);
            Assert.Equal(2, reader.GetSheetNames().Count);

            reader.OpenSheet("Summary");
            Assert.True(reader.ReadRow(out Row row));
            Assert.Equal("x", row.Cells.Span[0].Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScanSheets_ReturnsCheckpoints()
    {
        var path = TestFixtures.CreateOds(
            ("Alpha", new[] { new[] { "a1" } }),
            ("Beta", new[] { new[] { "b1" } }),
            ("Gamma", new[] { new[] { "g1" } }));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new OdsReader(fs);

            var sheets = reader.ScanSheets();
            Assert.Equal(3, sheets.Count);
            Assert.Equal("Alpha", sheets[0].Name);
            Assert.Equal("Beta", sheets[1].Name);
            Assert.Equal("Gamma", sheets[2].Name);

            // Each checkpoint is a valid blob
            foreach (var s in sheets)
                Assert.True(s.Checkpoint.Length > 0);

            // Use checkpoint to open third sheet directly
            reader.OpenSheet(2);
            Assert.True(reader.ReadRow(out Row row));
            Assert.Equal("g1", row.Cells.Span[0].Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NumericValues()
    {
        var path = TestFixtures.CreateOds(
            ("Sheet1", new[] { new[] { "42.5", "100" } }));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new OdsReader(fs);
            reader.OpenSheet(0);

            Assert.True(reader.ReadRow(out Row r));
            Assert.Equal("42.5", r.Cells.Span[0].Value);
            Assert.Equal(CellType.Number, r.Cells.Span[0].Type);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Checkpoint_OdsRoundTrip()
    {
        var rows = new string[30][];
        for (int i = 0; i < 30; i++)
            rows[i] = new[] { $"Item_{i}", $"{i}" };

        var path = TestFixtures.CreateOds(("Data", rows));

        try
        {
            using var fs = File.OpenRead(path);

            var allRows = new List<string>();
            using (var reader = new OdsReader(fs))
            {
                reader.OpenSheet(0);
                while (reader.ReadRow(out Row r))
                    allRows.Add(r.Cells.Span[0].Value!);
            }

            byte[] cp;
            var partialRows = new List<string>();
            fs.Position = 0;
            using (var reader = new OdsReader(fs))
            {
                reader.OpenSheet(0);
                for (int i = 0; i < 10; i++)
                {
                    reader.ReadRow(out Row r);
                    partialRows.Add(r.Cells.Span[0].Value!);
                }
                cp = reader.SaveCheckpoint();
                reader.RestoreCheckpoint(cp);
                while (reader.ReadRow(out Row r))
                    partialRows.Add(r.Cells.Span[0].Value!);
            }

            Assert.Equal(allRows.Count, partialRows.Count);
            for (int i = 0; i < allRows.Count; i++)
                Assert.Equal(allRows[i], partialRows[i]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadSheet_Zip64Container()
    {
        var path = TestFixtures.CreateOds(
            ("Sheet1", new[] {
                new[] { "Name", "Age" },
                new[] { "Alice", "30" },
            }));

        try
        {
            TestFixtures.ConvertArchiveToZip64(path);

            using var fs = File.OpenRead(path);
            using var reader = new OdsReader(fs);
            reader.OpenSheet(0);

            Assert.True(reader.ReadRow(out Row header));
            Assert.Equal("Name", header.Cells.Span[0].Value);
            Assert.Equal("Age", header.Cells.Span[1].Value);

            Assert.True(reader.ReadRow(out Row row));
            Assert.Equal("Alice", row.Cells.Span[0].Value);
            Assert.Equal("30", row.Cells.Span[1].Value);
        }
        finally { File.Delete(path); }
    }
}
