using System.Text;

namespace FhirAugury.Parsing.Xml;

/// <summary>
/// A <see cref="TextReader"/> decorator that downgrades XML 1.1 input into a
/// character stream consumable by an XML 1.0 reader. See
/// <see cref="XmlDowngradeReader"/> for the public API.
/// </summary>
/// <remarks>
/// Applies four rules as the stream is read:
/// <list type="number">
/// <item>If the document declares <c>version="1.1"</c>, rewrite to <c>version="1.0"</c>.</item>
/// <item>Replace U+0085 and U+2028 with CR+LF (outside markup-name regions).</item>
/// <item>Strip code points outside the XML 1.0 <c>Char</c> production.</item>
/// <item>Sanitize element/attribute names; substitute <c>_</c> when empty.</item>
/// </list>
/// </remarks>
internal sealed class DowngradingTextReader : TextReader
{
    private readonly TextReader _inner;
    private readonly bool _ownsInner;

    // Output queue (UTF-16 code units to hand back via Read/Peek).
    private readonly Queue<char> _output = new();

    // Input look-ahead buffer (raw UTF-16 code units pulled from inner reader).
    private readonly List<int> _inputBuf = new();
    private int _inputHead;

    private State _state = State.Start;
    private char _attrQuote;
    private bool _disposed;

    private enum State
    {
        Start,
        OutsideMarkup,
        InTagBody,         // inside <foo ...> after element name, between attributes
        AfterAttrName,     // just emitted an attribute name; expecting '=' or whitespace then '='
        BeforeAttrValue,   // saw '='; expecting opening quote
        InAttrValue,       // between matching quotes; rules 2+3 only
        InPiBody,          // inside <?target ... ?> after target
        InComment,         // inside <!-- ... -->
        InCData,           // inside <![CDATA[ ... ]]>
        InDoctype,         // inside <!DOCTYPE ...> (tracks [..] subset depth)
    }

    private int _doctypeBracketDepth;

    public DowngradingTextReader(TextReader inner, bool ownsInner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ownsInner = ownsInner;
    }

    public override int Read()
    {
        Fill();
        return _output.Count > 0 ? _output.Dequeue() : -1;
    }

    public override int Peek()
    {
        Fill();
        return _output.Count > 0 ? _output.Peek() : -1;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (index < 0 || count < 0 || buffer.Length - index < count)
            throw new ArgumentOutOfRangeException();

        int written = 0;
        while (written < count)
        {
            int c = Read();
            if (c == -1) break;
            buffer[index + written] = (char)c;
            written++;
        }
        return written;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && _ownsInner)
                _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    // ────────────────────────── Pump ──────────────────────────

    private void Fill()
    {
        while (_output.Count == 0)
        {
            if (!Pump()) return;
        }
    }

    private bool Pump()
    {
        if (_state == State.Start)
        {
            HandleStart();
            return _output.Count > 0 || PeekRaw(0) != -1;
        }

        switch (_state)
        {
            case State.OutsideMarkup:
                return PumpOutsideMarkup();
            case State.InTagBody:
                return PumpInTagBody();
            case State.AfterAttrName:
                return PumpAfterAttrName();
            case State.BeforeAttrValue:
                return PumpBeforeAttrValue();
            case State.InAttrValue:
                return PumpInAttrValue();
            case State.InPiBody:
                return PumpUntil("?>", State.OutsideMarkup, includeTerminator: true);
            case State.InComment:
                return PumpUntil("-->", State.OutsideMarkup, includeTerminator: true);
            case State.InCData:
                return PumpUntil("]]>", State.OutsideMarkup, includeTerminator: true);
            case State.InDoctype:
                return PumpDoctype();
        }
        return false;
    }

    // ────────────────────────── Start / Declaration ──────────────────────────

    private void HandleStart()
    {
        _state = State.OutsideMarkup;

        // Look for "<?xml" at the very start (allowing nothing before).
        if (PeekRaw(0) != '<' || PeekRaw(1) != '?' ||
            PeekRaw(2) != 'x' || PeekRaw(3) != 'm' || PeekRaw(4) != 'l' ||
            !IsXmlWhitespaceCode(PeekRaw(5)))
        {
            return;
        }

        // Consume up to "?>" (cap at 2048 chars to avoid runaway).
        StringBuilder body = new(64);
        // Skip the "<?xml" prefix.
        for (int i = 0; i < 5; i++) ConsumeRaw();

        bool found = false;
        for (int i = 0; i < 2048; i++)
        {
            int a = PeekRaw(0);
            if (a == -1) break;
            if (a == '?' && PeekRaw(1) == '>')
            {
                ConsumeRaw(); ConsumeRaw();
                found = true;
                break;
            }
            body.Append((char)a);
            ConsumeRaw();
        }

        if (!found)
        {
            // Pathological: no "?>". Emit what we read literally.
            EmitLiteral("<?xml");
            EmitLiteral(body.ToString());
            return;
        }

        EmitLiteral(BuildRewrittenDeclaration(body.ToString()));
    }

