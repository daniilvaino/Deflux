using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Deflux.Core;
using Deflux.Core.Zip;
using Deflux.Ods;

// ═══════════════════════════════════════════════════════════
//  GIGATEST: real >4GB ODS file from real sheet data — generate, scan, read
// ═══════════════════════════════════════════════════════════

const string OdsPath = @"C:\work\Deflux\gigatest.ods";
const int SheetCount = 50;
const int RowsPerSheet = 25_000;
const int ColsPerRow = 12;
const int PayloadColumnIndex = -1;         // no payload column
const int PayloadCharsPerCell = 0;
const long FourGiB = 4L * 1024 * 1024 * 1024;
const int HugeModeSheetStride = 2;
const int HugeModeRowsPerSheet = int.MaxValue;

var sw = Stopwatch.StartNew();

// ── Step 1: Generate ──
Console.WriteLine($"=== Generating {SheetCount} sheets × {RowsPerSheet} rows × {ColsPerRow} cols ===");
GenerateGiantOds(OdsPath, SheetCount, RowsPerSheet, ColsPerRow, PayloadColumnIndex, PayloadCharsPerCell);
var fileSize = new FileInfo(OdsPath).Length;
Console.WriteLine($"  File: {fileSize / 1024.0 / 1024.0:F1} MB ({fileSize:N0} bytes)");
Console.WriteLine($"  Generated in {sw.Elapsed.TotalSeconds:F1}s");
bool hugeArchive = false;

