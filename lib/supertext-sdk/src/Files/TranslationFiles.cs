using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Supertext.OrchardCore.Translation.Sdk.V1;

namespace Supertext.OrchardCore.Translation.Sdk.Files;

/// <summary>
/// Translation files: export segments as XLIFF 1.2 or HTML, translate them outside the API
/// (a CAT tool, a translator, Supertext's web app), import them again
/// (spec/translation-file.json, integration guide → Translation files).
/// </summary>
/// <remarks>
/// Not tied to an API version: nothing here talks to the API. The HTML file is the same document
/// <see cref="HtmlDocument.Build"/> makes, plus metadata in its head. Imported files are untrusted:
/// <see cref="Read"/> refuses DTDs and files over <see cref="MaxFileCharacters"/>;
/// <see cref="ImportTranslations"/> takes markup only from the integration's current source, never from the file.
/// </remarks>
public static partial class TranslationFiles
{
    /// <summary>Larger files are refused by <see cref="Read"/> (counted in UTF-16 code units, the string's length).</summary>
    public const int MaxFileCharacters = 10_000_000;

    private static readonly ConditionalWeakTable<TranslationFile, Dictionary<string, UnitData>> Internals = new();

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.Ordinal)
    {
        "href", "src", "action", "formaction", "poster", "cite", "xlink:href",
    };

    private static readonly HashSet<string> SafeSchemes = new(StringComparer.Ordinal) { "http", "https", "mailto", "tel" };

    private static TranslationFileException Fail(string code, string message) => new(code, message);

    private static bool IsBlank(string text)
    {
        foreach (var c in text)
        {
            if (c is not (' ' or '\t' or '\r' or '\n' or '\f'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// FNV-1a (32 bit) of the packed segment's UTF-8 bytes, 8 lowercase hex digits. Detects changed
    /// sources; not a security check. Only <see cref="FileSegment.Text"/> and <see cref="FileSegment.Html"/> are used.
    /// </summary>
    public static string Checksum(FileSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var hash = 0x811c9dc5u;
        foreach (var b in Encoding.UTF8.GetBytes(Pack(segment)))
        {
            hash = unchecked((hash ^ b) * 0x01000193u);
        }
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static string Pack(FileSegment segment) => HtmlDocument.Content(new Segment(segment.Text, segment.Html));

    // ---------------------------------------------------------------------------
    // HTML fragments: tokens and inline codes

    private enum TokenKind
    {
        Text,
        Start,
        End,
        Comment,
    }

    private sealed record Token(TokenKind Kind, string Raw, string Name = "", bool SelfClosing = false, List<(string Name, string? Value)>? Attrs = null);

    private sealed record Code(string Kind, int Id, string Raw);

    private static List<Token> Tokenize(string html)
    {
        var tokens = new List<Token>();
        var last = 0;
        for (var m = Tag().Match(html); m.Success; m = m.NextMatch())
        {
            if (m.Index > last)
            {
                tokens.Add(new Token(TokenKind.Text, html[last..m.Index]));
            }
            var raw = m.Value;
            if (raw.StartsWith("<!--", StringComparison.Ordinal))
            {
                tokens.Add(new Token(TokenKind.Comment, raw));
            }
            else if (m.Groups[1].Success)
            {
                tokens.Add(new Token(TokenKind.End, raw, m.Groups[1].Value.ToLowerInvariant()));
            }
            else
            {
                var attrs = new List<(string, string?)>();
                for (var a = Attr().Match(m.Groups[3].Value); a.Success; a = a.NextMatch())
                {
                    attrs.Add((a.Groups[1].Value.ToLowerInvariant(), AttrValue(a)));
                }
                tokens.Add(new Token(TokenKind.Start, raw, m.Groups[2].Value.ToLowerInvariant(), m.Groups[4].Value == "/", attrs));
            }
            last = m.Index + m.Length;
        }
        if (last < html.Length)
        {
            tokens.Add(new Token(TokenKind.Text, html[last..]));
        }
        return tokens;
    }

    private static string? AttrValue(Match a) =>
        a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Success ? a.Groups[4].Value : null;

    private static bool IsXmlChar(long cp) =>
        cp == 0x9 || cp == 0xA || cp == 0xD || (cp >= 0x20 && cp <= 0xD7FF) || (cp >= 0xE000 && cp <= 0xFFFD) || (cp >= 0x10000 && cp <= 0x10FFFF);

    /// <summary>
    /// The inline codes of an HTML segment: a start tag with its matching end tag becomes bpt/ept,
    /// everything else (void and self-closing tags, unmatched tags, comments, entities XML can't hold)
    /// a ph. Ids count from 1 in order of appearance; an ept has its bpt's id. Text has the known
    /// entities decoded. Parts are strings or <see cref="Code"/>s.
    /// </summary>
    private static List<object> SegmentParts(string html)
    {
        var tokens = Tokenize(html);
        var endOf = new Dictionary<int, int>();
        var startOf = new Dictionary<int, int>();
        var stack = new List<(string Name, int Index)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Start && !t.SelfClosing && !Void.Contains(t.Name))
            {
                stack.Add((t.Name, i));
            }
            else if (t.Kind == TokenKind.End && stack.Count > 0 && stack[^1].Name == t.Name)
            {
                var start = stack[^1].Index;
                stack.RemoveAt(stack.Count - 1);
                endOf[start] = i;
                startOf[i] = start;
            }
        }
        var parts = new List<object>();
        var ids = new Dictionary<int, int>();
        var next = 1;
        void Text(string s)
        {
            if (parts.Count > 0 && parts[^1] is string previous)
            {
                parts[^1] = previous + s;
            }
            else
            {
                parts.Add(s);
            }
        }
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Text)
            {
                var text = t.Raw;
                var last = 0;
                for (var m = Entity().Match(text); m.Success; m = m.NextMatch())
                {
                    if (m.Index > last)
                    {
                        Text(text[last..m.Index]);
                    }
                    var decoded = KnownEntity().IsMatch(m.Groups[1].Value) ? HtmlDocument.DecodeEntities(m.Value) : m.Value;
                    var cp = char.IsHighSurrogate(decoded[0]) && decoded.Length > 1 && char.IsLowSurrogate(decoded[1])
                        ? char.ConvertToUtf32(decoded[0], decoded[1])
                        : decoded[0];
                    if (decoded != m.Value && IsXmlChar(cp))
                    {
                        Text(decoded);
                    }
                    else
                    {
                        parts.Add(new Code("ph", next++, m.Value));
                    }
                    last = m.Index + m.Length;
                }
                if (last < text.Length)
                {
                    Text(text[last..]);
                }
            }
            else if (t.Kind == TokenKind.Start && endOf.ContainsKey(i))
            {
                var id = next++;
                ids[i] = id;
                parts.Add(new Code("bpt", id, t.Raw));
            }
            else if (t.Kind == TokenKind.End && startOf.TryGetValue(i, out var start))
            {
                parts.Add(new Code("ept", ids[start], t.Raw));
            }
            else
            {
                parts.Add(new Code("ph", next++, t.Raw));
            }
        }
        return parts;
    }

    // ---------------------------------------------------------------------------
    // Export

    /// <summary>XML text: characters XML 1.0 can't hold dropped, &amp; &lt; &gt; escaped, CR as &amp;#13;.</summary>
    private static string XmlText(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb.Append(c).Append(s[i + 1]);
                    i++;
                }
                continue;
            }
            if (char.IsLowSurrogate(c) || (c < 0x20 && c is not ('\t' or '\n' or '\r')) || c is '\ufffe' or '\uffff')
            {
                continue;
            }
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '\r' => "&#13;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    private static string XmlAttr(string s) => XmlText(s).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string HtmlAttr(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static bool HasControl(string s)
    {
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7F)
            {
                return true;
            }
        }
        return false;
    }

    private static List<FileSegment> CheckJob(FileJob job)
    {
        if (job is null)
        {
            throw Fail(TranslationFileErrorCodes.InvalidJob, "a job is null");
        }
        foreach (var (field, value) in new[]
        {
            ("plugin", job.Plugin), ("reference", job.Reference), ("sourceLanguage", job.SourceLanguage), ("targetLanguage", job.TargetLanguage),
        })
        {
            if (value is null || JsTrim(value).Length == 0 || HasControl(value))
            {
                throw Fail(TranslationFileErrorCodes.InvalidJob, $"{field} must be a non-empty string without control characters");
            }
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var segments = job.Segments ?? Array.Empty<FileSegment>();
        foreach (var segment in segments)
        {
            if (segment?.Key is null || segment.Key.Length == 0 || HasControl(segment.Key) || segment.Text is null)
            {
                throw Fail(TranslationFileErrorCodes.InvalidJob, "segment keys must be non-empty strings without control characters");
            }
            if (!keys.Add(segment.Key))
            {
                throw Fail(TranslationFileErrorCodes.InvalidJob, $"duplicate segment key \"{segment.Key}\"");
            }
        }
        var nonBlank = segments.Where(s => !IsBlank(s.Text)).ToList();
        if (nonBlank.Count == 0)
        {
            throw Fail(TranslationFileErrorCodes.InvalidJob, "the job has nothing to translate (all segments blank)");
        }
        return nonBlank;
    }

    private static string XliffSource(FileSegment segment)
    {
        if (!segment.Html)
        {
            return XmlText(segment.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        }
        var sb = new StringBuilder();
        foreach (var p in SegmentParts(segment.Text))
        {
            if (p is string s)
            {
                sb.Append(XmlText(s));
            }
            else
            {
                var code = (Code)p;
                sb.Append('<').Append(code.Kind).Append(" id=\"").Append(code.Id.ToString(CultureInfo.InvariantCulture)).Append("\">")
                    .Append(XmlText(code.Raw)).Append("</").Append(code.Kind).Append('>');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// One XLIFF 1.2 file with one <c>&lt;file&gt;</c> per job. Blank segments are left out; a job with only
    /// blank segments is refused (<see cref="TranslationFileErrorCodes.InvalidJob"/>).
    /// </summary>
    public static string ExportXliff(IReadOnlyList<FileJob> jobs)
    {
        if (jobs is null || jobs.Count == 0)
        {
            throw Fail(TranslationFileErrorCodes.InvalidJob, "at least one job is needed");
        }
        var out_ = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<xliff version=\"1.2\" xmlns=\"urn:oasis:names:tc:xliff:document:1.2\">\n");
        foreach (var job in jobs)
        {
            var segments = CheckJob(job);
            out_.Append("  <file original=\"").Append(XmlAttr(job.Reference))
                .Append("\" source-language=\"").Append(XmlAttr(job.SourceLanguage))
                .Append("\" target-language=\"").Append(XmlAttr(job.TargetLanguage))
                .Append("\" datatype=\"html\" product-name=\"").Append(XmlAttr(job.Plugin)).Append("\" tool-id=\"supertext-sdk\">\n")
                .Append("    <header>\n      <tool tool-id=\"supertext-sdk\" tool-name=\"Supertext SDK\"/>\n    </header>\n    <body>\n");
            foreach (var segment in segments)
            {
                out_.Append("      <trans-unit id=\"").Append(XmlAttr(segment.Key)).Append('"')
                    .Append(segment.Html ? string.Empty : " datatype=\"plaintext\"")
                    .Append(" extradata=\"").Append(Checksum(segment)).Append("\" xml:space=\"preserve\">\n")
                    .Append("        <source>").Append(XliffSource(segment)).Append("</source>\n      </trans-unit>\n");
            }
            out_.Append("    </body>\n  </file>\n");
        }
        return out_.Append("</xliff>\n").ToString();
    }

    /// <summary>
    /// The HTML document for one job (what <see cref="HtmlDocument.Build"/> makes) with the job's metadata in its
    /// head. Blank segments are left out; a job with only blank segments is refused (<see cref="TranslationFileErrorCodes.InvalidJob"/>).
    /// </summary>
    public static string ExportHtml(FileJob job)
    {
        var segments = CheckJob(job);
        var out_ = new StringBuilder()
            .Append("<!DOCTYPE html>\n<html lang=\"").Append(HtmlAttr(job.SourceLanguage)).Append("\"><head><meta charset=\"utf-8\">\n")
            .Append("<meta name=\"supertext:plugin\" content=\"").Append(HtmlAttr(job.Plugin)).Append("\">\n")
            .Append("<meta name=\"supertext:reference\" content=\"").Append(HtmlAttr(job.Reference)).Append("\">\n")
            .Append("<meta name=\"supertext:source-language\" content=\"").Append(HtmlAttr(job.SourceLanguage)).Append("\">\n")
            .Append("<meta name=\"supertext:target-language\" content=\"").Append(HtmlAttr(job.TargetLanguage)).Append("\">\n");
        for (var i = 0; i < segments.Count; i++)
        {
            out_.Append("<meta name=\"supertext:segment\" content=\"").Append(i.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(Checksum(segments[i])).Append(' ').Append(HtmlAttr(segments[i].Key)).Append("\">\n");
        }
        out_.Append("</head><body>\n");
        for (var i = 0; i < segments.Count; i++)
        {
            out_.Append("<div data-st-id=\"").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\">")
                .Append(Pack(segments[i])).Append("</div>\n");
        }
        return out_.Append("</body></html>\n").ToString();
    }

    // ---------------------------------------------------------------------------
    // Reading files

    /// <summary>A target part: a string, or a code (kind bpt/ept/ph with an id, or kind "invalid").</summary>
    private sealed record TargetCode(string Kind, int Id);

    private sealed class UnitData
    {
        public required string Key { get; init; }

        public string? Checksum { get; init; }

        /// <summary>XLIFF: the target's parts (null: no target). HTML: the segment's inner HTML as a string (null: missing).</summary>
        public object? Target { get; init; }
    }

    private static List<object> TargetParts(List<object> nodes, List<object>? out_ = null)
    {
        out_ ??= new List<object>();
        foreach (var node in nodes)
        {
            if (node is string s)
            {
                if (out_.Count > 0 && out_[^1] is string previous)
                {
                    out_[^1] = previous + s;
                }
                else
                {
                    out_.Add(s);
                }
                continue;
            }
            var element = (XmlLite.Element)node;
            var name = XmlLite.LocalName(element.Name);
            if (name == "mrk")
            {
                TargetParts(element.Children, out_);
            }
            else if (name is "bpt" or "ept" or "ph" && CodeId().IsMatch(element.Attrs.GetValueOrDefault("id") ?? string.Empty))
            {
                out_.Add(new TargetCode(name, int.Parse(element.Attrs["id"], NumberStyles.None, CultureInfo.InvariantCulture)));
            }
            else
            {
                out_.Add(new TargetCode("invalid", 0));
            }
        }
        return out_;
    }

    private static List<TranslationFile> ReadXliff(string content)
    {
        var root = XmlLite.Parse(content);
        if (XmlLite.LocalName(root.Name) != "xliff")
        {
            throw Fail(TranslationFileErrorCodes.InvalidFile, "not an XLIFF file");
        }
        var files = root.Children.OfType<XmlLite.Element>().Where(e => XmlLite.Is(e, "file")).ToList();
        if (files.Count == 0)
        {
            throw Fail(TranslationFileErrorCodes.InvalidFile, "the XLIFF file has no <file> element");
        }
        var result = new List<TranslationFile>();
        foreach (var file in files)
        {
            var original = file.Attrs.GetValueOrDefault("original");
            var sourceLanguage = file.Attrs.GetValueOrDefault("source-language");
            var targetLanguage = file.Attrs.GetValueOrDefault("target-language");
            var plugin = file.Attrs.GetValueOrDefault("product-name");
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(sourceLanguage) || string.IsNullOrEmpty(targetLanguage) || string.IsNullOrEmpty(plugin))
            {
                throw Fail(TranslationFileErrorCodes.MissingMetadata, "the <file> element lacks original, source-language, target-language or product-name");
            }
            var units = new List<UnitData>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            void Walk(List<object> nodes)
            {
                foreach (var node in nodes)
                {
                    if (node is not XmlLite.Element element)
                    {
                        continue;
                    }
                    if (XmlLite.Is(element, "group"))
                    {
                        Walk(element.Children);
                    }
                    else if (XmlLite.Is(element, "trans-unit"))
                    {
                        var key = element.Attrs.GetValueOrDefault("id");
                        if (string.IsNullOrEmpty(key) || !keys.Add(key))
                        {
                            continue;
                        }
                        var target = element.Children.OfType<XmlLite.Element>().FirstOrDefault(c => XmlLite.Is(c, "target"));
                        var extradata = element.Attrs.GetValueOrDefault("extradata") ?? string.Empty;
                        units.Add(new UnitData
                        {
                            Key = key,
                            Checksum = ChecksumPattern().IsMatch(extradata) ? extradata : null,
                            Target = target is null ? null : TargetParts(target.Children),
                        });
                    }
                }
            }
            foreach (var body in file.Children.OfType<XmlLite.Element>().Where(c => XmlLite.Is(c, "body")))
            {
                Walk(body.Children);
            }
            result.Add(Register(TranslationFileFormats.Xliff, plugin, original, sourceLanguage, targetLanguage, units));
        }
        return result;
    }

    private static TranslationFile ReadHtml(string content)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var segments = new List<(int Position, string Checksum, string Key)>();
        for (var m = Meta().Match(content); m.Success; m = m.NextMatch())
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var a = Attr().Match(m.Groups[1].Value); a.Success; a = a.NextMatch())
            {
                var attrName = a.Groups[1].Value.ToLowerInvariant();
                if (!attrs.ContainsKey(attrName))
                {
                    attrs[attrName] = HtmlDocument.DecodeEntities(AttrValue(a) ?? string.Empty);
                }
            }
            var name = (attrs.GetValueOrDefault("name") ?? string.Empty).ToLowerInvariant();
            if (!name.StartsWith("supertext:", StringComparison.Ordinal) || !attrs.TryGetValue("content", out var value))
            {
                continue;
            }
            if (name == "supertext:segment")
            {
                var s = SegmentMeta().Match(value);
                if (s.Success)
                {
                    segments.Add((int.Parse(s.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture), s.Groups[2].Value, s.Groups[3].Value));
                }
            }
            else
            {
                meta.TryAdd(name, value);
            }
        }
        var plugin = meta.GetValueOrDefault("supertext:plugin");
        var reference = meta.GetValueOrDefault("supertext:reference");
        var sourceLanguage = meta.GetValueOrDefault("supertext:source-language");
        var targetLanguage = meta.GetValueOrDefault("supertext:target-language");
        if (string.IsNullOrEmpty(plugin) || string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(sourceLanguage)
            || string.IsNullOrEmpty(targetLanguage) || segments.Count == 0)
        {
            throw Fail(TranslationFileErrorCodes.MissingMetadata, "the HTML file lacks the supertext:* metadata in its head");
        }
        var count = segments.Max(s => s.Position) + 1;
        var inner = HtmlDocument.ParseSparse(content, count, _ => true);
        var units = new List<UnitData>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var positions = new HashSet<int>();
        foreach (var s in segments.OrderBy(s => s.Position))
        {
            if (keys.Contains(s.Key) || positions.Contains(s.Position))
            {
                continue;
            }
            keys.Add(s.Key);
            positions.Add(s.Position);
            units.Add(new UnitData { Key = s.Key, Checksum = s.Checksum, Target = inner.GetValueOrDefault(s.Position) });
        }
        return Register(TranslationFileFormats.Html, plugin, reference, sourceLanguage, targetLanguage, units);
    }

    private static TranslationFile Register(string format, string plugin, string reference, string sourceLanguage, string targetLanguage, List<UnitData> units)
    {
        var file = new TranslationFile(format, plugin, reference, sourceLanguage, targetLanguage,
            units.Select(u => new FileUnit(u.Key, u.Checksum)).ToList().AsReadOnly());
        Internals.Add(file, units.ToDictionary(u => u.Key, StringComparer.Ordinal));
        return file;
    }

    /// <summary>
    /// Reads an exported file after translation: XLIFF (one result per <c>&lt;file&gt;</c>) or HTML (one result).
    /// Throws <see cref="TranslationFileException"/>: <c>too_large</c>, <c>invalid_file</c> (not XLIFF or HTML,
    /// not well-formed, a DTD), <c>missing_metadata</c>.
    /// </summary>
    public static IReadOnlyList<TranslationFile> Read(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > MaxFileCharacters)
        {
            throw Fail(TranslationFileErrorCodes.TooLarge, $"the file is larger than {MaxFileCharacters.ToString(CultureInfo.InvariantCulture)} characters");
        }
        var start = content.StartsWith('\ufeff') ? content[1..] : content;
        start = JsTrimStart(start);
        start = (start.Length > 200 ? start[..200] : start).ToLowerInvariant();
        if (start.StartsWith("<?xml", StringComparison.Ordinal) || start.StartsWith("<xliff", StringComparison.Ordinal))
        {
            return ReadXliff(content).AsReadOnly();
        }
        if (start.StartsWith("<!doctype html", StringComparison.Ordinal) || HtmlStart().IsMatch(start))
        {
            return new[] { ReadHtml(content) };
        }
        throw Fail(TranslationFileErrorCodes.InvalidFile, "neither an XLIFF nor an HTML file");
    }

    // ---------------------------------------------------------------------------
    // Importing

    private static string EscapeHtmlText(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>Rebuilds an HTML segment from XLIFF target parts with the current source's codes; null if the target uses codes the source doesn't have.</summary>
    private static string? RebuildHtml(List<object> parts, string source)
    {
        var codes = new Dictionary<(string, int), string>();
        foreach (var p in SegmentParts(source))
        {
            if (p is Code code)
            {
                codes[(code.Kind, code.Id)] = code.Raw;
            }
        }
        var open = new Stack<int>();
        var used = new HashSet<int>();
        var out_ = new StringBuilder();
        foreach (var p in parts)
        {
            if (p is string s)
            {
                out_.Append(EscapeHtmlText(s));
                continue;
            }
            var code = (TargetCode)p;
            if (code.Kind == "invalid" || !codes.TryGetValue((code.Kind, code.Id), out var raw))
            {
                return null;
            }
            if (code.Kind == "bpt")
            {
                if (!used.Add(code.Id))
                {
                    return null;
                }
                open.Push(code.Id);
            }
            else if (code.Kind == "ept")
            {
                if (!open.TryPop(out var top) || top != code.Id)
                {
                    return null;
                }
            }
            out_.Append(raw);
        }
        return open.Count == 0 ? out_.ToString() : null;
    }

    private static bool SafeUrl(string value)
    {
        var decoded = HtmlDocument.DecodeEntities(value);
        var sb = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
        {
            if (c > 0x20 && c != 0x7F)
            {
                sb.Append(c);
            }
        }
        var v = sb.ToString().ToLowerInvariant();
        var cut = v.IndexOfAny(new[] { '/', '?', '#' });
        var head = cut < 0 ? v : v[..cut];
        if (head.Contains('&', StringComparison.Ordinal))
        {
            return false;
        }
        var colon = head.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 || SafeSchemes.Contains(head[..colon]);
    }

    /// <summary>A translated HTML segment from an HTML file, rebuilt; null if it has tags, attributes, comments or URLs the source doesn't allow.</summary>
    private static string? SanitizeHtml(string inner, string source)
    {
        var allowed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var comments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Tokenize(source))
        {
            if (t.Kind == TokenKind.Comment)
            {
                comments.Add(t.Raw);
            }
            else if (t.Kind != TokenKind.Text)
            {
                if (!allowed.TryGetValue(t.Name, out var names))
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    allowed[t.Name] = names;
                }
                if (t.Kind == TokenKind.Start)
                {
                    foreach (var (name, _) in t.Attrs!)
                    {
                        names.Add(name);
                    }
                }
            }
        }
        var out_ = new StringBuilder();
        foreach (var t in Tokenize(inner))
        {
            if (t.Kind == TokenKind.Text)
            {
                out_.Append(t.Raw.Replace("<", "&lt;", StringComparison.Ordinal));
            }
            else if (t.Kind == TokenKind.Comment)
            {
                if (!comments.Contains(t.Raw))
                {
                    return null;
                }
                out_.Append(t.Raw);
            }
            else
            {
                if (!allowed.TryGetValue(t.Name, out var names))
                {
                    return null;
                }
                if (t.Kind == TokenKind.Start)
                {
                    foreach (var (name, value) in t.Attrs!)
                    {
                        if (!names.Contains(name))
                        {
                            return null;
                        }
                        if (value is not null && UrlAttributes.Contains(name) && !SafeUrl(value))
                        {
                            return null;
                        }
                        if (value is not null && name == "srcset"
                            && !value.Split(',').All(c => SafeUrl(JsWhitespaceRun().Split(JsTrim(c))[0])))
                        {
                            return null;
                        }
                    }
                }
                out_.Append(t.Raw);
            }
        }
        return out_.ToString();
    }

    /// <summary>
    /// The translations for the integration's current segments (collected again from the item the file's
    /// <see cref="TranslationFile.Reference"/> names), one per non-blank segment in their order. Markup comes
    /// from the current source only: XLIFF codes are replaced by the source's tags; HTML translations may only
    /// use the source's tags, attributes and comments, and safe URLs.
    /// </summary>
    /// <exception cref="TranslationFileException"><c>invalid_file</c> if <paramref name="file"/> wasn't returned by <see cref="Read"/>.</exception>
    public static IReadOnlyList<ImportedSegment> ImportTranslations(TranslationFile file, IReadOnlyList<FileSegment> current)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(current);
        if (!Internals.TryGetValue(file, out var units))
        {
            throw Fail(TranslationFileErrorCodes.InvalidFile, "not a file returned by Read()");
        }
        var result = new List<ImportedSegment>();
        foreach (var segment in current)
        {
            if (IsBlank(segment.Text))
            {
                continue;
            }
            if (!units.TryGetValue(segment.Key, out var unit))
            {
                result.Add(new ImportedSegment(segment.Key, ImportStatuses.Missing, null));
                continue;
            }
            // untranslated: no text; invalid: text null.
            var untranslated = false;
            string? text = null;
            switch (unit.Target)
            {
                case null:
                    untranslated = true;
                    break;
                case string html when segment.Html:
                    if (IsBlank(html))
                    {
                        untranslated = true;
                    }
                    else
                    {
                        text = SanitizeHtml(html, segment.Text);
                    }
                    break;
                case string html:
                    text = HtmlDocument.PlainText(html);
                    untranslated = text.Length == 0;
                    break;
                case List<object> parts:
                    var hasCode = parts.Any(p => p is not string);
                    if (!hasCode && parts.All(p => IsBlank((string)p)))
                    {
                        untranslated = true;
                    }
                    else if (segment.Html)
                    {
                        text = RebuildHtml(parts, segment.Text);
                    }
                    else
                    {
                        text = hasCode ? null : string.Concat(parts.Cast<string>());
                    }
                    break;
            }
            if (untranslated)
            {
                result.Add(new ImportedSegment(segment.Key, ImportStatuses.Untranslated, null));
            }
            else if (text is null)
            {
                result.Add(new ImportedSegment(segment.Key, ImportStatuses.InvalidMarkup, null));
            }
            else
            {
                var status = unit.Checksum == Checksum(segment) ? ImportStatuses.Ok : ImportStatuses.SourceChanged;
                result.Add(new ImportedSegment(segment.Key, status, text));
            }
        }
        return result.AsReadOnly();
    }

    // ---------------------------------------------------------------------------
    // JavaScript's whitespace (\s, trim): the reference implementation's rules.

    internal const string JsSpace = @"\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";

    internal static bool IsJsSpace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00a0' or '\u1680' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000' or '\ufeff'
        || (c >= '\u2000' && c <= '\u200a');

    internal static string JsTrimStart(string s)
    {
        var i = 0;
        while (i < s.Length && IsJsSpace(s[i]))
        {
            i++;
        }
        return s[i..];
    }

    internal static string JsTrim(string s)
    {
        var start = JsTrimStart(s);
        var end = start.Length;
        while (end > 0 && IsJsSpace(start[end - 1]))
        {
            end--;
        }
        return start[..end];
    }

    private const string TagPattern =
        $@"<(?:!--[\s\S]*?-->|/([a-zA-Z][a-zA-Z0-9:-]*)[{JsSpace}]*>|([a-zA-Z][a-zA-Z0-9:-]*)((?:[{JsSpace}]+[^{JsSpace}""'>/=]+(?:[{JsSpace}]*=[{JsSpace}]*(?:""[^""]*""|'[^']*'|[^{JsSpace}""'=<>`]+))?)*)[{JsSpace}]*(/?)>)";

    private const string AttrPattern =
        $@"([^{JsSpace}""'>/=]+)(?:[{JsSpace}]*=[{JsSpace}]*(?:""([^""]*)""|'([^']*)'|([^{JsSpace}""'=<>`]+)))?";

    [GeneratedRegex(TagPattern, RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(AttrPattern, RegexOptions.CultureInvariant)]
    private static partial Regex Attr();

    [GeneratedRegex("&(#[xX][0-9a-fA-F]+|#[0-9]+|[a-zA-Z][a-zA-Z0-9]*);", RegexOptions.CultureInvariant)]
    private static partial Regex Entity();

    [GeneratedRegex(@"^(?:amp|lt|gt|quot|apos|nbsp|#[xX][0-9a-fA-F]+|#[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex KnownEntity();

    // <meta\b…> case-insensitive, \b as in JavaScript (ASCII word characters).
    [GeneratedRegex(@"<(?i:meta)(?![A-Za-z0-9_])((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.CultureInvariant)]
    private static partial Regex Meta();

    [GeneratedRegex(@"^([0-9]{1,9}) ([0-9a-f]{8}) ([\s\S]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentMeta();

    [GeneratedRegex(@"^[0-9a-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumPattern();

    [GeneratedRegex(@"^[1-9][0-9]{0,8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodeId();

    [GeneratedRegex($"^<html[{JsSpace}>]", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlStart();

    [GeneratedRegex($"[{JsSpace}]+", RegexOptions.CultureInvariant)]
    private static partial Regex JsWhitespaceRun();
}