    private static string BuildRewrittenDeclaration(string body)
    {
        // body is the text between "<?xml" and "?>", e.g. ' version="1.1" encoding="UTF-8"'.
        // Parse pseudo-attributes preserving order; replace version with "1.0".
        List<(string Name, char Quote, string Value)> attrs = ParsePseudoAttrs(body);

        bool hasVersion = false;
        for (int i = 0; i < attrs.Count; i++)
        {
            if (attrs[i].Name == "version")
            {
                attrs[i] = ("version", attrs[i].Quote == '\'' ? '\'' : '"', "1.0");
                hasVersion = true;
            }
        }
        if (!hasVersion)
            attrs.Insert(0, ("version", '"', "1.0"));

        StringBuilder sb = new();
        sb.Append("<?xml");
        foreach ((string Name, char Quote, string Value) a in attrs)
        {
            sb.Append(' ');
            sb.Append(a.Name);
            sb.Append('=');
            sb.Append(a.Quote);
            sb.Append(a.Value);
            sb.Append(a.Quote);
        }
        sb.Append("?>");
        return sb.ToString();
    }

    private static List<(string Name, char Quote, string Value)> ParsePseudoAttrs(string body)
    {
        List<(string, char, string)> result = new();
        int i = 0;
        while (i < body.Length)
        {
            while (i < body.Length && IsXmlWhitespaceCode(body[i])) i++;
            if (i >= body.Length) break;

            int nameStart = i;
            while (i < body.Length && body[i] != '=' && !IsXmlWhitespaceCode(body[i])) i++;
            string name = body.Substring(nameStart, i - nameStart);

            while (i < body.Length && IsXmlWhitespaceCode(body[i])) i++;
            if (i >= body.Length || body[i] != '=') break;
            i++; // '='
            while (i < body.Length && IsXmlWhitespaceCode(body[i])) i++;
            if (i >= body.Length) break;

            char quote = body[i];
            if (quote != '"' && quote != '\'') break;
            i++;
            int valStart = i;
            while (i < body.Length && body[i] != quote) i++;
            string val = body.Substring(valStart, i - valStart);
            if (i < body.Length) i++; // closing quote
            result.Add((name, quote, val));
        }
        return result;
    }

    // ────────────────────────── Pump helpers per state ──────────────────────────

    private bool PumpOutsideMarkup()
    {
        int cp = ReadCodepoint();
        if (cp == -1) return false;

        if (cp == '<')
        {
            HandleTagOpen();
            return true;
        }

        EmitFiltered(cp);
        return true;
    }

    private void HandleTagOpen()
    {
        // We've consumed '<'. Decide what follows.
        int p0 = PeekRaw(0);

        if (p0 == '!' && PeekRaw(1) == '-' && PeekRaw(2) == '-')
        {
            ConsumeRaw(); ConsumeRaw(); ConsumeRaw();
            EmitLiteral("<!--");
            _state = State.InComment;
            return;
        }
        if (p0 == '!' && PeekRaw(1) == '[' && PeekRaw(2) == 'C' &&
            PeekRaw(3) == 'D' && PeekRaw(4) == 'A' && PeekRaw(5) == 'T' &&
            PeekRaw(6) == 'A' && PeekRaw(7) == '[')
        {
            for (int i = 0; i < 8; i++) ConsumeRaw();
            EmitLiteral("<![CDATA[");
            _state = State.InCData;
            return;
        }
        if (p0 == '!')
        {
            // DOCTYPE or other <!...> markup. Emit literal and switch to InDoctype.
            ConsumeRaw();
            EmitLiteral("<!");
            _state = State.InDoctype;
            _doctypeBracketDepth = 0;
            return;
        }
        if (p0 == '?')
        {
            ConsumeRaw();
            EmitLiteral("<?");
            // Sanitize the PI target name.
            EmitSanitizedName();
            _state = State.InPiBody;
            return;
        }
        if (p0 == '/')
        {
            ConsumeRaw();
            EmitLiteral("</");
            EmitSanitizedName();
            // Skip to '>' inside the end tag, allowing whitespace.
            _state = State.InTagBody;
            return;
        }

        // Start tag.
        Emit('<');
        EmitSanitizedName();
        _state = State.InTagBody;
    }

