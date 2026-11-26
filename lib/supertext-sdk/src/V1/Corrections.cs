using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Supertext.OrchardCore.Translation.Sdk.V1;

/// <summary>
/// A correction a reviewer made in Supertext Localization Review that the integration hasn't written
/// into the CMS yet (<see cref="SupertextClient.PendingCorrectionsAsync"/>; spec/corrections.json).
/// </summary>
/// <param name="Id">The review service's id of this place (send back with <see cref="SupertextClient.AcknowledgeCorrectionsAsync"/>).</param>
/// <param name="SegmentId">The <c>data-st-id</c> the integration gave the segment (its own reference to the field).</param>
/// <param name="SourceLang">Null: the source language was detected.</param>
/// <param name="TargetLang">The target language code.</param>
/// <param name="Politeness">Formal or informal address of the translation.</param>
/// <param name="SourceHtml">The source segment.</param>
/// <param name="PreviousHtml">What the integration received for this place last: check the field still holds it before overwriting an editor's change.</param>
/// <param name="Html">The corrected translation: write this into the field.</param>
/// <param name="Status"><c>corrected</c>, <c>approved</c>, …</param>
/// <param name="CorrectedAt">ISO time of the latest change.</param>
/// <param name="Version">Changes with every new correction: acknowledge the version you wrote.</param>
public sealed record Correction(
    string Id,
    string SegmentId,
    string? SourceLang,
    string TargetLang,
    Politeness Politeness,
    string SourceHtml,
    string PreviousHtml,
    string Html,
    string Status,
    string CorrectedAt,
    string Version);

/// <summary>The review service's two calls without HTTP (spec/corrections.json).</summary>
internal static partial class Corrections
{
    public const int PageSize = 500;
    public const int MaxPages = 100;
    public const int AcknowledgeBatchSize = 1000;

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex DigitsPattern();

    public static string Path(string? targetLang, string? sourceLang, Politeness? politeness, string? after)
    {
        var query = new List<string>();
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                query.Add(name + "=" + Uri.EscapeDataString(value));
            }
        }
        Add("target_lang", targetLang);
        Add("source_lang", sourceLang);
        Add("politeness", politeness switch
        {
            null => null,
            Politeness.More => "more",
            Politeness.Less => "less",
            _ => "default",
        });
        Add("after", after);
        Add("limit", PageSize.ToString(CultureInfo.InvariantCulture));
        return "review/corrections?" + string.Join("&", query);
    }

    public static (List<Correction> Corrections, string? NextAfter) ParsePage(string body)
    {
        var result = new List<Correction>();
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("corrections", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return (result, null);
            }
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var id = NonEmpty(item, "id");
                var stId = NonEmpty(item, "st_id");
                var target = NonEmpty(item, "target_lang");
                var html = NonEmpty(item, "html");
                var version = Str(item, "version");
                if (id is null || stId is null || target is null || html is null || version is null || !VersionPattern().IsMatch(version))
                {
                    continue;
                }
                var politeness = Str(item, "politeness") switch
                {
                    "more" => Politeness.More,
                    "less" => Politeness.Less,
                    _ => Politeness.Default,
                };
                result.Add(new Correction(id, stId, NonEmpty(item, "source_lang"), target, politeness,
                    Str(item, "source_html") ?? "", Str(item, "previous_html") ?? "", html, Str(item, "status") ?? "", Str(item, "corrected_at") ?? "", version));
            }
            var next = Str(root, "next_after");
            return (result, next is not null && DigitsPattern().IsMatch(next) ? next : null);
        }
        catch (JsonException)
        {
            return (result, null);
        }
    }

    public static string AcknowledgeBody(IEnumerable<Correction> corrections)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("corrections");
            foreach (var c in corrections)
            {
                writer.WriteStartObject();
                writer.WriteString("id", c.Id);
                writer.WriteString("version", c.Version);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static int AcknowledgedCount(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("acknowledged", out var n)
                && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count) && count >= 0 ? count : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NonEmpty(JsonElement e, string name) => Str(e, name) is { Length: > 0 } s ? s : null;
}
