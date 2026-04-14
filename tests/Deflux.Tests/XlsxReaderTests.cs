using System.Collections.Generic;
using System.IO;
using Deflux.Core;
using Deflux.Xlsx;
using Deflux.Tests.Fixtures;
using Xunit;

namespace Deflux.Tests;

public class XlsxReaderTests
{
    [Fact]
    public void ReadSheet_BasicData()
    {
        var path = TestFixtures.CreateXlsx(
            ("Sheet1", new[] {
                new[] { "Name", "Age", "City" },
                new[] { "Alice", "30", "NYC" },
                new[] { "Bob", "25", "LA" },
            }));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new XlsxReader(fs);
            var names = reader.GetSheetNames();
            Assert.Single(names);
            Assert.Equal("Sheet1", names[0]);

            reader.OpenSheet(0);

            Assert.True(reader.ReadRow(out Row r1));
            Assert.Equal(0, r1.RowIndex);
            Assert.Equal(3, r1.Cells.Length);
            Assert.Equal("Name", r1.Cells.Span[0].Value);
            Assert.Equal(CellType.String, r1.Cells.Span[0].Type);
            Assert.Equal("Age", r1.Cells.Span[1].Value);
            Assert.Equal(CellType.String, r1.Cells.Span[1].Type);

            Assert.True(reader.ReadRow(out Row r2));
            Assert.Equal("Alice", r2.Cells.Span[0].Value);
            Assert.Equal("30", r2.Cells.Span[1].Value);
            Assert.Equal("NYC", r2.Cells.Span[2].Value);

            Assert.True(reader.ReadRow(out Row r3));
            Assert.Equal("Bob", r3.Cells.Span[0].Value);

            Assert.False(reader.ReadRow(out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MultipleSheets()
    {
        var path = TestFixtures.CreateXlsx(
            ("Data", new[] { new[] { "a", "b" } }),
            ("Summary", new[] { new[] { "x", "y" } }));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new XlsxReader(fs);
            Assert.Equal(2, reader.GetSheetNames().Count);

            reader.OpenSheet("Summary");
            Assert.True(reader.ReadRow(out Row row));
            Assert.Equal("x", row.Cells.Span[0].Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Checkpoint_XlsxRoundTrip()
    {
        var rows = new string[50][];
        for (int i = 0; i < 50; i++)
            rows[i] = new[] { $"Name_{i}", $"{i * 10}" };

        var path = TestFixtures.CreateXlsx(("Data", rows));

        try
        {
            using var fs = File.OpenRead(path);

            var allRows = new List<(string name, string val)>();
            using (var reader = new XlsxReader(fs))
            {
                reader.OpenSheet(0);
                while (reader.ReadRow(out Row r))
                    allRows.Add((r.Cells.Span[0].Value!, r.Cells.Span[1].Value!));
            }

            byte[] cp;
            var partialRows = new List<(string name, string val)>();
            fs.Position = 0;
            using (var reader = new XlsxReader(fs))
            {
                reader.OpenSheet(0);
                for (int i = 0; i < 20; i++)
                {
                    reader.ReadRow(out Row r);
                    partialRows.Add((r.Cells.Span[0].Value!, r.Cells.Span[1].Value!));
                }
                cp = reader.SaveCheckpoint();
                reader.RestoreCheckpoint(cp);
                while (reader.ReadRow(out Row r))
                    partialRows.Add((r.Cells.Span[0].Value!, r.Cells.Span[1].Value!));
            }

            Assert.Equal(allRows.Count, partialRows.Count);
            for (int i = 0; i < allRows.Count; i++)
            {
                Assert.Equal(allRows[i].name, partialRows[i].name);
                Assert.Equal(allRows[i].val, partialRows[i].val);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmptySheet()
    {
        var path = TestFixtures.CreateXlsx(("Empty", System.Array.Empty<string[]>()));

        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new XlsxReader(fs);
            reader.OpenSheet(0);
            Assert.False(reader.ReadRow(out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadSheet_Zip64Container()
    {
        var path = TestFixtures.CreateXlsx(
            ("Sheet1", new[] {
                new[] { "A", "B" },
                new[] { "1", "2" },
            }));

        try
        {
            TestFixtures.ConvertArchiveToZip64(path);

            using var fs = File.OpenRead(path);
            using var reader = new XlsxReader(fs);
            reader.OpenSheet(0);

            Assert.True(reader.ReadRow(out Row header));
            Assert.Equal("A", header.Cells.Span[0].Value);
            Assert.Equal("B", header.Cells.Span[1].Value);

            Assert.True(reader.ReadRow(out Row row));
            Assert.Equal("1", row.Cells.Span[0].Value);
            Assert.Equal("2", row.Cells.Span[1].Value);
        }
        finally { File.Delete(path); }
    }
}