    private bool PumpInTagBody()
    {
        int cp = ReadCodepoint();
        if (cp == -1) return false;

        if (cp == '>')
        {
            Emit('>');
            _state = State.OutsideMarkup;
            return true;
        }
        if (cp == '/')
        {
            // self-close?
            if (PeekRaw(0) == '>')
            {
                ConsumeRaw();
                EmitLiteral("/>");
                _state = State.OutsideMarkup;
                return true;
            }
            // stray '/'; emit and continue
            Emit('/');
            return true;
        }
        if (IsXmlWhitespaceCode(cp))
        {
            Emit((char)cp);
            return true;
        }
        if (XmlNameChars.IsNameStartChar(cp) || cp == ':' || cp == '_')
        {
            // Attribute name. Push back the codepoint so EmitSanitizedName can read it.
            PushBackCodepoint(cp);
            EmitSanitizedName();
            _state = State.AfterAttrName;
            return true;
        }
        // Illegal char in tag body; drop it.
        return true;
    }

    private bool PumpAfterAttrName()
    {
        int cp = ReadCodepoint();
        if (cp == -1) return false;

        if (IsXmlWhitespaceCode(cp))
        {
            Emit((char)cp);
            return true;
        }
        if (cp == '=')
        {
            Emit('=');
            _state = State.BeforeAttrValue;
            return true;
        }
        // Bare attribute (no value) — treat as attribute body. Push back and resume tag body.
        PushBackCodepoint(cp);
        _state = State.InTagBody;
        return true;
    }

    private bool PumpBeforeAttrValue()
    {
        int cp = ReadCodepoint();
        if (cp == -1) return false;

        if (IsXmlWhitespaceCode(cp))
        {
            Emit((char)cp);
            return true;
        }
        if (cp == '"' || cp == '\'')
        {
            _attrQuote = (char)cp;
            Emit((char)cp);
            _state = State.InAttrValue;
            return true;
        }
        // Malformed; push back and treat as if value started without quote (give up to tag body).
        PushBackCodepoint(cp);
        _state = State.InTagBody;
        return true;
    }

    private bool PumpInAttrValue()
    {
        int cp = ReadCodepoint();
        if (cp == -1) return false;

        if (cp == _attrQuote)
        {
            Emit(_attrQuote);
            _state = State.InTagBody;
            return true;
        }
        EmitFiltered(cp);
        return true;
    }

    private bool PumpUntil(string terminator, State next, bool includeTerminator)
    {
        int cp = ReadCodepoint();
        if (cp == -1) return false;

        if (cp == terminator[0] && MatchesAhead(terminator, 1))
        {
            for (int i = 1; i < terminator.Length; i++) ConsumeRaw();
            if (includeTerminator) EmitLiteral(terminator);
            else Emit((char)cp);
            _state = next;
            return true;
        }
        EmitFiltered(cp);
        return true;
    }

    private bool PumpDoctype()
    {
        int cp = ReadCodepoint();
        if (cp == -1) return false;

        if (cp == '[')
        {
            _doctypeBracketDepth++;
            Emit('[');
            return true;
        }
        if (cp == ']')
        {
            if (_doctypeBracketDepth > 0) _doctypeBracketDepth--;
            Emit(']');
            return true;
        }
        if (cp == '>' && _doctypeBracketDepth == 0)
        {
            Emit('>');
            _state = State.OutsideMarkup;
            return true;
        }
        EmitFiltered(cp);
        return true;
    }

    // ────────────────────────── Name sanitization ──────────────────────────

    /// <summary>
    /// Reads a sequence of name characters from input, sanitizes per rule 4
    /// (drop chars not in NameStartChar/NameChar; substitute <c>_</c> if empty),
    /// and emits the result. Stops at the first character that is neither a
    /// NameChar nor in the input stream's name region; that terminator is
    /// pushed back into the input buffer.
    /// </summary>
    private void EmitSanitizedName()
    {
        StringBuilder name = new(16);
        bool first = true;
        while (true)
        {
            int cp = PeekCodepoint();
            if (cp == -1) break;

            // Determine whether this codepoint is part of the name region.
            // Stop at whitespace, '>', '/', '=', quotes, '?' (PI close), '<'.
            if (IsNameTerminator(cp)) break;

            ConsumeCodepoint();

            bool keep = first ? XmlNameChars.IsNameStartChar(cp)
                              : XmlNameChars.IsNameChar(cp);
            if (keep)
            {
                AppendCodepoint(name, cp);
                first = false;
            }
            // otherwise drop
        }

        if (name.Length == 0)
            name.Append('_');

        for (int i = 0; i < name.Length; i++) Emit(name[i]);
    }

    private static bool IsNameTerminator(int cp)
    {
        return cp == '>' || cp == '/' || cp == '=' || cp == '"' || cp == '\'' ||
               cp == '<' || cp == '?' || IsXmlWhitespaceCode(cp);
    }

