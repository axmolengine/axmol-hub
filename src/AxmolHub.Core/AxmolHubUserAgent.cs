namespace AxmolHub.Core;

/// <summary>Identifies Hub's HTTP requests without changing for every patch release.</summary>
public static class AxmolHubUserAgent
{
    /// <summary>Uses the product's major and minor version; the patch component stays at zero.</summary>
    public static string Value
    {
        get
        {
            var version = typeof(AxmolHubUserAgent).Assembly.GetName().Version;
            return $"axmol-hub/{version?.Major ?? 0}.{version?.Minor ?? 0}.0";
        }
    }
}
