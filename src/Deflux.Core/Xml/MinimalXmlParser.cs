using System;
using System.Collections.Generic;
using System.Text;
using Deflux.Core.Exceptions;

namespace Deflux.Core.Xml;

// ── Parse state enum ──

internal enum ParseState
{
    Initial,
    BeforeMarkup,
    InElement,          // kept for compat — unused in new code
    InEndTag,           // kept for compat — unused in new code
    InAttributeName,    // kept for compat — unused in new code
    InAttributeValue,   // kept for compat — unused in new code
    InText,
    InCData,
    InComment,
    InProcessingInstruction,
    InXmlDeclaration,
    Done,

    // ── New granular states for cross-chunk persistence ──
    AfterLessThan,          // consumed '<', need to determine what follows
    InStartTagName,         // reading element name after '<'
    InStartTagBody,         // inside start tag, reading attrs / waiting for '>' or '/>'
    InEndTagName,           // reading end tag name after '</'
    InBangMarkup,           // consumed '<!', determining comment/cdata/doctype
    InCommentDash1,         // consumed '<!-', need second '-'
    InCommentBody,          // inside comment, scanning for '-->'
    InCDataHeader,          // reading '[CDATA[' header
    InCDataBody,            // inside CDATA, scanning for ']]>'
    InPIName,               // reading PI target name after '<?'
    InPIBody,               // inside PI, scanning for '?>'
    InAttrName,             // reading attribute name
    InAttrEquals,           // between attr name and '='
    InAttrValueStart,       // consumed '=', optional whitespace, need quote
    InAttrValue,            // reading attribute value inside quotes
    InAttrEntityRef,        // inside '&...;' within attribute value
    InTextEntityRef,        // inside '&...;' within text content
    InEndTagTrailing,       // consumed end tag name, reading trailing ws / '>'
    InStartTagSlash,        // consumed '/' inside start tag, expecting '>'
}

// ── Supporting types ──

internal struct ElementFrame
{
    public string LocalName;
    public string? Prefix;
    public string? NamespaceUri;
    public int Depth;
}

internal struct NamespaceBinding
{
    public string Prefix; // "" for default namespace
    public string Uri;
    public int ScopeDepth;
}

internal struct AttributeEntry
{
    public string LocalName;
    public string? Prefix;
    public string? NamespaceUri;
    public string Value;
}

/// <summary>
/// Minimal feed-model XML parser. Caller feeds raw bytes via Feed(),
/// retrieves events via Read(). Supports elements, attributes, namespaces,
/// text, CDATA, character entities, XML declaration (skip), comments (skip), PI (skip).
/// DOCTYPE is forbidden -> XmlParseException.
/// UTF-8 only.
///
/// CRITICAL: every parse sub-routine that might run out of chars sets State
/// to the correct granular ParseState BEFORE returning false, so that the
/// next Read() after Feed() resumes exactly where we left off.
/// </summary>
internal class MinimalXmlParser
{
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _nameBuffer = new();
    private readonly StringBuilder _valueBuffer = new();
    private readonly List<ElementFrame> _elementStack = new();
    private readonly List<NamespaceBinding> _namespaceBindings = new();
    private readonly List<AttributeEntry> _attributes = new();

    // Feed buffer — raw bytes not yet consumed
    private byte[] _feedBuffer = new byte[16384];
    private int _feedStart;
    private int _feedEnd;

    // Incomplete UTF-8 sequence
    private readonly byte[] _incompleteUtf8 = new byte[4];
    private int _incompleteUtf8Len;

    // Decoded char buffer (growable)
    private char[] _charBuffer = new char[16384];
    private int _charStart;
    private int _charEnd;

    // Parser state
    internal ParseState State = ParseState.Initial;
    private int _depth;
    private bool _bomSkipped;
    private bool _xmlDeclParsed;
    private int _lineNumber = 1;
    private int _columnNumber = 1;

    // Current node output
    private XmlNodeKind _nodeKind = XmlNodeKind.None;
    private string? _localName;
    private string? _prefix;
    private string? _namespaceUri;
    private string? _value;
    private int _nodeDepth;
    private bool _isEmptyElement;
    private bool _emitEndForEmpty;

    // Persistent intermediate state for cross-chunk parsing
    private string? _pendingTagPrefix;
    private string? _pendingTagLocalName;
    private char _attrQuoteChar;
    private int _commentDashCount;
    private int _cdataBracketCount;
    private int _cdataHeaderIndex; // how many chars of "[CDATA[" consumed
    private readonly StringBuilder _entityBuffer = new();

