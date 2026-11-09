using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Supertext.OrchardCore.Translation.Sdk.Files;

/// <summary>
/// A small XML parser for imported XLIFF files: elements, attributes, text, CDATA; comments and processing
/// instructions skipped. No DTD: a DOCTYPE or any other <c>&lt;!</c> declaration is refused before anything
/// is expanded, and only the five predefined entities and character references are known.
/// </summary>
internal static partial class XmlLite
{
    internal sealed class Element
    {
        public Element(string name) => Name = name;

        public string Name { get; }

        /// <summary>Attributes by local name.</summary>
        public Dictionary<string, string> Attrs { get; } = new(StringComparer.Ordinal);

        /// <summary>Strings and elements.</summary>
        public List<object> Children { get; } = new();
    }

    internal static string LocalName(string name) => name[(name.LastIndexOf(':') + 1)..];

    internal static bool Is(Element element, string localName) => LocalName(element.Name) == localName;

    private static TranslationFileException Bad(string what) =>
        new(TranslationFileErrorCodes.InvalidFile, $"not a well-formed XML file ({what})");

    private static bool IsXmlChar(long cp) =>
        cp == 0x9 || cp == 0xA || cp == 0xD || (cp >= 0x20 && cp <= 0xD7FF) || (cp >= 0xE000 && cp <= 0xFFFD) || (cp >= 0x10000 && cp <= 0x10FFFF);

    private static bool IsBlank(string text) => text.All(c => c is ' ' or '\t' or '\r' or '\n' or '\f');

    private static string Window(string s, int start, int length) => s.Substring(start, Math.Min(length, s.Length - start));

    internal static string Decode(string text)
    {
        var out_ = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var amp = text.IndexOf('&', i);
            if (amp < 0)
            {
                return out_.Append(text, i, text.Length - i).ToString();
            }
            out_.Append(text, i, amp - i);
            var m = Reference().Match(Window(text, amp, 12));
            if (!m.Success)
            {
                throw new TranslationFileException(TranslationFileErrorCodes.InvalidFile, "not a well-formed XML file (unknown entity)");
            }
            if (m.Groups[3].Success)
            {
                out_.Append(m.Groups[3].Value switch
                {
                    "amp" => '&',
                    "lt" => '<',
                    "gt" => '>',
                    "quot" => '"',
                    _ => '\'',
                });
            }
            else
            {
                var cp = m.Groups[1].Success
                    ? long.Parse(m.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                    : long.Parse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                if (!IsXmlChar(cp))
                {
                    throw new TranslationFileException(TranslationFileErrorCodes.InvalidFile, "not a well-formed XML file (character reference)");
                }
                out_.Append(char.ConvertFromUtf32((int)cp));
            }
            i = amp + m.Length;
        }
        return out_.ToString();
    }

