using System.Globalization;

namespace Supertext.OrchardCore.Translation.Services;

public static class CultureNames
{
    /// <summary>"German (Switzerland)" for "de-CH"; the code itself if .NET doesn't know it.</summary>
    public static string Get(string culture)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(culture).DisplayName;
            return string.IsNullOrEmpty(name) ? culture : name;
        }
        catch (CultureNotFoundException)
        {
            return culture;
        }
    }

    /// <summary>
    /// The name of a Supertext code in the current UI language where .NET knows it ("Deutsch (Schweiz)"
    /// for de-CH under German), else <paramref name="fallback"/> (the SDK's English name), e.g. for
    /// gsw-u-sd-chzh or a code ICU doesn't name.
    /// </summary>
    public static string DisplayNameOr(string code, string fallback)
    {
        if (code.Contains("-u-", StringComparison.Ordinal))
        {
            return fallback;
        }
        var name = Get(code);
        return name == code || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) || name.Contains(code, StringComparison.OrdinalIgnoreCase) ? fallback : name;
    }
}
