using System.Text;
using Iso20022Kit.Internal;

namespace Iso20022Kit.Mt;

/// <summary>One field of an MT message's text block: a tag such as "32A" and its value (lines joined with \n).</summary>
public sealed record MtField(string Tag, string Value, int Line)
{
    /// <summary>The value's lines.</summary>
    public IReadOnlyList<string> Lines => Value.Split('\n');
}

/// <summary>A problem found in an MT message, with the line it's on when known.</summary>
public sealed record MtIssue(string Code, string Message, int? Line = null, bool IsError = true);

/// <summary>A SWIFT FIN message, split into its blocks. Only what translation needs is kept.</summary>
public sealed record FinMessage(
    string? SenderBic,
    string? ReceiverBic,
    string? MessageType,
    bool IsInput,
    IReadOnlyDictionary<string, string> UserHeader,
    IReadOnlyList<MtField> Fields)
{
    /// <summary>The longest message read, in characters. A FIN message's text block holds at most 10,000.</summary>
    public const int MaxLength = 20_000;

    /// <summary>
    /// Reads a FIN message: {1:…}{2:…}{3:{121:…}}{4:\r\n:20:…\r\n-}{5:…}. Blocks 1 to 3 and 5 are optional, so a bare
    /// text block (":20:…" lines) is accepted too. Problems are added to <paramref name="issues"/>; null is returned when
    /// the text can't be read at all.
    /// </summary>
    public static FinMessage? Parse(string text, List<MtIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(issues);
        if (text.Length > MaxLength)
        {
            issues.Add(new("MT-TOO-LONG", $"The message is {text.Length:N0} characters long; an MT message is at most {MaxLength:N0}."));
            return null;
        }
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ', '\uFEFF');
        if (text.Length == 0) { issues.Add(new("MT-EMPTY", "There is nothing to read.")); return null; }

        string? block1 = null, block2 = null, block3 = null, block4;
        if (text.StartsWith('{'))
        {
            var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
            var pos = 0;
            while (pos < text.Length)
            {
                if (text[pos] is '\n' or ' ') { pos++; continue; }
                if (text[pos] != '{' || pos + 3 > text.Length || text[pos + 2] != ':' || !char.IsAsciiDigit(text[pos + 1]))
                {
                    issues.Add(new("MT-BLOCKS", $"Expected a block such as {{4: at character {pos + 1}, found {SafeText.Quote(text.Substring(pos, Math.Min(10, text.Length - pos)))}.", LineOf(text, pos)));
                    return null;
                }
                var id = text[pos + 1].ToString();
                int end;
                if (id == "4")
                {
                    end = text.IndexOf("\n-}", pos, StringComparison.Ordinal);
                    if (end < 0) { issues.Add(new("MT-BLOCKS", "The text block {4: isn't closed by a line \"-}\".", LineOf(text, pos))); return null; }
                    blocks[id] = text[(pos + 3)..end];
                    pos = end + 3;
                }
                else
                {
                    // blocks 3 and 5 hold {tag:value} pairs; braces nest one level
                    var depth = 0;
                    end = -1;
                    for (var i = pos; i < text.Length; i++)
                    {
                        if (text[i] == '{') depth++;
                        else if (text[i] == '}' && --depth == 0) { end = i; break; }
                        if (depth > 2) { issues.Add(new("MT-BLOCKS", $"Block {id} nests braces too deeply.", LineOf(text, i))); return null; }
                    }
                    if (end < 0) { issues.Add(new("MT-BLOCKS", $"Block {id} isn't closed.", LineOf(text, pos))); return null; }
                    blocks[id] = text[(pos + 3)..end];
                    pos = end + 1;
                }
                if (blocks.Count > 5) { issues.Add(new("MT-BLOCKS", "A FIN message has at most five blocks.")); return null; }
            }
            blocks.TryGetValue("1", out block1);
            blocks.TryGetValue("2", out block2);
            blocks.TryGetValue("3", out block3);
            if (!blocks.TryGetValue("4", out var b4)) { issues.Add(new("MT-BLOCKS", "The message has no text block {4:.")); return null; }
            block4 = b4;
        }
        else block4 = "\n" + (text.EndsWith("\n-", StringComparison.Ordinal) ? text[..^2] : text);

        // block 1: F01 + logical terminal (BIC8 + terminal code + branch) + session and sequence
        string? sender = null, receiver = null, type = null;
        var input = true;
        if (block1 is not null)
        {
            if (block1.Length >= 15 && block1.StartsWith("F01", StringComparison.Ordinal)) sender = Bic(block1.Substring(3, 12));
            else issues.Add(new("MT-HEADER", "The basic header {1: doesn't start with F01 and a 12-character address.", IsError: false));
            if (sender is not null && !sender.All(char.IsAsciiLetterOrDigit)) { issues.Add(new("MT-HEADER", "The sender's address isn't a BIC.")); sender = null; }
        }
        if (block2 is not null)
        {
            if (block2.Length >= 16 && block2[0] == 'I') { type = block2.Substring(1, 3); receiver = Bic(block2.Substring(4, 12)); }
            else if (block2.Length >= 28 && block2[0] == 'O') { input = false; type = block2.Substring(1, 3); sender = Bic(block2.Substring(14, 12)); }
            else issues.Add(new("MT-HEADER", "The application header {2: isn't an input (I…) or output (O…) header.", IsError: false));
            if (type is not null && !type.All(char.IsAsciiDigit)) { issues.Add(new("MT-HEADER", $"The message type {SafeText.Quote(type)} isn't three digits.")); type = null; }
            if (sender is not null && !sender.All(char.IsAsciiLetterOrDigit)) { issues.Add(new("MT-HEADER", "The sender's address isn't a BIC.")); sender = null; }
            if (receiver is not null && !receiver.All(char.IsAsciiLetterOrDigit)) { issues.Add(new("MT-HEADER", "The receiver's address isn't a BIC.")); receiver = null; }
        }
        var user = new Dictionary<string, string>(StringComparer.Ordinal);
        if (block3 is not null)
        {
            var i = 0;
            while (i < block3.Length)
            {
                var open = block3.IndexOf('{', i);
                if (open < 0) break;
                var colon = block3.IndexOf(':', open);
                var close = block3.IndexOf('}', open);
                if (colon < 0 || close < 0 || colon > close) { issues.Add(new("MT-HEADER", "The user header {3: isn't made of {tag:value} fields.", IsError: false)); break; }
                user[block3[(open + 1)..colon]] = block3[(colon + 1)..close];
                i = close + 1;
            }
        }

        // block 4: fields start with a line ":TAG:"
        var fields = new List<MtField>();
        var block4Line = block1 is null ? 1 : LineOf(text, text.IndexOf("{4:", StringComparison.Ordinal)) + 1;
        var lines = block4.Split('\n');
        string? tag = null;
        var value = new StringBuilder();
        var start = 0;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            if (n == 0 && line.Length == 0) continue;
            if (line.Length > 0 && line[0] == ':' && line.IndexOf(':', 1) is int c && c is > 1 and <= 4)
            {
                if (tag is not null) fields.Add(new MtField(tag, value.ToString(), start));
                tag = line[1..c];
                if (!(tag.Length is 2 or 3 && char.IsAsciiDigit(tag[0]) && char.IsAsciiDigit(tag[1]) && (tag.Length == 2 || char.IsAsciiLetterUpper(tag[2]))))
                {
                    issues.Add(new("MT-FIELD", $"{SafeText.Quote(tag)} isn't a field tag (two digits and an optional capital letter, such as 32A).", block4Line + n - 1));
                    return null;
                }
                value.Clear().Append(line[(c + 1)..]);
                start = block4Line + n - 1;
            }
            else if (tag is null)
            {
                issues.Add(new("MT-FIELD", $"Text before the first field: {SafeText.Quote(line)}.", block4Line + n - 1));
                return null;
            }
            else value.Append('\n').Append(line);
            if (fields.Count > 200) { issues.Add(new("MT-FIELD", "The message has more than 200 fields.")); return null; }
        }
        if (tag is not null) fields.Add(new MtField(tag, value.ToString(), start));
        if (block1 is null && block2 is null) issues.Add(new("MT-HEADER", "Only the text block was given, so the sending and receiving banks are unknown.", IsError: false));
        return new FinMessage(sender, receiver, type, input, user, fields);
    }

    /// <summary>A logical terminal address (BIC8 + terminal code + branch) as a BIC: BIC8, plus the branch unless it is XXX.</summary>
    private static string Bic(string lt) => lt.Length < 12 ? lt : lt[..8] + (lt.Substring(9, 3) == "XXX" ? "" : lt.Substring(9, 3));

    private static int LineOf(string text, int pos)
    {
        var line = 1;
        for (var i = 0; i < pos && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }
}