    // ── Public API ──

    public XmlNodeKind NodeKind => _nodeKind;
    public string LocalName => _localName ?? "";
    public string? Prefix => _prefix;
    public string? NamespaceUri => _namespaceUri;
    public string? Value => _value;
    public int Depth => _nodeDepth;
    public int AttributeCount => _attributes.Count;
    public int LineNumber => _lineNumber;
    public int ColumnNumber => _columnNumber;
    public bool IsEof => State == ParseState.Done;

    public string? GetAttribute(string localName)
    {
        for (int i = 0; i < _attributes.Count; i++)
            if (_attributes[i].LocalName == localName)
                return _attributes[i].Value;
        return null;
    }

    public string? GetAttribute(string localName, string ns)
    {
        for (int i = 0; i < _attributes.Count; i++)
            if (_attributes[i].LocalName == localName && _attributes[i].NamespaceUri == ns)
                return _attributes[i].Value;
        return null;
    }

    /// <summary>
    /// Feed raw bytes (UTF-8) to the parser.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        // Ensure feed buffer capacity
        int needed = _feedEnd - _feedStart + data.Length;
        if (needed > _feedBuffer.Length)
        {
            int newSize = Math.Max(_feedBuffer.Length * 2, needed);
            byte[] newBuf = new byte[newSize];
            int existing = _feedEnd - _feedStart;
            if (existing > 0)
                Array.Copy(_feedBuffer, _feedStart, newBuf, 0, existing);
            _feedBuffer = newBuf;
            _feedStart = 0;
            _feedEnd = existing;
        }
        else if (_feedStart > 0)
        {
            int existing = _feedEnd - _feedStart;
            Array.Copy(_feedBuffer, _feedStart, _feedBuffer, 0, existing);
            _feedStart = 0;
            _feedEnd = existing;
        }

        data.CopyTo(_feedBuffer.AsSpan(_feedEnd));
        _feedEnd += data.Length;

