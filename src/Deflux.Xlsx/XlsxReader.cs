using System;
using System.Collections.Generic;
using System.IO;
using Deflux.Core;
using Deflux.Core.Exceptions;
using Deflux.Core.Reader;
using Deflux.Core.Zip;

namespace Deflux.Xlsx;

/// <summary>
/// Streaming XLSX reader with checkpoint support.
/// Accepts a seekable stream. Does NOT own it — caller manages lifetime.
/// SharedStringsTable is loaded eagerly and NOT included in checkpoint.
/// </summary>
public class XlsxReader : IDisposable, ICheckpointable
{
    private readonly Stream _stream;
    private ZipNavigator _zip;
    private List<string> _sheetNames = null!;
    private List<string> _sheetPaths = null!;
    private string[] _sharedStrings = null!;

    private CheckpointableXmlReader? _reader;
    private string? _currentSheetEntry;
    private bool _disposed;

    public XlsxReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable", nameof(stream));

        _zip = new ZipNavigator(_stream);
        LoadWorkbookMetadata();
        LoadSharedStrings();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(XlsxReader));
    }

    public IReadOnlyList<string> GetSheetNames() => _sheetNames;

    public void OpenSheet(string sheetName)
    {
        ThrowIfDisposed();
        int idx = _sheetNames.IndexOf(sheetName);
        if (idx < 0)
            throw new ArgumentException($"Sheet not found: {sheetName}");
        OpenSheet(idx);
    }

    public void OpenSheet(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= _sheetNames.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        _reader?.Dispose();
        _currentSheetEntry = _sheetPaths[index];
        _reader = new CheckpointableXmlReader(_stream, _currentSheetEntry);
        _reader.SkipTo("sheetData");
    }

    public bool ReadRow(out Row row)
    {
        ThrowIfDisposed();
        row = default;
        if (_reader == null)
            throw new InvalidOperationException("No sheet opened. Call OpenSheet() first.");

        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.Element && _reader.LocalName == "row")
            {
                row = ParseRow();
                return true;
            }
            if (_reader.NodeKind == XmlNodeKind.EndElement && _reader.LocalName == "sheetData")
                return false;
        }
        return false;
    }

    // ── Checkpoint ──

    public byte[] SaveCheckpoint()
    {
        ThrowIfDisposed();
        if (_reader == null)
            throw new InvalidOperationException("No sheet opened");
        return _reader.SaveCheckpoint();
    }

    public void RestoreCheckpoint(byte[] data)
    {
        ThrowIfDisposed();
        // Re-read metadata from same stream
        _zip = new ZipNavigator(_stream);
        LoadWorkbookMetadata();
        LoadSharedStrings();

        _currentSheetEntry = CheckpointableXmlReader.PeekCheckpointEntryName(data);

        _reader?.Dispose();
        _reader = new CheckpointableXmlReader(_stream, _currentSheetEntry);
        _reader.RestoreCheckpoint(data);
    }

    // ── Row parsing ──

    private Row ParseRow()
    {
        string? rowRef = _reader!.GetAttribute("r");
        int rowIndex = rowRef != null ? int.Parse(rowRef) - 1 : 0;

        var cells = new List<Cell>();

        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.EndElement && _reader.LocalName == "row")
                break;
            if (_reader.NodeKind == XmlNodeKind.Element && _reader.LocalName == "c")
                cells.Add(ParseCell());
        }

        return new Row(rowIndex, cells.ToArray());
    }

    private Cell ParseCell()
    {
        string? cellRef = _reader!.GetAttribute("r");
        string? cellType = _reader.GetAttribute("t");
        int colIndex = cellRef != null ? ParseColumnIndex(cellRef) : 0;

        string? rawValue = null;
        string? inlineStr = null;
        int cellDepth = _reader.Depth;

        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.EndElement && _reader.Depth == cellDepth)
                break;

            if (_reader.NodeKind == XmlNodeKind.Element)
            {
                if (_reader.LocalName == "v")
                {
                    if (_reader.Read() && _reader.NodeKind == XmlNodeKind.Text)
                        rawValue = _reader.Value;
                }
                else if (_reader.LocalName == "t" && cellType == "inlineStr")
                {
                    if (_reader.Read() && _reader.NodeKind == XmlNodeKind.Text)
                        inlineStr = _reader.Value;
                }
            }
        }

        return ResolveCellValue(colIndex, cellType, rawValue, inlineStr);
    }

    private Cell ResolveCellValue(int colIndex, string? cellType, string? rawValue, string? inlineStr)
    {
        return cellType switch
        {
            "s" when rawValue != null && int.TryParse(rawValue, out int i) =>
                i >= 0 && i < _sharedStrings.Length
                    ? new Cell(colIndex, _sharedStrings[i], CellType.String)
                    : throw new XmlParseException($"Shared string index out of range: {i}"),
            "s" => new Cell(colIndex, rawValue, CellType.String),
            "b" => new Cell(colIndex, rawValue, CellType.Boolean),
            "e" => new Cell(colIndex, rawValue, CellType.Error),
            "str" => new Cell(colIndex, rawValue, CellType.String),
            "inlineStr" => new Cell(colIndex, inlineStr, CellType.String),
            "n" or null => rawValue == null
                ? new Cell(colIndex, null, CellType.Blank)
                : new Cell(colIndex, rawValue, CellType.Number),
            _ => new Cell(colIndex, rawValue, CellType.String),
        };
    }

    internal static int ParseColumnIndex(string cellRef)
    {
        int col = 0;
        for (int i = 0; i < cellRef.Length; i++)
        {
            char c = cellRef[i];
            if (c >= 'A' && c <= 'Z')
                col = col * 26 + (c - 'A' + 1);
            else
                break;
        }
        return col - 1;
    }

    // ── Workbook metadata ──

    private void LoadWorkbookMetadata()
    {
        _sheetNames = new List<string>();
        _sheetPaths = new List<string>();

        var relMap = new Dictionary<string, string>();
        using (var relsReader = new CheckpointableXmlReader(_stream, "xl/_rels/workbook.xml.rels"))
        {
            while (relsReader.Read())
            {
                if (relsReader.NodeKind == XmlNodeKind.Element && relsReader.LocalName == "Relationship")
                {
                    string? id = relsReader.GetAttribute("Id");
                    string? target = relsReader.GetAttribute("Target");
                    if (id != null && target != null)
                        relMap[id] = target.StartsWith("/") ? target.Substring(1) : "xl/" + target;
                }
            }
        }

        using (var wbReader = new CheckpointableXmlReader(_stream, "xl/workbook.xml"))
        {
            while (wbReader.Read())
            {
                if (wbReader.NodeKind == XmlNodeKind.Element && wbReader.LocalName == "sheet")
                {
                    string? name = wbReader.GetAttribute("name");
                    string? rId = wbReader.GetAttribute("id");
                    rId ??= wbReader.GetAttribute("id",
                        "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

                    if (name != null && rId != null && relMap.TryGetValue(rId, out string? path))
                    {
                        _sheetNames.Add(name);
                        _sheetPaths.Add(path);
                    }
                }
            }
        }
    }

    private void LoadSharedStrings()
    {
        bool hasSST = false;
        foreach (var entry in _zip.Entries)
            if (entry.Name == "xl/sharedStrings.xml") { hasSST = true; break; }

        if (!hasSST) { _sharedStrings = Array.Empty<string>(); return; }

        var strings = new List<string>();
        using var sstReader = new CheckpointableXmlReader(_stream, "xl/sharedStrings.xml");
        while (sstReader.Read())
        {
            if (sstReader.NodeKind == XmlNodeKind.Element && sstReader.LocalName == "si")
                strings.Add(ReadSharedStringItem(sstReader));
        }
        _sharedStrings = strings.ToArray();
    }

    private static string ReadSharedStringItem(CheckpointableXmlReader reader)
    {
        var result = new System.Text.StringBuilder();
        int siDepth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeKind == XmlNodeKind.EndElement && reader.Depth == siDepth)
                break;
            if (reader.NodeKind == XmlNodeKind.Element && reader.LocalName == "t")
            {
                if (reader.Read() && reader.NodeKind == XmlNodeKind.Text)
                    result.Append(reader.Value);
            }
        }
        return result.ToString();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _reader?.Dispose();
            // Don't dispose _stream — we don't own it
            _disposed = true;
        }
    }
}
