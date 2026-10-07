namespace AxmolHub.Core;

/// <summary>A website request to install one exact engine release from a supported package source.</summary>
public sealed record EngineInstallLink(string Version, string Source)
{
    public const string Scheme = "axmolhub";
    private const int MaximumLength = 2048;

    public static EngineInstallLink Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("install", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath is not ("" or "/")
            || uri.UserInfo.Length != 0
            || !uri.IsDefaultPort
            || uri.Fragment.Length != 0)
        {
            throw new FormatException("Invalid Axmol Hub install link.");
        }

        var queryStart = value.IndexOf('?');
        if (queryStart < 0 || queryStart == value.Length - 1)
        {
            throw new FormatException("The install link must include version and source.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value[(queryStart + 1)..].Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException("Invalid install link query parameter.");
            }

            var key = Decode(pair[..separator]);
            var item = Decode(pair[(separator + 1)..]);
            if (key is not ("version" or "source") || item.Length == 0 || !values.TryAdd(key, item))
            {
                throw new FormatException("The install link contains an unsupported or duplicate parameter.");
            }
        }

        if (!values.TryGetValue("version", out var version)
            || !values.TryGetValue("source", out var source)
            || version.Length > 64
            || version.Any(char.IsControl)
            || version.Any(char.IsWhiteSpace)
            || !DownloadSources.IsKnown(source))
        {
            throw new FormatException("The install link must specify a valid version and supported source.");
        }

        return new EngineInstallLink(version, source);
    }

    private static string Decode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
            {
                throw new FormatException("Invalid percent-encoding in install link.");
            }

            index += 2;
        }

        try
        {
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
        }
        catch (UriFormatException ex)
        {
            throw new FormatException("Invalid percent-encoding in install link.", ex);
        }
    }
}