    // ────────────────────────── Emit / filter ──────────────────────────

    private void Emit(char c) => _output.Enqueue(c);

    private void EmitLiteral(string s)
    {
        for (int i = 0; i < s.Length; i++) _output.Enqueue(s[i]);
    }

    /// <summary>
    /// Emits a codepoint after applying rules 2 (line-ending normalization) and
    /// 3 (illegal-char stripping). Used for content regions (text, attribute
    /// value, comment, CDATA, PI body, DOCTYPE).
    /// </summary>
    private void EmitFiltered(int cp)
    {
        // Rule 2: NEL / LS → CRLF.
        if (cp == 0x0085 || cp == 0x2028)
        {
            _output.Enqueue('\r');
            _output.Enqueue('\n');
            return;
        }
        // Rule 3: drop if not a valid XML 1.0 Char.
        if (!XmlNameChars.IsXml10Char(cp)) return;

        AppendCodepointToQueue(cp);
    }

    private void AppendCodepointToQueue(int cp)
    {
        if (cp <= 0xFFFF)
        {
            _output.Enqueue((char)cp);
        }
        else
        {
            int v = cp - 0x10000;
            _output.Enqueue((char)(0xD800 | (v >> 10)));
            _output.Enqueue((char)(0xDC00 | (v & 0x3FF)));
        }
    }

    private static void AppendCodepoint(StringBuilder sb, int cp)
    {
        if (cp <= 0xFFFF) sb.Append((char)cp);
        else
        {
            int v = cp - 0x10000;
            sb.Append((char)(0xD800 | (v >> 10)));
            sb.Append((char)(0xDC00 | (v & 0x3FF)));
        }
    }

    // ────────────────────────── Codepoint I/O ──────────────────────────

    /// <summary>Reads one Unicode codepoint, consuming surrogate pairs.</summary>
    private int ReadCodepoint()
    {
        int c = ConsumeRaw();
        if (c == -1) return -1;
        if (char.IsHighSurrogate((char)c))
        {
            int next = PeekRaw(0);
            if (next != -1 && char.IsLowSurrogate((char)next))
            {
                ConsumeRaw();
                return char.ConvertToUtf32((char)c, (char)next);
            }
            // Lone high surrogate; return as-is so EmitFiltered drops it.
            return c;
        }
        return c;
    }

    /// <summary>Peeks one Unicode codepoint without consuming it.</summary>
    private int PeekCodepoint()
    {
        int c = PeekRaw(0);
        if (c == -1) return -1;
        if (char.IsHighSurrogate((char)c))
        {
            int next = PeekRaw(1);
            if (next != -1 && char.IsLowSurrogate((char)next))
                return char.ConvertToUtf32((char)c, (char)next);
        }
        return c;
    }

    /// <summary>Consumes one Unicode codepoint (the one returned by <see cref="PeekCodepoint"/>).</summary>
    private void ConsumeCodepoint()
    {
        int c = ConsumeRaw();
        if (c != -1 && char.IsHighSurrogate((char)c))
        {
            int next = PeekRaw(0);
            if (next != -1 && char.IsLowSurrogate((char)next)) ConsumeRaw();
        }
    }

    /// <summary>Pushes a codepoint back onto the front of the input buffer.</summary>
    private void PushBackCodepoint(int cp)
    {
        if (cp <= 0xFFFF)
        {
            _inputBuf.Insert(_inputHead, cp);
        }
        else
        {
            int v = cp - 0x10000;
            _inputBuf.Insert(_inputHead, 0xD800 | (v >> 10));
            _inputBuf.Insert(_inputHead + 1, 0xDC00 | (v & 0x3FF));
        }
    }

    private int PeekRaw(int offset)
    {
        while (_inputBuf.Count - _inputHead <= offset)
        {
            int x = _inner.Read();
            if (x == -1) return -1;
            _inputBuf.Add(x);
        }
        return _inputBuf[_inputHead + offset];
    }

    private int ConsumeRaw()
    {
        int v = PeekRaw(0);
        if (v == -1) return -1;
        _inputHead++;
        // Periodic compaction.
        if (_inputHead > 256)
        {
            _inputBuf.RemoveRange(0, _inputHead);
            _inputHead = 0;
        }
        return v;
    }

    private bool MatchesAhead(string s, int startOffset)
    {
        for (int i = startOffset; i < s.Length; i++)
        {
            if (PeekRaw(i - startOffset) != s[i]) return false;
        }
        return true;
    }

    private static bool IsXmlWhitespaceCode(int cp) =>
        cp == 0x20 || cp == 0x09 || cp == 0x0A || cp == 0x0D;
}
