using System;
using System.Collections.Generic;
using System.IO;
using Deflux.Core;
using Deflux.Core.Exceptions;
using Deflux.Core.Reader;

namespace Deflux.Ods;

/// <summary>
/// Streaming ODS reader with checkpoint support.
/// Accepts a seekable stream. Does NOT own it — caller manages lifetime.
/// All sheets live in a single content.xml DEFLATE stream.
/// On first access, scans all sheet names and captures a checkpoint at each
/// sheet start — subsequent OpenSheet() calls restore via O(1) checkpoint.
/// Handles repeated rows/columns (never materializes huge repeats).
/// </summary>
public class OdsReader : IDisposable, ICheckpointable
{
    private const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    private readonly Stream _stream;
    private CheckpointableXmlReader? _reader;

    private List<SheetInfo>? _sheets;

    private bool _sheetOpened;
    private int _currentRowIndex;
    private bool _disposed;

    public OdsReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable", nameof(stream));
    }

    // ── Sheet discovery ──

    /// <summary>
    /// Single pass through content.xml: collects sheet names and saves a checkpoint
    /// at each &lt;table:table&gt; element (~40-45 KB per sheet).
    /// Result is cached — subsequent calls return the same list.
    /// Can also be called standalone to get checkpoints without opening any sheet.
    /// </summary>
    public IReadOnlyList<SheetInfo> ScanSheets(Action<int, string>? onSheetFound = null)
    {
        if (_sheets != null)
            return _sheets;

        _sheets = new List<SheetInfo>();
        using var reader = new CheckpointableXmlReader(_stream, "content.xml");

        while (reader.Read())
        {
            if (reader.NodeKind == XmlNodeKind.Element &&
                reader.LocalName == "table" &&
                reader.NamespaceUri == TableNs)
            {
                string? name = reader.GetAttribute("name", TableNs);
                name ??= reader.GetAttribute("name");

                if (name != null)
                {
                    byte[] cp = reader.SaveCheckpoint();
                    _sheets.Add(new SheetInfo(name, cp));
                    onSheetFound?.Invoke(_sheets.Count, name);
                }

                reader.SkipChildren();
            }
        }

        return _sheets;
    }

    public IReadOnlyList<string> GetSheetNames()
    {
        var sheets = ScanSheets();
        var names = new List<string>(sheets.Count);
        for (int i = 0; i < sheets.Count; i++)
            names.Add(sheets[i].Name);
        return names;
    }

    public void OpenSheet(string sheetName)
    {
        var sheets = ScanSheets();
        for (int i = 0; i < sheets.Count; i++)
        {
            if (sheets[i].Name == sheetName)
            {
                OpenSheet(i);
                return;
            }
        }
        throw new ArgumentException($"Sheet not found: {sheetName}");
    }

    public void OpenSheet(int index)
    {
        var sheets = ScanSheets();
        if (index < 0 || index >= sheets.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        // Try checkpoint restore first (O(1) — no re-decompression)
        try
        {
            _reader?.Dispose();
            _reader = new CheckpointableXmlReader(_stream, "content.xml");
            _reader.RestoreCheckpoint(sheets[index].Checkpoint);
            _sheetOpened = true;
            _currentRowIndex = 0;
            return;
        }
        catch (CheckpointMismatchException) { }
        catch (CheckpointVersionException) { }

        // Fallback: re-read from start, skip to target sheet
        _reader?.Dispose();
        _reader = new CheckpointableXmlReader(_stream, "content.xml");
        _sheetOpened = false;
        _currentRowIndex = 0;

        int tableIndex = 0;
        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.Element &&
                _reader.LocalName == "table" &&
                _reader.NamespaceUri == TableNs)
            {
                if (tableIndex == index)
                {
                    _sheetOpened = true;
                    return;
                }
                _reader.SkipChildren();
                tableIndex++;
            }
        }

        throw new ArgumentException($"Sheet index {index} not found in content.xml");
    }

    /// <summary>
    /// Read next row. Returns false at end of sheet.
    /// Empty rows with repeats are skipped automatically.
    /// </summary>
    public bool ReadRow(out Row row)
    {
        row = default;
        if (_reader == null || !_sheetOpened)
            throw new InvalidOperationException("No sheet opened. Call OpenSheet() first.");

        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.EndElement &&
                _reader.LocalName == "table" &&
                _reader.NamespaceUri == TableNs)
                return false;

            if (_reader.NodeKind == XmlNodeKind.Element &&
                _reader.LocalName == "table-row" &&
                _reader.NamespaceUri == TableNs)
            {
                int rowsRepeated = GetRepeatCount(_reader, "number-rows-repeated");
                var cells = ParseRowCells();

                if (cells.Count == 0)
                {
                    _currentRowIndex += rowsRepeated;
                    continue;
                }

                row = new Row(_currentRowIndex, cells.ToArray());
                _currentRowIndex += rowsRepeated;
                return true;
            }
        }
        return false;
    }

    // ── Checkpoint ──

    public byte[] SaveCheckpoint()
    {
        if (_reader == null)
            throw new InvalidOperationException("No sheet opened");
        return _reader.SaveCheckpoint();
    }

    public void RestoreCheckpoint(byte[] data)
    {
        _reader?.Dispose();
        _reader = new CheckpointableXmlReader(_stream, "content.xml");
        _reader.RestoreCheckpoint(data);
        _sheetOpened = true;
    }

    // ── Row cell parsing ──

    private List<Cell> ParseRowCells()
    {
        var cells = new List<Cell>();
        int colIndex = 0;
        int rowDepth = _reader!.Depth;

        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.EndElement && _reader.Depth == rowDepth)
                break;

            if (_reader.NodeKind == XmlNodeKind.Element &&
                _reader.LocalName == "table-cell" &&
                _reader.NamespaceUri == TableNs)
            {
                int colsRepeated = GetRepeatCount(_reader, "number-columns-repeated");
                var cell = ParseSingleCell(colIndex);

                if (cell.HasValue)
                {
                    for (int i = 0; i < colsRepeated; i++)
                        cells.Add(new Cell(colIndex + i, cell.Value.Value, cell.Value.Type));
                }

                colIndex += colsRepeated;
            }
            else if (_reader.NodeKind == XmlNodeKind.Element &&
                     _reader.LocalName == "covered-table-cell" &&
                     _reader.NamespaceUri == TableNs)
            {
                int colsRepeated = GetRepeatCount(_reader, "number-columns-repeated");
                colIndex += colsRepeated;
            }
        }

        return cells;
    }

    private Cell? ParseSingleCell(int colIndex)
    {
        string? valueType = _reader!.GetAttribute("value-type", OfficeNs);
        valueType ??= _reader.GetAttribute("value-type");

        string? officeValue = _reader.GetAttribute("value", OfficeNs);
        officeValue ??= _reader.GetAttribute("value");

        string? boolValue = _reader.GetAttribute("boolean-value", OfficeNs);
        boolValue ??= _reader.GetAttribute("boolean-value");

        string? dateValue = _reader.GetAttribute("date-value", OfficeNs);
        dateValue ??= _reader.GetAttribute("date-value");

        string? timeValue = _reader.GetAttribute("time-value", OfficeNs);
        timeValue ??= _reader.GetAttribute("time-value");

        string? textContent = null;
        int cellDepth = _reader.Depth;

        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.EndElement && _reader.Depth == cellDepth)
                break;

            if (_reader.NodeKind == XmlNodeKind.Element &&
                _reader.LocalName == "p" &&
                (_reader.NamespaceUri == TextNs || _reader.NamespaceUri == null))
            {
                textContent = ReadTextParagraph();
            }
        }

        if (valueType == null && textContent == null)
            return null;

        return valueType switch
        {
            "string" => new Cell(colIndex, textContent, CellType.String),
            "float" or "percentage" or "currency" => new Cell(colIndex, officeValue ?? textContent, CellType.Number),
            "date" => new Cell(colIndex, dateValue ?? textContent, CellType.String),
            "time" => new Cell(colIndex, timeValue ?? textContent, CellType.String),
            "boolean" => new Cell(colIndex, boolValue ?? textContent, CellType.Boolean),
            _ => textContent != null ? new Cell(colIndex, textContent, CellType.String) : null,
        };
    }

    private string ReadTextParagraph()
    {
        var sb = new System.Text.StringBuilder();
        int pDepth = _reader!.Depth;

        while (_reader.Read())
        {
            if (_reader.NodeKind == XmlNodeKind.EndElement && _reader.Depth == pDepth)
                break;

            if (_reader.NodeKind == XmlNodeKind.Text)
                sb.Append(_reader.Value);
        }

        return sb.ToString();
    }

    // ── Helpers ──

    private int GetRepeatCount(CheckpointableXmlReader reader, string attrName)
    {
        string? val = reader.GetAttribute(attrName, TableNs);
        val ??= reader.GetAttribute(attrName);
        if (val != null && int.TryParse(val, out int count) && count > 1)
            return count;
        return 1;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _reader?.Dispose();
            _disposed = true;
        }
    }
}
