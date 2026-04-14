# Deflux

Streaming XML-from-ZIP engine with serializable checkpoints for .NET 8+.  
Read XLSX, ODS, and other ZIP-based XML formats with **pause/resume across process restarts** — no re-decompression, no re-parsing. Pure C#, zero native dependencies.

```
Read(0 -> P) + Save(P) + [restart] + Restore(P) + Read(P -> end) === Read(0 -> end)
```

---

## Install

```
Deflux.Core     — low-level engine (ZIP + DEFLATE + XML + checkpoints)
Deflux.Xlsx     — XLSX streaming reader
Deflux.Ods      — ODS streaming reader
```

All APIs accept a seekable `Stream`. Nothing takes file paths — you control the stream lifetime.

---

## Examples

### XLSX — streaming rows with checkpoint

```csharp
using Deflux.Xlsx;
using Deflux.Core;

using var stream = File.OpenRead("report.xlsx");
using var reader = new XlsxReader(stream);

Console.WriteLine(string.Join(", ", reader.GetSheetNames()));

reader.OpenSheet(0);

byte[]? checkpoint = null;
int n = 0;

while (reader.ReadRow(out Row row))
{
    foreach (var cell in row.Cells.Span)
        Console.Write($"[{cell.ColumnIndex}:{cell.Type}] {cell.Value}  ");
    Console.WriteLine();

    // Save checkpoint every 10k rows
    if (++n % 10_000 == 0)
        checkpoint = reader.SaveCheckpoint();
}

// Later (even in a different process):
if (checkpoint != null)
{
    using var stream2 = File.OpenRead("report.xlsx");
    using var reader2 = new XlsxReader(stream2);
    reader2.RestoreCheckpoint(checkpoint);

    while (reader2.ReadRow(out Row row))
        ProcessRow(row); // continues exactly where we left off
}
```

### ODS — scan sheets, instant open via checkpoint

```csharp
using Deflux.Ods;
using Deflux.Core;

using var stream = File.OpenRead("data.ods");
using var reader = new OdsReader(stream);

// One pass through content.xml — finds all sheets and caches a
// checkpoint (~45 KB) at the start of each one.
IReadOnlyList<SheetInfo> sheets = reader.ScanSheets();

foreach (var s in sheets)
    Console.WriteLine($"{s.Name}  (checkpoint {s.Checkpoint.Length} bytes)");

// Open any sheet instantly — checkpoint restore, no re-decompression.
reader.OpenSheet("Summary");

while (reader.ReadRow(out Row row))
{
    foreach (var cell in row.Cells.Span)
        Console.Write($"{cell.Value}\t");
    Console.WriteLine();
}
```

### Low-level XML — read any entry from any ZIP

```csharp
using Deflux.Core.Reader;
using Deflux.Core;

// Works with EPUB, DOCX, KMZ, 3MF — anything that is ZIP + XML.
using var stream = File.OpenRead("book.epub");
using var reader = new CheckpointableXmlReader(stream, "OEBPS/chapter3.xhtml");

if (reader.SkipTo("body"))
{
    foreach (var _ in reader.ReadElements("p"))
    {
        reader.Read(); // Text node
        Console.WriteLine(reader.Value);

        if (NeedPause())
        {
            byte[] cp = reader.SaveCheckpoint();
            File.WriteAllBytes("progress.bin", cp);
            break;
        }
    }
}
```

---

## How it works

```
Stream
  -> ZipNavigator        parse Central Directory, open entry as SubStream
  -> CheckpointableInflater   forked SharpZipLib DEFLATE with save/restore
  -> MinimalXmlParser    feed-model, chunk-boundary safe, namespace-aware
  -> CheckpointableXmlReader  unified public API
  -> XlsxReader / OdsReader   format modules
```

**Checkpoint** (~45 KB) captures the full vertical state:
- DEFLATE: 32 KB sliding window + Huffman trees + bit buffer + stream position
- XML parser: element stack, namespace bindings, parse state
- Pending bytes between inflater and parser

Restore seeks the compressed stream to the saved position, rebuilds all state, and the next `Read()` continues transparently.

---

## Performance

Measured on a 76 MB ODS file (50 sheets, 1.25M rows, 6.7M cells):

| | |
|---|---|
| ScanSheets (50 sheets) | 9 s |
| OpenSheet (checkpoint restore) | 0.2 s |
| ReadRow throughput | ~125K rows/s |
| Checkpoint size | ~45 KB |
| Memory | < 1 MB steady state |

---

## Limitations (v1)

- UTF-8 only
- No ZIP encryption
- No async (sync-only)
- No thread safety (single-threaded by design)
- XLSX SharedStringsTable loaded fully into memory
- No XLSX date/style detection (dates returned as serial floats)

---

## License

MIT

DEFLATE decompressor forked from [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (MIT).