        DecodeUtf8();
    }

    /// <summary>
    /// Signal that no more data will be fed.
    /// </summary>
    public void FeedEof()
    {
        if (_incompleteUtf8Len > 0)
            throw new XmlParseException("Incomplete UTF-8 sequence at end of input");

        // Allow InText at EOF — flush remaining text as a Text node
        if (State == ParseState.InText && _buffer.Length > 0)
        {
            // There's pending text — it will be flushed on the next Read()
            // Mark as Done after that flush
        }
        else if (State != ParseState.Done && State != ParseState.BeforeMarkup && State != ParseState.Initial)
        {
            throw new XmlParseException($"Unexpected end of input in state {State}");
        }

        if (State != ParseState.InText)
            State = ParseState.Done;
    }

    /// <summary>
    /// Attempt to read the next XML node. Returns true if a node was read.
    /// Returns false if more data is needed (call Feed()) or if EOF.
    /// </summary>
    public bool Read()
    {
        // Emit synthetic EndElement for empty elements (e.g. <foo/>)
        if (_emitEndForEmpty)
        {
            _emitEndForEmpty = false;
            _depth--;
            _nodeKind = XmlNodeKind.EndElement;
            _nodeDepth = _depth;
            // _localName, _prefix, _namespaceUri already set from the Element
            _value = null;
            _attributes.Clear();
            PopNamespaceScope(_depth);
            _elementStack.RemoveAt(_elementStack.Count - 1);
            return true;
        }

        _nodeKind = XmlNodeKind.None;
        _localName = null;
        _prefix = null;
        _namespaceUri = null;
        _value = null;
        // Note: do NOT clear _attributes here — they may be partially
        // built from a previous Read() that returned false mid-tag.
        // _attributes is cleared at the start of each new tag in ParseAfterLessThan.

        while (true)
        {
            if (State == ParseState.Done)
                return false;

            if (State == ParseState.Initial)
            {
                SkipBom();
                if (!_bomSkipped)
                    return false; // need more data before we can skip BOM
                State = ParseState.BeforeMarkup;
            }

            if (!TryParseNext())
                return false;

            if (_nodeKind != XmlNodeKind.None)
                return true;
        }
    }

    // ── UTF-8 decoding ──

    private void DecodeUtf8()
    {
        int i = _feedStart;
        int end = _feedEnd;
        int charPos = _charEnd;

        // Ensure char buffer capacity
        int maxChars = end - i + _incompleteUtf8Len;
        int existing = _charEnd - _charStart;
        if (charPos + maxChars > _charBuffer.Length)
        {
            // Try compact first
            if (_charStart > 0)
            {
                Array.Copy(_charBuffer, _charStart, _charBuffer, 0, existing);
                _charStart = 0;
                _charEnd = existing;
                charPos = existing;
            }

            // If still not enough, grow
            if (charPos + maxChars > _charBuffer.Length)
            {
                int newSize = Math.Max(_charBuffer.Length * 2, existing + maxChars);
                char[] newBuf = new char[newSize];
                Array.Copy(_charBuffer, _charStart, newBuf, 0, existing);
                _charBuffer = newBuf;
                _charStart = 0;
                _charEnd = existing;
                charPos = existing;
            }
        }

        // Handle incomplete UTF-8 from previous feed
        if (_incompleteUtf8Len > 0 && i < end)
        {
            while (_incompleteUtf8Len < 4 && i < end)
            {
                byte b = _feedBuffer[i];
                if ((b & 0xC0) != 0x80)
                    break; // Not a continuation byte
                _incompleteUtf8[_incompleteUtf8Len++] = b;
                i++;

                int seqLen = GetUtf8SeqLen(_incompleteUtf8[0]);
                if (_incompleteUtf8Len == seqLen)
                {
                    int cp = DecodeUtf8Codepoint(_incompleteUtf8, _incompleteUtf8Len);
                    charPos = AppendCodepoint(cp, charPos);
                    _incompleteUtf8Len = 0;
                    break;
                }
            }
        }

        while (i < end)
        {
            byte b = _feedBuffer[i];
            if (b < 0x80)
            {
                _charBuffer[charPos++] = (char)b;
                i++;
            }
            else
            {
                int seqLen = GetUtf8SeqLen(b);
                if (i + seqLen > end)
                {
                    // Incomplete — save for next feed
                    _incompleteUtf8Len = end - i;
                    Array.Copy(_feedBuffer, i, _incompleteUtf8, 0, _incompleteUtf8Len);
                    i = end;
                    break;
                }
                int cp = DecodeUtf8Codepoint(_feedBuffer, i, seqLen);
                charPos = AppendCodepoint(cp, charPos);
                i += seqLen;
            }
        }

        _feedStart = i;
        _charEnd = charPos;
    }

    private int AppendCodepoint(int cp, int charPos)
    {
        if (cp <= 0xFFFF)
        {
            _charBuffer[charPos++] = (char)cp;
        }
        else
        {
            // Surrogate pair
            cp -= 0x10000;
            _charBuffer[charPos++] = (char)(0xD800 + (cp >> 10));
            _charBuffer[charPos++] = (char)(0xDC00 + (cp & 0x3FF));
        }
        return charPos;
    }

    private static int GetUtf8SeqLen(byte first)
    {
        if ((first & 0x80) == 0) return 1;
        if ((first & 0xE0) == 0xC0) return 2;
        if ((first & 0xF0) == 0xE0) return 3;
        if ((first & 0xF8) == 0xF0) return 4;
        return 1; // Invalid — will produce replacement
    }

    private static int DecodeUtf8Codepoint(byte[] buf, int len)
    {
        return DecodeUtf8Codepoint(buf, 0, len);
    }

    private static int DecodeUtf8Codepoint(byte[] buf, int offset, int len)
    {
        return len switch
        {
            2 => ((buf[offset] & 0x1F) << 6) | (buf[offset + 1] & 0x3F),
            3 => ((buf[offset] & 0x0F) << 12) | ((buf[offset + 1] & 0x3F) << 6) | (buf[offset + 2] & 0x3F),
            4 => ((buf[offset] & 0x07) << 18) | ((buf[offset + 1] & 0x3F) << 12) |
                 ((buf[offset + 2] & 0x3F) << 6) | (buf[offset + 3] & 0x3F),
            _ => 0xFFFD // replacement
        };
    }

    // ── BOM ──

    private void SkipBom()
    {
        if (_bomSkipped) return;
        if (_charEnd - _charStart < 1) return; // no chars yet — defer
        _bomSkipped = true;
        if (_charBuffer[_charStart] == '\uFEFF')
            _charStart++;
    }

    // ── Char helpers ──

    private int CharsAvailable => _charEnd - _charStart;

    private char PeekChar() => _charBuffer[_charStart];

    private char ReadChar()
    {
        char c = _charBuffer[_charStart++];
        if (c == '\n') { _lineNumber++; _columnNumber = 1; }
        else _columnNumber++;
        return c;
    }

    private bool TryPeekChar(out char c)
    {
        if (_charStart < _charEnd) { c = _charBuffer[_charStart]; return true; }
        c = default;
        return false;
    }

    private static bool IsNameChar(char c)
    {
        return c != ' ' && c != '\t' && c != '\r' && c != '\n' &&
               c != '/' && c != '>' && c != '=' && c != '<';
    }

    private static bool IsWhitespace(char c)
    {
        return c == ' ' || c == '\t' || c == '\r' || c == '\n';
    }

    // ── Main parse dispatch ──

    private bool TryParseNext()
    {
        if (CharsAvailable == 0) return false;

        switch (State)
        {
            case ParseState.BeforeMarkup:
                return ParseBeforeMarkup();
            case ParseState.InText:
                return ParseText();
            case ParseState.InTextEntityRef:
                return ParseTextEntityRef();
            case ParseState.AfterLessThan:
                return ParseAfterLessThan();
            case ParseState.InStartTagName:
                return ParseStartTagName();
            case ParseState.InStartTagBody:
                return ParseStartTagBody();
            case ParseState.InStartTagSlash:
                return ParseStartTagSlash();
            case ParseState.InEndTagName:
                return ParseEndTagName();
            case ParseState.InEndTagTrailing:
                return ParseEndTagTrailing();
            case ParseState.InAttrName:
                return ParseAttrName();
            case ParseState.InAttrEquals:
                return ParseAttrEquals();
            case ParseState.InAttrValueStart:
                return ParseAttrValueStart();
            case ParseState.InAttrValue:
                return ParseAttrValue();
            case ParseState.InAttrEntityRef:
                return ParseAttrEntityRef();
            case ParseState.InBangMarkup:
                return ParseBangMarkup();
            case ParseState.InCommentDash1:
                return ParseCommentDash1();
            case ParseState.InCommentBody:
                return ParseCommentBody();
            case ParseState.InCDataHeader:
                return ParseCDataHeader();
            case ParseState.InCDataBody:
                return ParseCDataBody();
            case ParseState.InPIName:
                return ParsePIName();
            case ParseState.InPIBody:
                return ParsePIBody();
            default:
                return false;
        }
    }

    // ── Before Markup ──

    private bool ParseBeforeMarkup()
    {
        if (CharsAvailable == 0) return false;
        char c = PeekChar();

        if (c == '<')
        {
            ReadChar();
            State = ParseState.AfterLessThan;
            return ParseAfterLessThan();
        }
        else
        {
            // Text content
            State = ParseState.InText;
            _buffer.Clear();
            return ParseText();
        }
    }

    // ── Text ──

    private bool ParseText()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == '<')
            {
                // End of text
                string text = _buffer.ToString();
                _buffer.Clear();
                State = ParseState.BeforeMarkup;
                _nodeKind = XmlNodeKind.Text;
                _nodeDepth = _depth;
                _value = text;
                return true;
            }
            else if (c == '&')
            {
                ReadChar();
                _entityBuffer.Clear();
                State = ParseState.InTextEntityRef;
                return ParseTextEntityRef();
            }
            else
            {
                _buffer.Append(ReadChar());
            }
        }

        // If we're at EOF with pending text, flush it
        // (FeedEof sets State to InText if there's pending text)
        return false;
    }

    private bool ParseTextEntityRef()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == ';')
            {
                ReadChar();
                string entity = _entityBuffer.ToString();
                _buffer.Append(ResolveEntity(entity));
                State = ParseState.InText;
                return ParseText();
            }
            _entityBuffer.Append(ReadChar());
            if (_entityBuffer.Length > 10)
                throw new XmlParseException($"Entity reference too long: &{_entityBuffer}");
        }
        return false; // need more data, State stays InTextEntityRef
    }

    private static string ResolveEntity(string name)
    {
        if (name == "amp") return "&";
        if (name == "lt") return "<";
        if (name == "gt") return ">";
        if (name == "quot") return "\"";
        if (name == "apos") return "'";
        if (name.Length > 1 && name[0] == '#')
        {
            int cp;
            if (name[1] == 'x')
                cp = Convert.ToInt32(name.Substring(2), 16);
            else
                cp = int.Parse(name.Substring(1));
            return char.ConvertFromUtf32(cp);
        }
        throw new XmlParseException($"Unknown entity: &{name};");
    }

    // ── After '<' ──

    private bool ParseAfterLessThan()
    {
        if (CharsAvailable == 0) return false; // State already AfterLessThan
        char c = PeekChar();

        if (c == '/')
        {
            ReadChar();
            _nameBuffer.Clear();
            State = ParseState.InEndTagName;
            return ParseEndTagName();
        }
        else if (c == '?')
        {
            ReadChar();
            _nameBuffer.Clear();
            State = ParseState.InPIName;
            return ParsePIName();
        }
        else if (c == '!')
        {
            ReadChar();
            State = ParseState.InBangMarkup;
            return ParseBangMarkup();
        }
        else
        {
            // Start tag
            _nameBuffer.Clear();
            _attributes.Clear();
            State = ParseState.InStartTagName;
            return ParseStartTagName();
        }
    }

    // ── Start tag name ──

    private bool ParseStartTagName()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '/' || c == '>')
            {
                if (_nameBuffer.Length == 0)
                    throw new XmlParseException("Empty element name");
                // Tag name complete, move to body
                string fullName = _nameBuffer.ToString();
                SplitName(fullName, out _pendingTagPrefix, out _pendingTagLocalName);
                State = ParseState.InStartTagBody;
                return ParseStartTagBody();
            }
            _nameBuffer.Append(ReadChar());
        }
        return false; // need more data, State stays InStartTagName
    }

    // ── Start tag body (attributes, '>', '/>') ──

    private bool ParseStartTagBody()
    {
        while (CharsAvailable > 0)
        {
            // Skip whitespace
            while (CharsAvailable > 0 && IsWhitespace(PeekChar()))
                ReadChar();
            if (CharsAvailable == 0) return false; // State stays InStartTagBody

            char c = PeekChar();
            if (c == '>')
            {
                ReadChar();
                _isEmptyElement = false;
                return EmitStartTag(_pendingTagPrefix, _pendingTagLocalName!);
            }
            else if (c == '/')
            {
                ReadChar();
                State = ParseState.InStartTagSlash;
                return ParseStartTagSlash();
            }
            else
            {
                // Start of an attribute name
                _nameBuffer.Clear();
                State = ParseState.InAttrName;
                return ParseAttrName();
            }
        }
        return false;
    }

    private bool ParseStartTagSlash()
    {
        if (CharsAvailable == 0) return false; // State stays InStartTagSlash
        if (PeekChar() != '>') throw new XmlParseException("Expected '>' after '/'");
        ReadChar();
        _isEmptyElement = true;
        return EmitStartTag(_pendingTagPrefix, _pendingTagLocalName!);
    }

    // ── Attribute parsing ──

    private bool ParseAttrName()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == '=' || IsWhitespace(c))
            {
                if (_nameBuffer.Length == 0)
                    throw new XmlParseException("Empty attribute name");
                State = ParseState.InAttrEquals;
                return ParseAttrEquals();
            }
            if (c == '>' || c == '/')
            {
                // Safety: we hit tag close while parsing attr name.
                // This means no '=' follows — treat accumulated text as if
                // we're back in the tag body (attribute-less tag end).
                // Push name back conceptually — but since we can't unread,
                // handle the close directly.
                if (_nameBuffer.Length == 0)
                {
                    // No attr name accumulated — just go back to tag body
                    State = ParseState.InStartTagBody;
                    return ParseStartTagBody();
                }
                // Had partial attr name but hit '>' — malformed, but handle gracefully
                State = ParseState.InStartTagBody;
                return ParseStartTagBody();
            }
            _nameBuffer.Append(ReadChar());
        }
        return false; // State stays InAttrName
    }

    private bool ParseAttrEquals()
    {
        // skip whitespace before '='
        while (CharsAvailable > 0 && IsWhitespace(PeekChar()))
            ReadChar();
        if (CharsAvailable == 0) return false; // State stays InAttrEquals

        if (PeekChar() != '=')
            throw new XmlParseException($"Expected '=' in attribute, got '{PeekChar()}' (U+{(int)PeekChar():X4}) after attr name '{_nameBuffer}' in tag '{_pendingTagLocalName}' at line {_lineNumber}:{_columnNumber}");
        ReadChar();
        State = ParseState.InAttrValueStart;
        return ParseAttrValueStart();
    }

    private bool ParseAttrValueStart()
    {
        // skip whitespace after '='
        while (CharsAvailable > 0 && IsWhitespace(PeekChar()))
            ReadChar();
        if (CharsAvailable == 0) return false; // State stays InAttrValueStart

        char quote = PeekChar();
        if (quote != '"' && quote != '\'')
            throw new XmlParseException($"Expected quote in attribute value, got '{quote}'");
        ReadChar();
        _attrQuoteChar = quote;
        _valueBuffer.Clear();
        State = ParseState.InAttrValue;
        return ParseAttrValue();
    }

    private bool ParseAttrValue()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == _attrQuoteChar)
            {
                ReadChar();
                // Attribute complete
                string attrFullName = _nameBuffer.ToString();
                string attrValue = _valueBuffer.ToString();
                SplitName(attrFullName, out string? attrPrefix, out string attrLocal);
                _attributes.Add(new AttributeEntry
                {
                    LocalName = attrLocal,
                    Prefix = attrPrefix,
                    Value = attrValue
                });
                // Back to tag body for more attrs or close
                State = ParseState.InStartTagBody;
                return ParseStartTagBody();
            }
            else if (c == '&')
            {
                ReadChar();
                _entityBuffer.Clear();
                State = ParseState.InAttrEntityRef;
                return ParseAttrEntityRef();
            }
            else
            {
                _valueBuffer.Append(ReadChar());
            }
        }
        return false; // State stays InAttrValue
    }

    private bool ParseAttrEntityRef()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == ';')
            {
                ReadChar();
                _valueBuffer.Append(ResolveEntity(_entityBuffer.ToString()));
                State = ParseState.InAttrValue;
                return ParseAttrValue();
            }
            _entityBuffer.Append(ReadChar());
            if (_entityBuffer.Length > 10)
                throw new XmlParseException($"Entity reference too long: &{_entityBuffer}");
        }
        return false; // State stays InAttrEntityRef
    }

    // ── Emit start tag ──

    private bool EmitStartTag(string? prefix, string localName)
    {
        // Process namespace declarations first
        for (int i = 0; i < _attributes.Count; i++)
        {
            var attr = _attributes[i];
            if (attr.Prefix == "xmlns")
            {
                _namespaceBindings.Add(new NamespaceBinding
                {
                    Prefix = attr.LocalName,
                    Uri = attr.Value,
                    ScopeDepth = _depth
                });
            }
            else if (attr.Prefix == null && attr.LocalName == "xmlns")
            {
                _namespaceBindings.Add(new NamespaceBinding
                {
                    Prefix = "",
                    Uri = attr.Value,
                    ScopeDepth = _depth
                });
            }
        }

        // Resolve element namespace
        string? nsUri = ResolveNamespace(prefix ?? "");

        // Resolve attribute namespaces
        for (int i = 0; i < _attributes.Count; i++)
        {
            var attr = _attributes[i];
            if (attr.Prefix != null && attr.Prefix != "xmlns")
            {
                attr.NamespaceUri = ResolveNamespace(attr.Prefix);
                _attributes[i] = attr;
            }
        }

        _elementStack.Add(new ElementFrame
        {
            LocalName = localName,
            Prefix = prefix,
            NamespaceUri = nsUri,
            Depth = _depth
        });

        _nodeKind = XmlNodeKind.Element;
        _nodeDepth = _depth;
        _localName = localName;
        _prefix = prefix;
        _namespaceUri = nsUri;
        _value = null;
        _depth++;
        State = ParseState.BeforeMarkup;

        if (_isEmptyElement)
            _emitEndForEmpty = true;

        return true;
    }

    // ── End tag ──

    private bool ParseEndTagName()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == '>')
            {
                return FinishEndTag();
            }
            if (IsWhitespace(c))
            {
                // Whitespace after tag name — skip trailing
                State = ParseState.InEndTagTrailing;
                return ParseEndTagTrailing();
            }
            _nameBuffer.Append(ReadChar());
        }
        return false; // State stays InEndTagName
    }

    private bool ParseEndTagTrailing()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (IsWhitespace(c))
            {
                ReadChar();
                continue;
            }
            if (c == '>')
            {
                return FinishEndTag();
            }
            throw new XmlParseException($"Unexpected character in end tag: '{c}'");
        }
        return false; // State stays InEndTagTrailing
    }

    private bool FinishEndTag()
    {
        ReadChar(); // consume '>'

        _depth--;
        if (_elementStack.Count == 0)
            throw new XmlParseException("End tag without matching start tag");

        var frame = _elementStack[_elementStack.Count - 1];
        _elementStack.RemoveAt(_elementStack.Count - 1);

        _nodeKind = XmlNodeKind.EndElement;
        _nodeDepth = _depth;
        _localName = frame.LocalName;
        _prefix = frame.Prefix;
        _namespaceUri = frame.NamespaceUri;
        _value = null;
        State = _elementStack.Count == 0 && _depth == 0 ? ParseState.Done : ParseState.BeforeMarkup;

        PopNamespaceScope(_depth);
        return true;
    }

    // ── ! markup: comment, CDATA, DOCTYPE ──

    private bool ParseBangMarkup()
    {
        if (CharsAvailable == 0) return false; // State stays InBangMarkup
        char c = PeekChar();

        if (c == '[')
        {
            ReadChar();
            _cdataHeaderIndex = 0;
            State = ParseState.InCDataHeader;
            return ParseCDataHeader();
        }
        else if (c == '-')
        {
            ReadChar();
            State = ParseState.InCommentDash1;
            return ParseCommentDash1();
        }
        else if (c == 'D')
        {
            throw new XmlParseException("DOCTYPE is not supported");
        }
        else
        {
            throw new XmlParseException($"Unexpected character after '<!' : '{c}'");
        }
    }

    // ── Comment: <!-- ... --> ──

    private bool ParseCommentDash1()
    {
        if (CharsAvailable == 0) return false; // State stays InCommentDash1
        char c = ReadChar();
        if (c != '-')
            throw new XmlParseException("Expected '--' after '<!'");
        _commentDashCount = 0;
        State = ParseState.InCommentBody;
        return ParseCommentBody();
    }

    private bool ParseCommentBody()
    {
        while (CharsAvailable > 0)
        {
            char c = ReadChar();
            if (c == '-')
            {
                _commentDashCount++;
            }
            else if (c == '>' && _commentDashCount >= 2)
            {
                _commentDashCount = 0;
                State = ParseState.BeforeMarkup;
                return TryParseNext();
            }
            else
            {
                _commentDashCount = 0;
            }
        }
        return false; // State stays InCommentBody
    }

    // ── CDATA: <![CDATA[ ... ]]> ──

    private static readonly string CDataTag = "CDATA[";

    private bool ParseCDataHeader()
    {
        // We've already consumed '[', now need to consume "CDATA["
        while (CharsAvailable > 0 && _cdataHeaderIndex < CDataTag.Length)
        {
            char c = ReadChar();
            if (c != CDataTag[_cdataHeaderIndex])
                throw new XmlParseException($"Expected '<![CDATA[', got unexpected char '{c}'");
            _cdataHeaderIndex++;
        }
        if (_cdataHeaderIndex < CDataTag.Length)
            return false; // need more data, State stays InCDataHeader

        // Header complete, start reading body
        _buffer.Clear();
        _cdataBracketCount = 0;
        State = ParseState.InCDataBody;
        return ParseCDataBody();
    }

    private bool ParseCDataBody()
    {
        while (CharsAvailable > 0)
        {
            char c = ReadChar();
            if (c == ']')
            {
                _cdataBracketCount++;
            }
            else if (c == '>' && _cdataBracketCount >= 2)
            {
                // Remove trailing "]]" from buffer
                if (_buffer.Length >= 2)
                    _buffer.Length -= 2;
                _nodeKind = XmlNodeKind.CData;
                _nodeDepth = _depth;
                _value = _buffer.ToString();
                State = ParseState.BeforeMarkup;
                return true;
            }
            else
            {
                _cdataBracketCount = 0;
            }
            _buffer.Append(c);
        }
        return false; // State stays InCDataBody
    }

    // ── PI: <?...?> ──

    private bool ParsePIName()
    {
        while (CharsAvailable > 0)
        {
            char c = PeekChar();
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '?')
            {
                State = ParseState.InPIBody;
                return ParsePIBody();
            }
            _nameBuffer.Append(ReadChar());
        }
        return false; // State stays InPIName
    }

    private bool ParsePIBody()
    {
        while (CharsAvailable > 0)
        {
            char c = ReadChar();
            if (c == '?' && CharsAvailable > 0 && PeekChar() == '>')
            {
                ReadChar(); // '>'
                if (_nameBuffer.ToString() == "xml")
                    _xmlDeclParsed = true;
                State = ParseState.BeforeMarkup;
                return TryParseNext();
            }
        }
        return false; // State stays InPIBody
    }

    // ── Namespace resolution ──

    private string? ResolveNamespace(string prefix)
    {
        // Search backwards for most recent binding
        for (int i = _namespaceBindings.Count - 1; i >= 0; i--)
        {
            if (_namespaceBindings[i].Prefix == prefix)
                return _namespaceBindings[i].Uri;
        }
        if (prefix == "xml") return "http://www.w3.org/XML/1998/namespace";
        if (prefix == "xmlns") return "http://www.w3.org/2000/xmlns/";
        return prefix.Length == 0 ? null : null; // Unknown prefix
    }

    private void PopNamespaceScope(int depth)
    {
        while (_namespaceBindings.Count > 0 &&
               _namespaceBindings[_namespaceBindings.Count - 1].ScopeDepth >= depth)
        {
            _namespaceBindings.RemoveAt(_namespaceBindings.Count - 1);
        }
    }

    // ── Helpers ──

    private static void SplitName(string fullName, out string? prefix, out string localName)
    {
        int colon = fullName.IndexOf(':');
        if (colon >= 0)
        {
            prefix = fullName.Substring(0, colon);
            localName = fullName.Substring(colon + 1);
        }
        else
        {
            prefix = null;
            localName = fullName;
        }
    }

    // ── Checkpoint support ──

    /// <summary>
    /// Returns unconsumed chars from the char buffer, re-encoded as UTF-8.
    /// These bytes must be saved and re-fed after restore.
    /// </summary>
    internal byte[] GetUnconsumedBytes()
    {
        int unconsumedChars = _charEnd - _charStart;
        if (unconsumedChars <= 0 && _incompleteUtf8Len == 0)
            return Array.Empty<byte>();

        // Re-encode unconsumed chars back to UTF-8
        byte[] charBytes = unconsumedChars > 0
            ? System.Text.Encoding.UTF8.GetBytes(_charBuffer, _charStart, unconsumedChars)
            : Array.Empty<byte>();

        // Prepend incomplete UTF-8 bytes
        if (_incompleteUtf8Len > 0)
        {
            byte[] result = new byte[_incompleteUtf8Len + charBytes.Length];
            Array.Copy(_incompleteUtf8, 0, result, 0, _incompleteUtf8Len);
            Array.Copy(charBytes, 0, result, _incompleteUtf8Len, charBytes.Length);
            return result;
        }

        return charBytes;
    }

    internal XmlParserState SaveState()
    {
        var frames = new ElementFrame[_elementStack.Count];
        _elementStack.CopyTo(frames);

        var bindings = new NamespaceBinding[_namespaceBindings.Count];
        _namespaceBindings.CopyTo(bindings);

        byte[] incompleteCopy = new byte[_incompleteUtf8Len];
        if (_incompleteUtf8Len > 0)
            Array.Copy(_incompleteUtf8, 0, incompleteCopy, 0, _incompleteUtf8Len);

        return new XmlParserState
        {
            CurrentState = State,
            Depth = _depth,
            ElementStack = frames,
            NamespaceBindings = bindings,
            PendingText = _buffer.Length > 0 ? _buffer.ToString() : null,
            IncompleteUtf8 = incompleteCopy,
            LineNumber = _lineNumber,
            ColumnNumber = _columnNumber,
            BomSkipped = _bomSkipped,
            XmlDeclParsed = _xmlDeclParsed,
            EmitEndForEmpty = _emitEndForEmpty,
        };
    }

    internal void RestoreState(XmlParserState state)
    {
        State = state.CurrentState;
        _depth = state.Depth;

        _elementStack.Clear();
        _elementStack.AddRange(state.ElementStack);

        _namespaceBindings.Clear();
        _namespaceBindings.AddRange(state.NamespaceBindings);

        _buffer.Clear();
        if (state.PendingText != null)
            _buffer.Append(state.PendingText);

        _incompleteUtf8Len = state.IncompleteUtf8.Length;
        if (_incompleteUtf8Len > 0)
            Array.Copy(state.IncompleteUtf8, 0, _incompleteUtf8, 0, _incompleteUtf8Len);

        _lineNumber = state.LineNumber;
        _columnNumber = state.ColumnNumber;
        _bomSkipped = state.BomSkipped;
        _xmlDeclParsed = state.XmlDeclParsed;

        // Reset transient parse state
        _charStart = 0;
        _charEnd = 0;
        _feedStart = 0;
        _feedEnd = 0;
        _entityBuffer.Clear();
        _emitEndForEmpty = state.EmitEndForEmpty;
        _nodeKind = XmlNodeKind.None;
        _attributes.Clear();
    }
}

/// <summary>
/// Complete serializable state of the XML parser.
/// </summary>
internal class XmlParserState
{
    public ParseState CurrentState;
    public int Depth;
    public ElementFrame[] ElementStack = Array.Empty<ElementFrame>();
    public NamespaceBinding[] NamespaceBindings = Array.Empty<NamespaceBinding>();
    public string? PendingText;
    public byte[] IncompleteUtf8 = Array.Empty<byte>();
    public int LineNumber;
    public int ColumnNumber;
    public bool BomSkipped;
    public bool XmlDeclParsed;
    public bool EmitEndForEmpty;
}