    internal static Element Parse(string source)
    {
        var s = (source.StartsWith('\ufeff') ? source[1..] : source).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var i = 0;
        Element? root = null;
        var stack = new List<Element>();
        while (i < s.Length)
        {
            var lt = s.IndexOf('<', i);
            var textEnd = lt < 0 ? s.Length : lt;
            if (textEnd > i)
            {
                var text = s[i..textEnd];
                if (stack.Count > 0)
                {
                    stack[^1].Children.Add(Decode(text));
                }
                else if (!IsBlank(text))
                {
                    throw Bad("text outside the root element");
                }
                if (text.Contains("]]>", StringComparison.Ordinal))
                {
                    throw Bad("]]> in text");
                }
            }
            if (lt < 0)
            {
                break;
            }
            if (string.CompareOrdinal(s, lt, "<!--", 0, 4) == 0)
            {
                var end = s.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw Bad("unclosed comment");
                }
                i = end + 3;
            }
            else if (string.CompareOrdinal(s, lt, "<![CDATA[", 0, 9) == 0)
            {
                var end = s.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                if (end < 0 || stack.Count == 0)
                {
                    throw Bad("CDATA");
                }
                stack[^1].Children.Add(s[(lt + 9)..end]);
                i = end + 3;
            }
            else if (string.CompareOrdinal(s, lt, "<?", 0, 2) == 0)
            {
                var end = s.IndexOf("?>", lt + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw Bad("unclosed processing instruction");
                }
                i = end + 2;
            }
            else if (string.CompareOrdinal(s, lt, "<!", 0, 2) == 0)
            {
                throw Bad("declarations are not allowed");
            }
            else if (string.CompareOrdinal(s, lt, "</", 0, 2) == 0)
            {
                var nameMatch = Name().Match(s, lt + 2);
                var end = s.IndexOf('>', lt);
                if (!nameMatch.Success || end < 0
                    || TranslationFiles.JsTrim(s[(lt + 2 + nameMatch.Length)..end]).Length != 0)
                {
                    throw Bad("end tag");
                }
                if (stack.Count == 0 || stack[^1].Name != nameMatch.Value)
                {
                    throw Bad("mismatched end tag");
                }
                stack.RemoveAt(stack.Count - 1);
                i = end + 1;
            }
            else
            {
                var nameMatch = Name().Match(s, lt + 1);
                if (!nameMatch.Success)
                {
                    throw Bad("start tag");
                }
                var j = lt + 1 + nameMatch.Length;
                var element = new Element(nameMatch.Value);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    // The reference implementation looks at most 200 characters ahead for whitespace and names.
                    var ws = 0;
                    var window = Window(s, j, 200);
                    while (ws < window.Length && TranslationFiles.IsJsSpace(window[ws]))
                    {
                        ws++;
                    }
                    j += ws;
                    if (j < s.Length && (s[j] == '>' || (s[j] == '/' && j + 1 < s.Length && s[j + 1] == '>')))
                    {
                        break;
                    }
                    if (ws == 0)
                    {
                        throw Bad("attribute");
                    }
                    var attr = AnchoredName().Match(Window(s, j, 200));
                    if (!attr.Success)
                    {
                        throw Bad("attribute");
                    }
                    j += attr.Length;
                    var eq = Equals_().Match(Window(s, j, 200));
                    if (!eq.Success)
                    {
                        throw Bad("attribute value");
                    }
                    j += eq.Length;
                    var close = s.IndexOf(eq.Groups[1].Value[0], j);
                    if (close < 0)
                    {
                        throw Bad("attribute value");
                    }
                    var raw = s[j..close];
                    if (raw.Contains('<', StringComparison.Ordinal))
                    {
                        throw Bad("< in attribute value");
                    }
                    if (!seen.Add(attr.Value))
                    {
                        throw Bad("duplicate attribute");
                    }
                    element.Attrs[LocalName(attr.Value)] = Decode(raw.Replace('\t', ' ').Replace('\n', ' '));
                    j = close + 1;
                }
                var selfClosing = s[j] == '/';
                i = j + (selfClosing ? 2 : 1);
                if (stack.Count > 0)
                {
                    stack[^1].Children.Add(element);
                }
                else if (root is not null)
                {
                    throw Bad("more than one root element");
                }
                else
                {
                    root = element;
                }
                if (!selfClosing)
                {
                    stack.Add(element);
                }
            }
        }
        if (root is null || stack.Count > 0)
        {
            throw Bad("unclosed element");
        }
        return root;
    }

    [GeneratedRegex(@"\G[A-Za-z_:][-A-Za-z0-9_:.]*", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    [GeneratedRegex(@"^[A-Za-z_:][-A-Za-z0-9_:.]*", RegexOptions.CultureInvariant)]
    private static partial Regex AnchoredName();

    [GeneratedRegex($@"^[{TranslationFiles.JsSpace}]*=[{TranslationFiles.JsSpace}]*([""'])", RegexOptions.CultureInvariant)]
    private static partial Regex Equals_();

    [GeneratedRegex(@"^&(?:#x([0-9a-fA-F]+)|#([0-9]+)|(amp|lt|gt|quot|apos));", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
