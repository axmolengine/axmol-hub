using System.Text;

namespace AxmolHub.Core;

public sealed class AxmolApiIndexDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>"local-source" for locally indexed checkouts; Phase 3 reserves "official-release" for remote indexes.</summary>
    public string SourceKind { get; set; } = "local-source";
    public string EngineVersion { get; set; } = "";
    public string? EngineRevision { get; set; }
    public string CoreDirectory { get; set; } = "";
    public string ContentFingerprint { get; set; } = "";
    public int IndexedCppFiles { get; set; }
    public int IndexedLuaFiles { get; set; }
    public int SkippedUnauthorizedFiles { get; set; }
    public List<AxmolApiSymbol> Symbols { get; set; } = [];
}

public sealed record AxmolApiSymbol(
    string Language,
    string Kind,
    string Name,
    string QualifiedName,
    string? Owner,
    string[]? Bases,
    string Signature,
    string RelativePath,
    int Line,
    string BindingSource,
    bool Deprecated = false);

/// <summary>Ranked symbol lookup over a versioned Axmol API index document.</summary>
public static class AxmolApiIndexQuery
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public static string Execute(
        AxmolApiIndexDocument index,
        AxmolEngineResolution resolution,
        bool cacheHit,
        string query,
        string language = "all",
        string kind = "all",
        int limit = DefaultLimit)
    {
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0)
            return "Specify a non-empty symbol query (e.g. Sprite::setProgramState, ProgramManager, PointerEventListener, EventListenerTouchOneByOne).";

        var langFilter = NormalizeLanguage(language);
        if (langFilter is null)
            return "Invalid language filter. Use all, cpp, or lua.";
        var kindFilter = NormalizeKind(kind);
        if (kindFilter is null)
            return "Invalid kind filter. Use all, type, method, field, or enum.";
        var cap = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);

        // Normalize C++ "::", Lua ".", and Lua ":" member notation into a uniform lookup shape.
        var normalizedQuery = NormalizeSeparators(trimmed);
        var queryLower = normalizedQuery.ToLowerInvariant();
        string? queryOwner = null;
        string? queryMember = null;
        var lastDot = queryLower.LastIndexOf('.');
        if (lastDot > 0 && lastDot < queryLower.Length - 1)
        {
            queryOwner = queryLower[..lastDot];
            queryMember = queryLower[(lastDot + 1)..];
        }
        var tokens = queryLower.Split(['.', ' ', '_', '-', '/'], StringSplitOptions.RemoveEmptyEntries);

        var scored = new List<(AxmolApiSymbol Symbol, int Score)>();
        foreach (var symbol in index.Symbols)
        {
            if (langFilter != "all" && !string.Equals(symbol.Language, langFilter, StringComparison.Ordinal))
                continue;
            if (!MatchesKind(symbol.Kind, kindFilter))
                continue;

            var score = ScoreSymbol(symbol, queryLower, queryOwner, queryMember, tokens);
            if (score > 0)
                scored.Add((symbol, score));
        }

        scored.Sort(static (a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;
            cmp = a.Symbol.Deprecated.CompareTo(b.Symbol.Deprecated);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Symbol.QualifiedName, b.Symbol.QualifiedName, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : a.Symbol.Line.CompareTo(b.Symbol.Line);
        });

        var output = new List<AxmolApiSymbol>(cap);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddUnique(AxmolApiSymbol s)
        {
            if (output.Count >= cap) return;
            var key = $"{s.Language}|{s.Kind}|{s.QualifiedName}|{s.RelativePath}|{s.Line}|{s.Signature}";
            if (seen.Add(key)) output.Add(s);
        }

        // When the query names a type directly (no member qualifier) and hits a class/struct/enum at the top,
        // expand its constructors, fields, and key methods in the same response to avoid follow-up round trips.
        if (queryMember is null && scored.Count > 0 && IsTypeKind(scored[0].Symbol.Kind) && scored[0].Score >= 700)
        {
            var primaryTypes = scored
                .Where(item => IsTypeKind(item.Symbol.Kind) && item.Score >= 700)
                .Take(3)
                .Select(item => item.Symbol)
                .ToArray();
            foreach (var typeSymbol in primaryTypes)
                AddUnique(typeSymbol);

            if (output.Count < cap)
            {
                var ownerKeys = primaryTypes
                    .Select(t => NormalizeSeparators(t.QualifiedName).ToLowerInvariant())
                    .Concat(primaryTypes.Select(t => t.Name.ToLowerInvariant()))
                    .ToHashSet(StringComparer.Ordinal);

                var members = index.Symbols
                    .Where(s => !IsTypeKind(s.Kind)
                                && (langFilter == "all" || s.Language == langFilter)
                                && MatchesKind(s.Kind, kindFilter)
                                && s.Owner is not null
                                && (ownerKeys.Contains(NormalizeSeparators(s.Owner).ToLowerInvariant())
                                    || ownerKeys.Contains(ShortName(s.Owner).ToLowerInvariant())))
                    .OrderBy(s => MemberPriority(s))
                    .ThenBy(s => s.Deprecated)
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(cap - output.Count);

                foreach (var member in members)
                    AddUnique(member);
            }
        }

        foreach (var (symbol, _) in scored)
        {
            if (output.Count >= cap) break;
            AddUnique(symbol);
        }

        var sb = new StringBuilder();
        var rev = index.EngineRevision is { Length: > 0 } r ? $" (rev {r})" : "";
        var cacheNote = cacheHit ? "cached" : "freshly indexed";
        sb.AppendLine($"Source: {index.SourceKind} ({cacheNote}, verified against authorized tree)");
        sb.AppendLine($"Engine: Axmol {index.EngineVersion}{rev}; core: {index.CoreDirectory}/; root: {resolution.EngineRoot}");
        sb.AppendLine($"Indexed: {index.IndexedCppFiles} C++ header(s), {index.IndexedLuaFiles} Lua binding file(s), {index.Symbols.Count} symbols.");
        if (index.SkippedUnauthorizedFiles > 0)
            sb.AppendLine($"Scope note: {index.SkippedUnauthorizedFiles} file(s) outside the current sandbox grant were skipped.");

        if (output.Count == 0)
        {
            sb.AppendLine($"No indexed {langFilter}/{kindFilter} symbols matched \"{trimmed}\" in Axmol {index.EngineVersion}.");
            sb.Append("Verify whether the API was renamed or removed in this version, or check with a targeted fs_grep in the matched tree.");
            return sb.ToString();
        }

        sb.AppendLine($"Matches ({output.Count} shown of {scored.Count} matched for \"{trimmed}\"):");
        foreach (var hit in output)
        {
            var dep = hit.Deprecated ? " [deprecated]" : "";
            var bases = hit.Bases is { Length: > 0 } b ? $" : {string.Join(", ", b)}" : "";
            var origin = hit.Language == "lua" ? $" ({hit.BindingSource})" : "";
            sb.AppendLine($"- [{hit.Language} {hit.Kind}{origin}]{dep} {hit.QualifiedName}{bases} — {hit.Signature} — {hit.RelativePath}:{hit.Line}");
        }
        sb.Append("Cite exact relative path and line number; verify critical implementation details with fs_read at the reported line.");
        return sb.ToString();
    }

    private static int MemberPriority(AxmolApiSymbol s)
    {
        if (s.Kind == "field") return 0;
        if (s.Name.StartsWith("create", StringComparison.OrdinalIgnoreCase)) return 1;
        if (s.Name.StartsWith("register", StringComparison.OrdinalIgnoreCase)
            || s.Name.StartsWith("set", StringComparison.OrdinalIgnoreCase)
            || s.Name.StartsWith("load", StringComparison.OrdinalIgnoreCase)
            || s.Name.StartsWith("add", StringComparison.OrdinalIgnoreCase)) return 2;
        if (s.Kind == "method") return 3;
        return 4;
    }

    private static int ScoreSymbol(
        AxmolApiSymbol symbol,
        string queryLower,
        string? queryOwner,
        string? queryMember,
        string[] tokens)
    {
        var qualLower = NormalizeSeparators(symbol.QualifiedName).ToLowerInvariant();
        var nameLower = symbol.Name.ToLowerInvariant();
        var ownerLower = symbol.Owner is not null ? NormalizeSeparators(symbol.Owner).ToLowerInvariant() : "";
        var ownerShort = ShortName(ownerLower);
        var ownerMember = ownerShort.Length > 0 ? $"{ownerShort}.{nameLower}" : nameLower;

        if (qualLower == queryLower) return 1000 + (IsTypeKind(symbol.Kind) ? 40 : 0);
        if (ownerMember == queryLower) return 960;
        if (queryOwner is not null && queryMember is not null)
        {
            var ownerMatches = ownerLower == queryOwner || ownerShort == queryOwner || ownerLower.EndsWith("." + queryOwner, StringComparison.Ordinal);
            if (ownerMatches && nameLower == queryMember) return 950;
            if (ownerMatches && nameLower.StartsWith(queryMember, StringComparison.Ordinal)) return 820;
            if (ownerMatches && nameLower.Contains(queryMember, StringComparison.Ordinal)) return 700;
            if (ownerLower.Contains(queryOwner, StringComparison.Ordinal) && nameLower == queryMember) return 760;
            if (ownerLower.Contains(queryOwner, StringComparison.Ordinal) && nameLower.Contains(queryMember, StringComparison.Ordinal)) return 620;
            return 0;
        }

        if (nameLower == queryLower) return 900 + (IsTypeKind(symbol.Kind) ? 60 : 0);
        if (qualLower.EndsWith("." + queryLower, StringComparison.Ordinal)) return 880 + (IsTypeKind(symbol.Kind) ? 40 : 0);
        if (nameLower.StartsWith(queryLower, StringComparison.Ordinal)) return 650 + (IsTypeKind(symbol.Kind) ? 30 : 0);
        if (qualLower.Contains(queryLower, StringComparison.Ordinal)) return 520 + (IsTypeKind(symbol.Kind) ? 20 : 0);

        if (tokens.Length > 1 && tokens.All(t => qualLower.Contains(t, StringComparison.Ordinal)))
            return 440;

        if (symbol.Signature.Contains(queryLower, StringComparison.OrdinalIgnoreCase) && queryLower.Length >= 4)
            return 220;

        return 0;
    }

    private static bool IsTypeKind(string kind) => kind is "class" or "struct" or "enum";

    private static bool MatchesKind(string symbolKind, string filter) => filter switch
    {
        "all" => true,
        "type" => symbolKind is "class" or "struct" or "enum",
        "method" => symbolKind is "method" or "function",
        "field" => symbolKind is "field",
        "enum" => symbolKind is "enum" or "constant",
        _ => true,
    };

    private static string? NormalizeLanguage(string? language) => (language ?? "all").Trim().ToLowerInvariant() switch
    {
        "" or "all" => "all",
        "cpp" or "c++" => "cpp",
        "lua" => "lua",
        _ => null,
    };

    private static string? NormalizeKind(string? kind) => (kind ?? "all").Trim().ToLowerInvariant() switch
    {
        "" or "all" => "all",
        "type" or "class" or "struct" => "type",
        "method" or "function" => "method",
        "field" or "property" => "field",
        "enum" or "constant" => "enum",
        _ => null,
    };

    private static string NormalizeSeparators(string value)
        => value.Replace("::", ".", StringComparison.Ordinal).Replace(':', '.').Trim('.');

    private static string ShortName(string value)
    {
        var idx = value.LastIndexOf('.');
        return idx >= 0 && idx < value.Length - 1 ? value[(idx + 1)..] : value;
    }
}
