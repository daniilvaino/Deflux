using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

// ═══════════════════════════════════════════════════════════
//  Deflux vs competitors — ODS benchmark
//  Each library runs in a separate child process for
//  clean memory isolation.
// ═══════════════════════════════════════════════════════════

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;

const string OdsPath = @"C:\work\Deflux\benchmark.ods";
const int SheetCount = 50;
const int RowsPerSheet = 25_000;
const int ColsPerRow = 12;

var mode = args.Length > 0 ? args[0] : "orchestrator";

if (mode == "orchestrator")
    RunOrchestrator();
else
    RunWorker(mode);

// ═══════════════════════════════════════════════════════════
//  Orchestrator — spawns isolated processes, collects results
// ═══════════════════════════════════════════════════════════

void RunOrchestrator()
{
    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  ODS Benchmark — 50 sheets × 25K rows × 12 cols        ║");
    Console.WriteLine("║  Each library runs in a separate process (clean memory) ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    Console.WriteLine();

    // Generate test file
    if (!File.Exists(OdsPath))
    {
        Console.Write("Generating test file... ");
        var sw = Stopwatch.StartNew();
        GenerateOds(OdsPath, SheetCount, RowsPerSheet, ColsPerRow);
        Console.WriteLine($"done ({new FileInfo(OdsPath).Length / 1024.0 / 1024.0:F1} MB, {sw.Elapsed.TotalSeconds:F1}s)");
    }
    else
    {
        Console.WriteLine($"Using existing {OdsPath} ({new FileInfo(OdsPath).Length / 1024.0 / 1024.0:F1} MB)");
    }
    Console.WriteLine();

    Console.WriteLine("┌─────────────────┬──────────────┬──────────────┬──────────────┬─────────────┐");
    Console.WriteLine("│ Library         │ Read all     │ Open sheet50 │ Peak memory  │ Rows        │");
    Console.WriteLine("├─────────────────┼──────────────┼──────────────┼──────────────┼─────────────┤");

    string self = Environment.ProcessPath!;
    string[] libs = { "deflux", "odsreaderwriter", "aspose" };

    foreach (var lib in libs)
    {
        var psi = new ProcessStartInfo(self, lib)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        using var proc = Process.Start(psi)!;
        proc.WaitForExit(300_000); // 5 min timeout
        string stdout = proc.StandardOutput.ReadToEnd().Trim();
        string stderr = proc.StandardError.ReadToEnd().Trim();

        if (proc.ExitCode != 0 || string.IsNullOrEmpty(stdout))
        {
            string errMsg = stderr.Length > 0 ? stderr.Split('\n')[^1] : "unknown error";
            if (errMsg.Length > 50) errMsg = errMsg[..50] + "...";
            Console.WriteLine($"│ {lib,-15} │         FAIL │            — │            — │ {errMsg} │");
        }
        else
        {
            Console.WriteLine(stdout);
        }
    }

    Console.WriteLine("└─────────────────┴──────────────┴──────────────┴──────────────┴─────────────┘");
    Console.WriteLine();
    Console.WriteLine($"Note: Deflux 'Open sheet50' includes ScanSheets (~8s) + checkpoint restore (~0.2s).");
    Console.WriteLine($"      Subsequent OpenSheet() calls use cached checkpoints: ~0.2s each.");
}

// ═══════════════════════════════════════════════════════════
//  Workers — each runs a single library benchmark
// ═══════════════════════════════════════════════════════════

void RunWorker(string lib)
{
    switch (lib)
    {
        case "deflux": WorkerDeflux(); break;
        case "odsreaderwriter": WorkerOdsReaderWriter(); break;
        case "aspose": WorkerAspose(); break;
        default: throw new ArgumentException($"Unknown library: {lib}");
    }
}

void WorkerDeflux()
{
    // Read all
    var sw = Stopwatch.StartNew();
    long totalRows = 0;
    using (var fs = File.OpenRead(OdsPath))
    using (var reader = new Deflux.Ods.OdsReader(fs))
    {
        var sheets = reader.ScanSheets();
        for (int i = 0; i < sheets.Count; i++)
        {
            reader.OpenSheet(i);
            while (reader.ReadRow(out _))
                totalRows++;
        }
    }
    var readAllTime = sw.Elapsed;
    long peakAfterReadAll = Process.GetCurrentProcess().PeakWorkingSet64;

    // Open last sheet (includes scan + checkpoint restore)
    sw.Restart();
    using (var fs = File.OpenRead(OdsPath))
    using (var reader = new Deflux.Ods.OdsReader(fs))
    {
        reader.ScanSheets();
        reader.OpenSheet(SheetCount - 1);
        reader.ReadRow(out _);
    }
    var openLastTime = sw.Elapsed;
    long peakFinal = Process.GetCurrentProcess().PeakWorkingSet64;

    long peak = Math.Max(peakAfterReadAll, peakFinal);
    PrintRow("Deflux",
        $"{readAllTime.TotalSeconds:F2}s",
        $"{openLastTime.TotalSeconds:F2}s",
        $"{peak / 1024.0 / 1024.0:F0} MB",
        $"{totalRows:N0}");
}

void WorkerOdsReaderWriter()
{
    var asm = System.Reflection.Assembly.LoadFrom(
        Path.Combine(AppContext.BaseDirectory, "OdsReaderWriter.dll"));
    var type = asm.GetType("Zaretto.ODS.ODSReaderWriter")
        ?? throw new Exception("Type not found");
    var instance = Activator.CreateInstance(type)!;
    var readMethod = type.GetMethod("ReadOdsFile", new[] { typeof(string) })
        ?? throw new Exception("ReadOdsFile not found");

    // Read all (DOM — loads everything)
    var sw = Stopwatch.StartNew();
    var dataSet = (System.Data.DataSet)readMethod.Invoke(instance, new object[] { OdsPath })!;

    long totalRows = 0;
    foreach (System.Data.DataTable table in dataSet.Tables)
        totalRows += table.Rows.Count;
    var readAllTime = sw.Elapsed;

    long peak = Process.GetCurrentProcess().PeakWorkingSet64;
    PrintRow("OdsReaderWriter",
        $"{readAllTime.TotalSeconds:F2}s",
        "(DOM, in RAM)",
        $"{peak / 1024.0 / 1024.0:F0} MB",
        $"{totalRows:N0}");
}

void WorkerAspose()
{
    // Read all
    var sw = Stopwatch.StartNew();
    var workbook = new Aspose.Cells.Workbook(OdsPath);

    long totalRows = 0;
    foreach (Aspose.Cells.Worksheet ws in workbook.Worksheets)
        totalRows += ws.Cells.MaxDataRow + 1;
    var readAllTime = sw.Elapsed;
    long peakAfterRead = Process.GetCurrentProcess().PeakWorkingSet64;

    // Open last = reload entire workbook
    GC.Collect(2, GCCollectionMode.Forced, true);
    GC.WaitForPendingFinalizers();
    sw.Restart();
    var wb2 = new Aspose.Cells.Workbook(OdsPath);
    var lastSheet = wb2.Worksheets[wb2.Worksheets.Count - 1];
    _ = lastSheet.Cells.MaxDataRow;
    var openLastTime = sw.Elapsed;

    long peak = Math.Max(peakAfterRead, Process.GetCurrentProcess().PeakWorkingSet64);
    PrintRow("Aspose.Cells",
        $"{readAllTime.TotalSeconds:F2}s",
        $"{openLastTime.TotalSeconds:F2}s",
        $"{peak / 1024.0 / 1024.0:F0} MB",
        $"{totalRows:N0}");
}

// ═══════════════════════════════════════════════════════════

void PrintRow(string name, string readAll, string openLast, string peakMem, string rows)
{
    Console.Write($"│ {name,-15} │ {readAll,12} │ {openLast,12} │ {peakMem,12} │ {rows,11} │");
}

// ═══════════════════════════════════════════════════════════
//  ODS Generator
// ═══════════════════════════════════════════════════════════

static void GenerateOds(string path, int sheetCount, int rowsPerSheet, int colsPerRow)
{
    using var fileStream = new FileStream(path, FileMode.Create);
    using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);
    WEntry(archive, "mimetype", "application/vnd.oasis.opendocument.spreadsheet", CompressionLevel.NoCompression);
    WEntry(archive, "META-INF/manifest.xml",
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">" +
        "<manifest:file-entry manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\" manifest:full-path=\"/\"/>" +
        "<manifest:file-entry manifest:media-type=\"text/xml\" manifest:full-path=\"content.xml\"/></manifest:manifest>");

    var e = archive.CreateEntry("content.xml", CompressionLevel.Optimal);
    using var stream = e.Open();
    using var w = new StreamWriter(stream, new UTF8Encoding(false), 65536);

    var rng = new Random(42);
    string[] fn = { "Алексей", "Мария", "Дмитрий", "Елена", "Сергей", "Анна", "Иван", "Ольга" };
    string[] ln = { "Иванов", "Петрова", "Сидоров", "Козлова", "Новиков", "Морозова" };
    string[] ct = { "Москва", "СПб", "Новосибирск", "Екатеринбург", "Казань" };
    string[] dp = { "Продажи", "Бухгалтерия", "ИТ", "Логистика", "HR" };

    w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><office:spreadsheet>");
    for (int si = 0; si < sheetCount; si++)
    {
        w.Write($"<table:table table:name=\"{Esc(si < 3 ? new[] { "Сотрудники", "Зарплата", "Табель" }[si] : $"Данные_{si + 1}")}\">");
        w.Write($"<table:table-column table:number-columns-repeated=\"{colsPerRow}\"/>");
        for (int ri = 0; ri < rowsPerSheet; ri++)
        {
            if (ri > 0 && ri % 50 == 0) w.Write($"<table:table-row table:number-rows-repeated=\"{rng.Next(1, 5)}\"><table:table-cell table:number-columns-repeated=\"{colsPerRow}\"/></table:table-row>");
            w.Write("<table:table-row>");
            for (int ci = 0; ci < colsPerRow; ci++)
            {
                if (rng.Next(10) == 0) { w.Write("<table:table-cell/>"); continue; }
                switch (ci % 6)
                {
                    case 0: WS(w, $"{ln[rng.Next(ln.Length)]} {fn[rng.Next(fn.Length)]}"); break;
                    case 1: WF(w, Math.Round(rng.NextDouble() * 150000 + 30000, 2)); break;
                    case 2: WD(w, new DateTime(2020, 1, 1).AddDays(rng.Next(1800))); break;
                    case 3: WB(w, rng.Next(2) == 1); break;
                    case 4: WS(w, rng.Next(2) == 0 ? ct[rng.Next(ct.Length)] : dp[rng.Next(dp.Length)]); break;
                    case 5: WS(w, $"ID-{rng.Next(100000, 999999)}"); break;
                }
            }
            w.Write("</table:table-row>");
            if (ri % 1000 == 0) w.Flush();
        }
        w.Write("</table:table>");
    }
    w.Write("</office:spreadsheet></office:body></office:document-content>");
}

static void WS(StreamWriter w, string v) => w.Write($"<table:table-cell office:value-type=\"string\"><text:p>{Esc(v)}</text:p></table:table-cell>");
static void WF(StreamWriter w, double v) { var s = v.ToString("G", CultureInfo.InvariantCulture); w.Write($"<table:table-cell office:value-type=\"float\" office:value=\"{s}\"><text:p>{s}</text:p></table:table-cell>"); }
static void WD(StreamWriter w, DateTime d) => w.Write($"<table:table-cell office:value-type=\"date\" office:date-value=\"{d:yyyy-MM-dd}\"><text:p>{d:dd.MM.yyyy}</text:p></table:table-cell>");
static void WB(StreamWriter w, bool v) { var s = v ? "true" : "false"; w.Write($"<table:table-cell office:value-type=\"boolean\" office:boolean-value=\"{s}\"><text:p>{s}</text:p></table:table-cell>"); }
static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
static void WEntry(ZipArchive a, string n, string c, CompressionLevel l = CompressionLevel.Optimal) { var e = a.CreateEntry(n, l); using var s = e.Open(); var b = Encoding.UTF8.GetBytes(c); s.Write(b, 0, b.Length); }