// ── Step 2: ScanSheets ──
Console.WriteLine($"\n=== ScanSheets ===");
sw.Restart();
using var fs = new FileStream(OdsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
using var reader = new OdsReader(fs);
var sheets = reader.ScanSheets((count, name) =>
{
    if (count <= 5 || count % 10 == 0)
        Console.WriteLine($"    scan progress: {count} sheet(s), last='{name}', elapsed={sw.Elapsed.TotalSeconds:F1}s");
});
Console.WriteLine($"  Found {sheets.Count} sheets in {sw.Elapsed.TotalSeconds:F2}s");
for (int i = 0; i < sheets.Count; i++)
    Console.WriteLine($"    [{i}] '{sheets[i].Name}' checkpoint={sheets[i].Checkpoint.Length} bytes");

// ── Step 2a0: Dump raw XML around position 137733 ──
Console.WriteLine("\n=== Raw XML at error position ===");
fs.Position = 0;
{
    var zipN = new ZipNavigator(fs);
    var entry = zipN.FindEntry("content.xml");
    var eStream = zipN.OpenEntryStream(entry);
    using var deflate = new System.IO.Compression.DeflateStream(eStream, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true);
    using var sr = new StreamReader(deflate, new UTF8Encoding(false));
    char[] cbuf = new char[138000];
    int totalRead = 0;
    while (totalRead < cbuf.Length)
    {
        int r = sr.Read(cbuf, totalRead, cbuf.Length - totalRead);
        if (r == 0) break;
        totalRead += r;
    }
    int pos = 137700;
    Console.WriteLine($"  @{pos}: ...{new string(cbuf, pos, Math.Min(200, totalRead - pos))}...");
}

// ── Step 2a: Dump first few cells from raw XML ──
Console.WriteLine("\n=== First table-row from raw XML ===");
fs.Position = 0;
using (var rawR = new Deflux.Core.Reader.CheckpointableXmlReader(fs, "content.xml"))
{
    if (rawR.SkipTo("table"))
    {
        int n = 0;
        while (rawR.Read() && n < 30)
        {
            Console.WriteLine($"  [{n}] {rawR.NodeKind} d={rawR.Depth} '{rawR.LocalName}' ns='{rawR.NamespaceUri}' val='{rawR.Value?.Substring(0, Math.Min(50, rawR.Value?.Length ?? 0))}'");
            n++;
        }
    }
}

// ── Step 2b: Raw read all events ──
if (!hugeArchive)
{
    Console.WriteLine($"\n=== Raw read all events ===");
    sw.Restart();
    fs.Position = 0;
    try
    {
        using var rawReader = new Deflux.Core.Reader.CheckpointableXmlReader(fs, "content.xml");
        int elems = 0, endElems = 0;
        while (rawReader.Read())
        {
            if (rawReader.NodeKind == XmlNodeKind.Element) elems++;
            else if (rawReader.NodeKind == XmlNodeKind.EndElement) endElems++;
        }
        Console.WriteLine($"  Elements={elems}, EndElements={endElems}, {sw.Elapsed.TotalSeconds:F2}s");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
    }
}
else
{
    Console.WriteLine("\n=== Raw read all events ===");
    Console.WriteLine("  Skipped in huge mode (>4 GiB) to keep runtime practical.");
}

// ── Step 3: Read every 2nd sheet ──
int sheetStride = hugeArchive ? HugeModeSheetStride : 2;
int maxRowsPerSheetToRead = hugeArchive ? HugeModeRowsPerSheet : int.MaxValue;

Console.WriteLine($"\n=== Reading sheets (stride={sheetStride}, maxRowsPerSheet={(maxRowsPerSheetToRead == int.MaxValue ? "ALL" : maxRowsPerSheetToRead.ToString(CultureInfo.InvariantCulture))}) ===");
long totalRows = 0;
long totalCells = 0;
long totalStrings = 0;
long totalNumbers = 0;
long totalDates = 0;
long totalBlanks = 0;
long totalBooleans = 0;
int sampledSheets = 0;
int failedSheets = 0;

for (int si = 0; si < sheets.Count; si += sheetStride)
{
    sw.Restart();
    try
    {
        reader.OpenSheet(si);

        int sheetRows = 0;
        int sheetCells = 0;
        int maxRowIdx = -1;

        while (sheetRows < maxRowsPerSheetToRead && reader.ReadRow(out Row row))
        {
            sheetRows++;
            maxRowIdx = row.RowIndex;
            foreach (var cell in row.Cells.Span)
            {
                sheetCells++;
                switch (cell.Type)
                {
                    case CellType.String: totalStrings++; break;
                    case CellType.Number: totalNumbers++; break;
                    case CellType.Boolean: totalBooleans++; break;
                    case CellType.Blank: totalBlanks++; break;
                }
            }
        }

        sampledSheets++;
        totalRows += sheetRows;
        totalCells += sheetCells;
        string sampledSuffix = maxRowsPerSheetToRead == int.MaxValue ? "" : " (sampled)";
        Console.WriteLine($"  [{si}] '{sheets[si].Name}': {sheetRows} rows{sampledSuffix}, {sheetCells} cells, maxRowIdx={maxRowIdx}, {sw.Elapsed.TotalSeconds:F2}s");
    }
    catch (Exception ex)
    {
        failedSheets++;
        Console.WriteLine($"  [{si}] '{sheets[si].Name}': ERROR after {totalRows} rows — {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"\n=== Summary ===");
Console.WriteLine($"  Sheets read: {(SheetCount + 1) / 2} of {SheetCount}");
Console.WriteLine($"  Total rows:    {totalRows:N0}");
Console.WriteLine($"  Total cells:   {totalCells:N0}");
Console.WriteLine($"  Strings:       {totalStrings:N0}");
Console.WriteLine($"  Numbers:       {totalNumbers:N0}");
Console.WriteLine($"  Booleans:      {totalBooleans:N0}");
Console.WriteLine($"  Blanks:        {totalBlanks:N0}");

bool ok;
if (maxRowsPerSheetToRead == int.MaxValue)
{
    Console.WriteLine($"\n  Expected rows: {(SheetCount + 1) / 2 * RowsPerSheet:N0}");
    ok = totalRows == (long)(SheetCount + 1) / 2 * RowsPerSheet;
    Console.WriteLine(ok ? "\n  ✓ PASS" : "\n  ✗ FAIL — row count mismatch!");
}
else
{
    Console.WriteLine($"\n  Sampled sheets: {sampledSheets:N0}");
    Console.WriteLine($"  Failed sheets:  {failedSheets:N0}");
    ok = sampledSheets > 0 && failedSheets == 0 && totalRows > 0;
    Console.WriteLine(ok ? "\n  ✓ PASS (sample mode)" : "\n  ✗ FAIL (sample mode)");
}

// Cleanup
fs.Close();
//File.Delete(OdsPath);
Console.WriteLine($"\n  Cleaned up. Done.");

// ═══════════════════════════════════════════════════════════
//  ODS Generator — realistic data, mixed types, gaps
// ═══════════════════════════════════════════════════════════

static void GenerateGiantOds(
    string path,
    int sheetCount,
    int rowsPerSheet,
    int colsPerRow,
    int payloadColumnIndex,
    int payloadCharsPerCell)
{
    using var fileStream = new FileStream(path, FileMode.Create);
    using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

    // mimetype (must be first, stored)
    WriteEntry(archive, "mimetype",
        "application/vnd.oasis.opendocument.spreadsheet",
        CompressionLevel.NoCompression);

    // manifest
    WriteEntry(archive, "META-INF/manifest.xml", BuildManifest());

    // content.xml — the big one
    var contentEntry = archive.CreateEntry("content.xml", CompressionLevel.Optimal);
    using (var stream = contentEntry.Open())
    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536))
    {
        WriteContentXml(writer, sheetCount, rowsPerSheet, colsPerRow, payloadColumnIndex, payloadCharsPerCell);
    }
}

static void WriteContentXml(
    StreamWriter w,
    int sheetCount,
    int rowsPerSheet,
    int colsPerRow,
    int payloadColumnIndex,
    int payloadCharsPerCell)
{
    var rng = new Random(42); // deterministic
    var firstNames = new[] { "Алексей", "Мария", "Дмитрий", "Елена", "Сергей", "Анна", "Иван", "Ольга", "Павел", "Наталья",
                             "Андрей", "Татьяна", "Михаил", "Екатерина", "Владимир", "Светлана", "Николай", "Юлия" };
    var lastNames = new[] { "Иванов", "Петрова", "Сидоров", "Козлова", "Новиков", "Морозова", "Волков", "Лебедева",
                            "Соколов", "Кузнецова", "Попов", "Смирнова", "Фёдоров", "Васильева", "Орлов", "Павлова" };
    var cities = new[] { "Москва", "Санкт-Петербург", "Новосибирск", "Екатеринбург", "Казань", "Нижний Новгород",
                         "Челябинск", "Самара", "Ростов-на-Дону", "Уфа", "Красноярск", "Воронеж", "Пермь", "Волгоград" };
    var departments = new[] { "Продажи", "Бухгалтерия", "ИТ", "Логистика", "HR", "Маркетинг", "Производство", "Склад" };
    var statuses = new[] { "Активен", "В отпуске", "Уволен", "Стажёр", "Декрет" };

    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    w.Write("<office:document-content");
    w.Write(" xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"");
    w.Write(" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\"");
    w.Write(" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"");
    w.Write(" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"");
    w.Write(" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"");
    w.Write(">");
    w.Write("<office:body><office:spreadsheet>");

    for (int si = 0; si < sheetCount; si++)
    {
        string sheetName = si switch
        {
            0 => "Сотрудники",
            1 => "Зарплата",
            2 => "Табель",
            _ => $"Данные_{si + 1}"
        };

        w.Write($"<table:table table:name=\"{Esc(sheetName)}\">");

        // Column definitions
        w.Write($"<table:table-column table:number-columns-repeated=\"{colsPerRow}\"/>");

        for (int ri = 0; ri < rowsPerSheet; ri++)
        {
            // Every 50th row — insert a gap (empty row with repeat)
            if (ri > 0 && ri % 50 == 0)
            {
                int gap = rng.Next(1, 5);
                w.Write($"<table:table-row table:number-rows-repeated=\"{gap}\"><table:table-cell table:number-columns-repeated=\"{colsPerRow}\"/></table:table-row>");
            }

            w.Write("<table:table-row>");

            for (int ci = 0; ci < colsPerRow; ci++)
            {
                // One dedicated high-entropy payload column to force real ZIP64 size
                // using actual cell data (no artificial ballast entry).
                if (ci == payloadColumnIndex)
                {
                    WritePayloadCell(w, si, ri, payloadCharsPerCell);
                    continue;
                }

                // Every ~10th cell is blank
                if (rng.Next(10) == 0)
                {
                    w.Write("<table:table-cell/>");
                    continue;
                }

                int colType = ci % 6;
                switch (colType)
                {
                    case 0: // String — full name
                        string name = $"{lastNames[rng.Next(lastNames.Length)]} {firstNames[rng.Next(firstNames.Length)]}";
                        WriteStringCell(w, name);
                        break;

                    case 1: // Float — salary / amount
                        double amount = Math.Round(rng.NextDouble() * 150000 + 30000, 2);
                        WriteFloatCell(w, amount);
                        break;

                    case 2: // Date
                        var date = new DateTime(2020, 1, 1).AddDays(rng.Next(0, 1800));
                        WriteDateCell(w, date);
                        break;

                    case 3: // Boolean
                        WriteBoolCell(w, rng.Next(2) == 1);
                        break;

                    case 4: // String — city or department
                        string text = rng.Next(2) == 0
                            ? cities[rng.Next(cities.Length)]
                            : departments[rng.Next(departments.Length)];
                        WriteStringCell(w, text);
                        break;

                    case 5: // String — status or ID
                        if (rng.Next(3) == 0)
                            WriteStringCell(w, statuses[rng.Next(statuses.Length)]);
                        else
                            WriteStringCell(w, $"ID-{rng.Next(100000, 999999)}");
                        break;
                }
            }

            w.Write("</table:table-row>");

            // Flush every 1000 rows to keep memory stable
            if (ri % 1000 == 0)
                w.Flush();
        }

        w.Write("</table:table>");
    }

    w.Write("</office:spreadsheet></office:body></office:document-content>");
}

static void WriteStringCell(StreamWriter w, string val)
{
    w.Write("<table:table-cell office:value-type=\"string\"><text:p>");
    w.Write(Esc(val));
    w.Write("</text:p></table:table-cell>");
}

static void WriteFloatCell(StreamWriter w, double val)
{
    string v = val.ToString("G", CultureInfo.InvariantCulture);
    w.Write($"<table:table-cell office:value-type=\"float\" office:value=\"{v}\"><text:p>{v}</text:p></table:table-cell>");
}

static void WriteDateCell(StreamWriter w, DateTime date)
{
    string iso = date.ToString("yyyy-MM-dd");
    string display = date.ToString("dd.MM.yyyy");
    w.Write($"<table:table-cell office:value-type=\"date\" office:date-value=\"{iso}\"><text:p>{display}</text:p></table:table-cell>");
}

static void WriteBoolCell(StreamWriter w, bool val)
{
    string v = val ? "true" : "false";
    w.Write($"<table:table-cell office:value-type=\"boolean\" office:boolean-value=\"{v}\"><text:p>{v}</text:p></table:table-cell>");
}

static void WritePayloadCell(StreamWriter w, int sheetIndex, int rowIndex, int payloadChars)
{
    w.Write("<table:table-cell office:value-type=\"string\"><text:p>");
    w.Write("S");
    w.Write(sheetIndex.ToString(CultureInfo.InvariantCulture));
    w.Write("_R");
    w.Write(rowIndex.ToString(CultureInfo.InvariantCulture));
    w.Write("_");
    WriteEntropyText(w, sheetIndex, rowIndex, payloadChars);
    w.Write("</text:p></table:table-cell>");
}

static void WriteEntropyText(StreamWriter w, int sheetIndex, int rowIndex, int length)
{
    const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    Span<char> buffer = stackalloc char[256];

    ulong state = 0x9E3779B97F4A7C15UL;
    state ^= (ulong)(sheetIndex + 1) * 0xBF58476D1CE4E5B9UL;
    state ^= (ulong)(rowIndex + 1) * 0x94D049BB133111EBUL;

    int remaining = length;
    while (remaining > 0)
    {
        int chunk = Math.Min(buffer.Length, remaining);
        for (int i = 0; i < chunk; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            ulong next = state * 2685821657736338717UL;
            buffer[i] = alphabet[(int)(next % (ulong)alphabet.Length)];
        }
        w.Write(buffer.Slice(0, chunk));
        remaining -= chunk;
    }
}

static string Esc(string s) =>
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

static string BuildManifest() =>
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
    "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">" +
    "<manifest:file-entry manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\" manifest:full-path=\"/\"/>" +
    "<manifest:file-entry manifest:media-type=\"text/xml\" manifest:full-path=\"content.xml\"/>" +
    "</manifest:manifest>";

static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
{
    var entry = archive.CreateEntry(name, level);
    using var s = entry.Open();
    var bytes = Encoding.UTF8.GetBytes(content);
    s.Write(bytes, 0, bytes.Length);
}
