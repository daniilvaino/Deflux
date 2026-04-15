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

### Checkpoint binary format

```
┌─────────┬─────────────┬──────────────┬──────────────────────────────┐
│ version │ totalLength │ payloadCRC32 │           payload            │
│  1 byte │   4 bytes   │   4 bytes    │         ~40-45 KB            │
└─────────┴─────────────┴──────────────┴──────────────────────────────┘

payload:
  ┌─ Entry ID ──────────────────────────────────────────────────────┐
  │  entryName (UTF-8, length-prefixed)                             │
  │  entryCRC32 · compressedSize · adjustedCompressedOffset         │
  ├─ DEFLATE state (section with length prefix) ────────────────────┤
  │  mode · neededBits · repLength · repDist · uncomprLen           │
  │  isLastBlock · totalOut · totalIn                               │
  │  OutputWindow: byte[32768] + windowEnd + windowFilled           │
  │  StreamManipulator: window + start/end + bitBuffer + bitsCount  │
  │  Huffman trees: litlen[] + dist[] (null if static/inactive)     │
  │  DynHeader state (if mid-dynamic block)                         │
  │  Adler32 checksum · adjustedOffset · totalBytesFeeded           │
  ├─ Pending decompressed bytes (0-8 KB) ───────────────────────────┤
  ├─ XML parser state (section with length prefix) ─────────────────┤
  │  parseState · depth · elementStack[] · namespaceBindings[]      │
  │  pendingText · lineNumber · columnNumber · incompleteUTF8       │
  └─────────────────────────────────────────────────────────────────┘

Three-level verification on restore:
  1. version ≠ expected  →  CheckpointVersionException
  2. CRC-32 mismatch    →  CheckpointMismatchException (corrupted)
  3. ZIP entry CRC/size  →  CheckpointMismatchException (file modified)
```

All values little-endian. `BinaryWriter`/`BinaryReader`, no external dependencies.

---

## Performance

Benchmark on a **70 MB ODS file** (50 sheets, 1.25M rows, 6.7M cells).  
Each library ran in a separate process for clean memory measurement (`PeakWorkingSet64`).

| Library | Read all rows | Need only sheet 50 | Peak memory |
|---|---|---|---|
| **Deflux** | **16.3 s** | **7.8 s** | **83 MB** |
| OdsReaderWriter | 29.7 s | 29.7 s (must load all) | 7,037 MB |
| Aspose.Cells | 22.7 s | 22.5 s (must load all) | 2,019 MB |

DOM libraries (OdsReaderWriter, Aspose) must load the entire file into memory even if you only need one sheet. Deflux streams — `ScanSheets()` does one pass (~8s), then any `OpenSheet()` restores from a cached checkpoint in ~0.2s.

| Deflux internals | |
|---|---|
| Checkpoint size | ~45 KB |
| Checkpoint save | < 5 ms |
| Checkpoint restore | < 50 ms |
| Steady-state memory | < 1 MB |

---

## Limitations

**By design:**
- **UTF-8 only** — this covers 99%+ of real-world XLSX/ODS/EPUB/DOCX files. Supporting legacy encodings would add complexity with near-zero practical benefit.
- **Sync-only** — the checkpoint model is inherently sequential (save state at point P, resume from P). Async would add overhead to every `Read()` call with no throughput gain, since the bottleneck is DEFLATE decompression which is CPU-bound.
- **Single instance = single thread** — each reader instance is not thread-safe. However, multiple reader instances can safely work on the same file concurrently (each opens its own stream).

**Currently not supported:**
- No ZIP encryption support
- No async overloads

**XLSX-specific:**
- SharedStringsTable is loaded fully into memory
- No date/style detection — Excel stores dates as serial floats; parsing them requires `styles.xml`, which is not yet implemented. Raw float values are returned as-is.

---

## License

MIT

DEFLATE decompressor forked from [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (MIT).
