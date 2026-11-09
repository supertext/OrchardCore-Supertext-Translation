using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Supertext.OrchardCore.Translation.Sdk.V1;

/// <summary>
/// One text to translate. Plain text is escaped and its line breaks become <c>&lt;br&gt;</c>;
/// HTML goes into the document as it is (send a whole paragraph as one segment, with formatting
/// and links as inline tags: each segment is translated on its own).
/// </summary>
/// <param name="Text">The text or HTML.</param>
/// <param name="Html">Whether <paramref name="Text"/> is HTML.</param>
public sealed record Segment(string Text, bool Html = false)
{
    /// <summary>A plain-text segment.</summary>
    public static implicit operator Segment(string text) => new(text);
}

/// <summary>
/// Packs segments into the HTML document Supertext translates (one <c>&lt;div data-st-id="N"&gt;</c>
/// per segment) and reads them back from the translated document.
/// </summary>
public static partial class HtmlDocument
{
    private const string Head = "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"></head><body>\n";
    private const string Foot = "</body></html>\n";

    /// <summary>The document for <paramref name="segments"/>; the ids are the positions in the list.</summary>
    public static string Build(IReadOnlyList<Segment> segments)
    {
        var html = new StringBuilder(Head);
        for (var i = 0; i < segments.Count; i++)
        {
            html.Append("<div data-st-id=\"").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\">")
                .Append(Content(segments[i])).Append("</div>\n");
        }
        return html.Append(Foot).ToString();
    }

    /// <summary>
    /// Reads the translated segments back, one result per segment (<c>null</c> = missing).
    /// Only the <see cref="Segment.Html"/> flag of <paramref name="segments"/> is used.
    /// </summary>
    public static IReadOnlyList<string?> Parse(string html, IReadOnlyList<Segment> segments)
    {
        var result = new string?[segments.Count];
        foreach (var (id, text) in ParseSparse(html, segments.Count, id => segments[id].Html))
        {
            result[id] = text;
        }
        return result;
    }

    /// <summary>
    /// <see cref="Parse"/> for <paramref name="count"/> segments without allocating one entry per segment:
    /// only the ids found are in the result.
    /// </summary>
    internal static Dictionary<int, string> ParseSparse(string html, int count, Func<int, bool> isHtml)
    {
        var result = new Dictionary<int, string>();
        var position = 0;
        while (position < html.Length)
        {
            var start = StartTag().Match(html, position);
            if (!start.Success)
            {
                break;
            }
            var contentStart = start.Index + start.Length;
            var end = FindClosingTag(html, start.Groups[1].Value, contentStart, out var after);
            if (end < 0)
            {
                // No closing tag: skip this start tag and keep looking.
                position = contentStart;
                continue;
            }
            var idText = start.Groups[2].Success ? start.Groups[2].Value
                : start.Groups[3].Success ? start.Groups[3].Value : start.Groups[4].Value;
            // Ids out of range (or too long to be an int) and repeated ids are ignored; the first wins.
            if (int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && id < count && !result.ContainsKey(id))
            {
                var inner = html[contentStart..end];
                result[id] = isHtml(id) ? TrimAscii(inner) : PlainText(inner);
            }
            position = after;
        }
        return result;
    }

    /// <summary>What goes between the segment's opening and closing div.</summary>
    internal static string Content(Segment segment)
    {
        if (segment.Html)
        {
            return segment.Text;
        }
        var text = segment.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var escaped = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            escaped.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                '\n' => "<br>",
                _ => c.ToString(),
            });
        }
        return escaped.ToString();
    }

    /// <summary>Counts depth over tags with the same name until the start tag is closed.</summary>
    private static int FindClosingTag(string html, string tag, int from, out int after)
    {
        var tags = new Regex("<(/?)" + Regex.Escape(tag) + @"(?=[\s>/])[^>]*?(/?)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var depth = 1;
        for (var m = tags.Match(html, from); m.Success; m = m.NextMatch())
        {
            if (m.Groups[1].Length > 0)
            {
                depth--;
            }
            else if (m.Groups[2].Length == 0)
            {
                depth++;
            }
            if (depth == 0)
            {
                after = m.Index + m.Length;
                return m.Index;
            }
        }
        after = -1;
        return -1;
    }

    private static string TrimAscii(string s) => s.Trim(' ', '\t', '\n', '\r', '\f');

    /// <summary>Plain text of a translated segment: tags removed, whitespace collapsed, entities decoded.</summary>
    internal static string PlainText(string inner)
    {
        var text = LineBreakTag().Replace(inner, "\u001e");
        text = AnyTag().Replace(text, string.Empty);
        text = AsciiWhitespace().Replace(text, " ");
        text = Marker().Replace(text, "\n");
        text = text.Trim(' ');
        return DecodeEntities(text);
    }

    /// <summary>Decodes the entities Supertext writes (numeric, amp, lt, gt, quot, apos, nbsp); others stay as they are.</summary>
    internal static string DecodeEntities(string text) => Entity().Replace(text, DecodeEntity);

    private static string DecodeEntity(Match m)
    {
        var entity = m.Groups[1].Value;
        switch (entity)
        {
            case "amp": return "&";
            case "lt": return "<";
            case "gt": return ">";
            case "quot": return "\"";
            case "apos": return "'";
            case "nbsp": return " ";
        }
        var hex = entity.Length > 1 && (entity[1] == 'x' || entity[1] == 'X');
        var ok = hex
            ? int.TryParse(entity.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n)
            : int.TryParse(entity.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out n);
        // Only valid code points; surrogates and overflowing numbers stay as they are.
        return ok && n > 0 && n <= 0x10FFFF && (n < 0xD800 || n > 0xDFFF) ? char.ConvertFromUtf32(n) : m.Value;
    }

    // [0-9] rather than \d: .NET's \d also matches non-ASCII digits.
    [GeneratedRegex(@"<([a-zA-Z][a-zA-Z0-9]*)(?=[\s>/])[^>]*?\sdata-st-id\s*=\s*(?:""([0-9]+)""|'([0-9]+)'|([0-9]+))[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex StartTag();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex("[ \t\n\r\f]+")]
    private static partial Regex AsciiWhitespace();

    [GeneratedRegex(" ?\u001e ?")]
    private static partial Regex Marker();

    [GeneratedRegex("&(#[xX][0-9a-fA-F]+|#[0-9]+|amp|lt|gt|quot|apos|nbsp);")]
    private static partial Regex Entity();
}
