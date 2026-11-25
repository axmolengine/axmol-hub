using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>Builds, caches, and queries a versioned local C++ and Lua API index for an authorized Axmol tree.</summary>
public static partial class AxmolApiIndexStore
{
    private const long MaxIndexedFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Query(
        WorkspaceToolScope scope,
        string query,
        string language = "all",
        string kind = "all",
        int limit = AxmolApiIndexQuery.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = AxmolSkillEngine.Resolve(scope);
        if (!resolution.IsMatched || resolution.EngineRoot is null || resolution.CoreDirectory is null)
            return resolution.Message;

        var (index, cacheHit) = LoadOrBuild(scope, resolution, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return AxmolApiIndexQuery.Execute(index, resolution, cacheHit, query, language, kind, limit);
    }

    public static (AxmolApiIndexDocument Document, bool CacheHit) LoadOrBuild(
        WorkspaceToolScope scope,
        AxmolEngineResolution resolution,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engineRoot = resolution.EngineRoot ?? throw new InvalidOperationException("Engine root is missing.");
        var coreDir = resolution.CoreDirectory ?? throw new InvalidOperationException("Core directory is missing.");

        var (cppFiles, luaManifests, luaAdapters, luaV2Files, skippedUnauthorized) =
            CollectAuthorizedFiles(scope, engineRoot, coreDir, cancellationToken);

        var fingerprint = ComputeFingerprint(cppFiles, luaManifests, luaAdapters, luaV2Files, skippedUnauthorized);
        var cachePath = CachePathFor(scope, resolution);
        if (cachePath is not null && File.Exists(cachePath))
        {
            var cached = StateStore.ReadJson<AxmolApiIndexDocument>(cachePath);
            if (cached is not null
                && cached.SchemaVersion == AxmolApiIndexDocument.CurrentSchemaVersion
                && string.Equals(cached.ContentFingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(cached.EngineVersion, resolution.Version, StringComparison.Ordinal)
                && string.Equals(cached.CoreDirectory, coreDir, StringComparison.Ordinal))
            {
                return (cached, true);
            }
        }

        var symbols = new List<AxmolApiSymbol>(4096);
        foreach (var file in cppFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadText(file.FullPath) is { } text)
                IndexCppHeader(text, file.RelativePath, symbols);
        }

        foreach (var file in luaManifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadText(file.FullPath) is { } text)
                IndexV3LuaManifest(text, file.RelativePath, symbols);
        }

        foreach (var file in luaAdapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadText(file.FullPath) is { } text)
                IndexV3LuaAdapter(text, file.RelativePath, symbols);
        }

        foreach (var file in luaV2Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadText(file.FullPath) is { } text)
                IndexV2LuaCpp(text, file.RelativePath, file.Tag, symbols);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var doc = new AxmolApiIndexDocument
        {
            SchemaVersion = AxmolApiIndexDocument.CurrentSchemaVersion,
            SourceKind = "local-source",
            EngineVersion = resolution.Version ?? "",
            EngineRevision = resolution.Revision,
            CoreDirectory = coreDir,
            ContentFingerprint = fingerprint,
            IndexedCppFiles = cppFiles.Count,
            IndexedLuaFiles = luaManifests.Count + luaAdapters.Count + luaV2Files.Count,
            SkippedUnauthorizedFiles = skippedUnauthorized,
            Symbols = symbols,
        };

        if (cachePath is not null)
        {
            try { WriteCompactJson(cachePath, doc); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return (doc, false);
    }

    private static void WriteCompactJson<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, TextEol.ToLf(JsonSerializer.Serialize(data, CompactJson)));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record IndexedSourceFile(string FullPath, string RelativePath, long Length, long LastWriteTicks, string Tag);

    private static (
        List<IndexedSourceFile> CppFiles,
        List<IndexedSourceFile> LuaManifests,
        List<IndexedSourceFile> LuaAdapters,
        List<IndexedSourceFile> LuaV2Files,
        int SkippedUnauthorized)
        CollectAuthorizedFiles(
            WorkspaceToolScope scope,
            string engineRoot,
            string coreDir,
            CancellationToken cancellationToken)
    {
        var cppFiles = new List<IndexedSourceFile>();
        var luaManifests = new List<IndexedSourceFile>();
        var luaAdapters = new List<IndexedSourceFile>();
        var luaV2Files = new List<IndexedSourceFile>();
        var skipped = 0;

        void Walk(string rootSubdir, Func<string, bool> filePredicate, List<IndexedSourceFile> target, string tag, HashSet<string>? skipDirs = null)
        {
            var startDir = Path.Combine(engineRoot, rootSubdir);
            if (!Directory.Exists(startDir)) return;

            var stack = new Stack<string>();
            stack.Push(startDir);
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                if (IsReparsePoint(dir)) continue;

                // Even if the parent directory itself isn't fully granted, a narrow subdirectory grant may authorize
                // children underneath it, so check each file through WorkspacePaths.ResolveRead.
                IEnumerable<string> subDirs;
                try { subDirs = Directory.EnumerateDirectories(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var sub in subDirs.OrderByDescending(static s => s, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(sub);
                    if (skipDirs is not null && skipDirs.Contains(name)) continue;
                    if (name.StartsWith("proj.", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(sub);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var full in files.OrderBy(static f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (!filePredicate(full) || IsReparsePoint(full)) continue;
                    var resolved = WorkspacePaths.ResolveRead(scope.Sandbox, full, scope.Guards);
                    if (!resolved.IsAllowed)
                    {
                        skipped++;
                        continue;
                    }
                    try
                    {
                        var info = new FileInfo(resolved.Full);
                        if (info.Length > MaxIndexedFileBytes) continue;
                        var rel = Path.GetRelativePath(engineRoot, resolved.Full).Replace('\\', '/');
                        target.Add(new(resolved.Full, rel, info.Length, info.LastWriteTimeUtc.Ticks, tag));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        // Core public C++ headers (<core>/**/*.h)
        Walk(coreDir, static f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase), cppFiles, "header",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "3rdparty", "build", ".git" });

        // First-party extension public headers (excluding 3rdparty, generated bindings, auto bindings, platform proj dirs)
        Walk("extensions",
            static f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase),
            cppFiles, "header",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "3rdparty", "generated", "auto", "build", ".git", "scripting" });

        // v3 Lua manifests and manual adapters
        Walk(Path.Combine("extensions", "scripting", "lua-bindings", "generated", "manifests"),
            static f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase), luaManifests, "manifest");
        Walk(Path.Combine("extensions", "scripting", "lua-bindings", "adapters"),
            static f => f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase), luaAdapters, "adapter");

        // v2 Lua auto and manual bindings
        Walk(Path.Combine("extensions", "scripting", "lua-bindings", "auto"),
            static f => f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase), luaV2Files, "auto");
        Walk(Path.Combine("extensions", "scripting", "lua-bindings", "manual"),
            static f => f.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase), luaV2Files, "manual");

        cppFiles.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        luaManifests.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        luaAdapters.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        luaV2Files.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        return (cppFiles, luaManifests, luaAdapters, luaV2Files, skipped);
    }

    private static string ComputeFingerprint(
        List<IndexedSourceFile> cppFiles,
        List<IndexedSourceFile> luaManifests,
        List<IndexedSourceFile> luaAdapters,
        List<IndexedSourceFile> luaV2Files,
        int skippedUnauthorized)
    {
        var sb = new StringBuilder();
        sb.Append("v1|skip=").Append(skippedUnauthorized).Append('\n');
        void AppendAll(List<IndexedSourceFile> list)
        {
            foreach (var f in list)
                sb.Append(f.RelativePath).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTicks).Append('\n');
        }
        AppendAll(cppFiles);
        AppendAll(luaManifests);
        AppendAll(luaAdapters);
        AppendAll(luaV2Files);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static string? CachePathFor(WorkspaceToolScope scope, AxmolEngineResolution resolution)
    {
        if (string.IsNullOrWhiteSpace(scope.DataRoot) || string.IsNullOrWhiteSpace(resolution.EngineRoot))
            return null;
        var sandboxScope = string.Join('|', scope.Sandbox.Roots.Select(WorkspacePaths.CanonicalRoot).OrderBy(static r => r, StringComparer.Ordinal));
        var keyMaterial = $"{WorkspacePaths.CanonicalRoot(resolution.EngineRoot)}|{resolution.CoreDirectory}|{resolution.Version}|{sandboxScope}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)))[..24].ToLowerInvariant();
        return Path.Combine(scope.DataRoot, "cache", "axmol-api-index", $"{key}.json");
    }

    private static string? TryReadText(string fullPath)
    {
        try
        {
            var snapshot = TextFile.Read(fullPath, (int)MaxIndexedFileBytes);
            return snapshot.State == TextFileState.Text ? snapshot.Text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // C++ Header Indexer (brace, namespace, and access-specifier aware)
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed class ScopeFrame(string kind, string name, int braceDepth, bool isPublic)
    {
        public string Kind { get; } = kind; // "namespace" | "class" | "struct" | "enum" | "body"
        public string Name { get; } = name;
        public int BraceDepth { get; } = braceDepth;
        public bool IsPublic { get; set; } = isPublic;
    }

    internal static void IndexCppHeader(string content, string relativePath, List<AxmolApiSymbol> output)
    {
        var lines = StripCppCommentsPreservingLines(content);
        var scopes = new List<ScopeFrame>();
        var braceDepth = 0;
        var pendingDeprecated = false;
        var declBuffer = new StringBuilder();
        var declStartLine = 0;

        string CurrentQualifier()
        {
            var parts = scopes.Where(static s => s.Kind is "namespace" or "class" or "struct" or "enum" && s.Name.Length > 0)
                .Select(static s => s.Name);
            return string.Join("::", parts);
        }

        ScopeFrame? EnclosingType()
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
            {
                if (scopes[i].Kind is "class" or "struct" or "enum") return scopes[i];
                if (scopes[i].Kind == "body") return null;
            }
            return null;
        }

        bool InsideBody()
        {
            for (var i = scopes.Count - 1; i >= 0; i--)
                if (scopes[i].Kind == "body") return true;
            return false;
        }

        void PopScopesTo(int depth)
        {
            while (scopes.Count > 0 && scopes[^1].BraceDepth > depth)
                scopes.RemoveAt(scopes.Count - 1);
        }

        void FlushDeclaration(string rawDecl, int startLine, bool openedBrace, bool deprecated)
        {
            var text = NormalizeSpaces(rawDecl);
            if (text.Length == 0) return;

            var enclosing = EnclosingType();
            if (enclosing is { Kind: "enum" })
            {
                // Enum constants inside an enum body (comma-separated).
                var ownerQual = CurrentQualifier();
                foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var eq = entry.IndexOf('=');
                    var name = (eq >= 0 ? entry[..eq] : entry).Trim();
                    if (IsValidIdentifier(name))
                    {
                        output.Add(new("cpp", "constant", name, $"{ownerQual}::{name}", ownerQual, null,
                            entry.Length > 120 ? entry[..120] : entry, relativePath, startLine, "header", deprecated));
                    }
                }
                return;
            }

            if (enclosing is not null && !enclosing.IsPublic) return;
            if (text.StartsWith("friend ", StringComparison.Ordinal)
                || text.StartsWith("using ", StringComparison.Ordinal)
                || text.StartsWith("typedef ", StringComparison.Ordinal)
                || text.StartsWith("static_assert", StringComparison.Ordinal)
                || text.StartsWith("AX_PROPERTY", StringComparison.Ordinal) && !text.Contains('('))
                return;

            // Recognize AX_SYNTHESIZE / AX_PROPERTY macros if present, otherwise standard C++ declarations.
            var cleaned = StripDeclarationAttributes(text);
            if (cleaned.Length == 0) return;

            var parenIdx = FindTopLevelChar(cleaned, '(');
            if (parenIdx > 0)
            {
                // Skip function pointer / std::function fields from being misclassified as methods.
                var beforeParen = cleaned[..parenIdx].Trim();
                if (beforeParen.StartsWith("std::function", StringComparison.Ordinal)
                    || beforeParen.Contains("std::function<", StringComparison.Ordinal))
                {
                    TryEmitCppField(cleaned, CurrentQualifier(), enclosing, relativePath, startLine, deprecated, output);
                    return;
                }

                // Extract method/constructor/function name at the end of beforeParen.
                var nameMatch = CppCallableNameRegex().Match(beforeParen);
                if (!nameMatch.Success) return;
                var name = nameMatch.Groups[1].Value;
                if (name is "if" or "for" or "while" or "switch" or "return" or "catch" or "sizeof" or "decltype" or "alignas"
                    or "AX_DEPRECATED" or "AX_DEPRECATED_ATTRIBUTE")
                    return;

                var ownerQual = enclosing is not null ? CurrentQualifier() : null;
                var nsQual = CurrentQualifier();
                var qualified = nsQual.Length > 0 ? $"{nsQual}::{name}" : name;
                var sig = TrimSignatureBody(cleaned, openedBrace);
                if (sig.Length > 260) sig = sig[..260] + "...";
                var kind = enclosing is not null ? "method" : "function";
                output.Add(new("cpp", kind, name, qualified, ownerQual, null, sig, relativePath, startLine, "header", deprecated));
                return;
            }

            // Public data fields on classes/structs (especially callback fields like std::function<...> onPointerDown).
            if (enclosing is not null && !openedBrace)
                TryEmitCppField(cleaned, CurrentQualifier(), enclosing, relativePath, startLine, deprecated, output);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNum = i + 1;
            var raw = lines[i].Trim();
            if (raw.Length == 0) continue;

            // Preprocessor lines
            if (raw[0] == '#')
            {
                declBuffer.Clear();
                continue;
            }

            // Axmol namespace macros (no braces in source)
            if (raw is "NS_AX_BEGIN" or "NS_AX_MATH_BEGIN")
            {
                scopes.Add(new("namespace", "ax", braceDepth, true));
                declBuffer.Clear();
                continue;
            }
            if (raw == "NS_AX_BACKEND_BEGIN")
            {
                scopes.Add(new("namespace", "ax::backend", braceDepth, true));
                declBuffer.Clear();
                continue;
            }
            if (raw is "NS_AX_END" or "NS_AX_BACKEND_END" or "NS_AX_MATH_END")
            {
                for (var s = scopes.Count - 1; s >= 0; s--)
                {
                    if (scopes[s].Kind == "namespace") { scopes.RemoveAt(s); break; }
                }
                declBuffer.Clear();
                continue;
            }

            if (raw.Contains("AX_DEPRECATED", StringComparison.Ordinal) || raw.Contains("[[deprecated", StringComparison.Ordinal))
                pendingDeprecated = true;

            if (InsideBody())
            {
                foreach (var ch in raw)
                {
                    if (ch == '{') braceDepth++;
                    else if (ch == '}') { braceDepth = Math.Max(0, braceDepth - 1); PopScopesTo(braceDepth); }
                }
                continue;
            }

            // Access specifiers inside class/struct
            if (raw is "public:" or "protected:" or "private:")
            {
                if (EnclosingType() is { Kind: "class" or "struct" } t)
                    t.IsPublic = raw is "public:" or "protected:";
                declBuffer.Clear();
                pendingDeprecated = false;
                continue;
            }

            // Handle closing brace on its own or at start of line
            if (raw[0] == '}')
            {
                if (declBuffer.Length > 0 && EnclosingType() is { Kind: "enum" })
                {
                    FlushDeclaration(declBuffer.ToString(), declStartLine, false, pendingDeprecated);
                    declBuffer.Clear();
                }
                foreach (var ch in raw)
                {
                    if (ch == '{') braceDepth++;
                    else if (ch == '}') { braceDepth = Math.Max(0, braceDepth - 1); PopScopesTo(braceDepth); }
                }
                declBuffer.Clear();
                pendingDeprecated = false;
                continue;
            }

            if (declBuffer.Length == 0) declStartLine = lineNum;
            else declBuffer.Append(' ');
            declBuffer.Append(raw);

            var current = declBuffer.ToString();

            // Check for namespace declaration: namespace ax::rhi {
            var nsMatch = CppNamespaceRegex().Match(current);
            if (nsMatch.Success && current.Contains('{'))
            {
                var openCount = current.Count(static c => c == '{');
                var closeCount = current.Count(static c => c == '}');
                braceDepth += openCount - closeCount;
                if (openCount > closeCount)
                    scopes.Add(new("namespace", nsMatch.Groups[1].Value, braceDepth, true));
                else
                    PopScopesTo(braceDepth);
                declBuffer.Clear();
                pendingDeprecated = false;
                continue;
            }

            // Check for class / struct / enum definition opening a brace (skip forward declarations ending in ;)
            if (current.Contains('{'))
            {
                var bracePos = current.IndexOf('{');
                var headerPart = NormalizeSpaces(current[..bracePos]);
                var tailPart = current[(bracePos + 1)..];
                var typeMatch = CppTypeDeclRegex().Match(headerPart);
                if (typeMatch.Success && !headerPart.Contains('('))
                {
                    var typeKind = typeMatch.Groups[1].Value; // class | struct | enum
                    var typeName = typeMatch.Groups[2].Value;
                    var basesRaw = typeMatch.Groups[3].Value;
                    var bases = ParseBaseClasses(basesRaw);
                    var ns = CurrentQualifier();
                    var qualified = ns.Length > 0 ? $"{ns}::{typeName}" : typeName;
                    var sig = headerPart.Length > 220 ? headerPart[..220] + "..." : headerPart;
                    output.Add(new("cpp", typeKind, typeName, qualified, ns.Length > 0 ? ns : null,
                        bases.Length > 0 ? bases : null, sig, relativePath, declStartLine, "header", pendingDeprecated));

                    var openCount = current.Count(static c => c == '{');
                    var closeCount = current.Count(static c => c == '}');
                    braceDepth += openCount - closeCount;
                    if (openCount > closeCount)
                    {
                        scopes.Add(new(typeKind, typeName, braceDepth, typeKind != "class"));
                    }
                    else
                    {
                        // Single-line enum/struct like `enum class Foo { A, B };`
                        var closePos = tailPart.LastIndexOf('}');
                        if (typeKind == "enum" && closePos > 0)
                        {
                            scopes.Add(new("enum", typeName, braceDepth + 1, true));
                            FlushDeclaration(tailPart[..closePos], declStartLine, false, pendingDeprecated);
                            scopes.RemoveAt(scopes.Count - 1);
                        }
                        PopScopesTo(braceDepth);
                    }
                    declBuffer.Clear();
                    pendingDeprecated = false;
                    continue;
                }

                // Inline method or function with `{`
                FlushDeclaration(headerPart, declStartLine, true, pendingDeprecated);
                var deltaOpen = current.Count(static c => c == '{');
                var deltaClose = current.Count(static c => c == '}');
                braceDepth += deltaOpen - deltaClose;
                if (deltaOpen > deltaClose)
                    scopes.Add(new("body", "", braceDepth, false));
                else
                    PopScopesTo(braceDepth);
                declBuffer.Clear();
                pendingDeprecated = false;
                continue;
            }

            if (current.EndsWith(';') || (EnclosingType() is { Kind: "enum" } && current.EndsWith(',')))
            {
                FlushDeclaration(current.TrimEnd(';'), declStartLine, false, pendingDeprecated);
                declBuffer.Clear();
                pendingDeprecated = false;
            }
            else if (declBuffer.Length > 1024)
            {
                // Avoid runaway buffer on unusual macro blocks.
                declBuffer.Clear();
                pendingDeprecated = false;
            }
        }
    }

    private static void TryEmitCppField(
        string cleaned,
        string ownerQual,
        ScopeFrame? enclosing,
        string relativePath,
        int startLine,
        bool deprecated,
        List<AxmolApiSymbol> output)
    {
        if (enclosing is null) return;
        // Trim default initializer (`= nullptr`, `{}`).
        var eqIdx = FindTopLevelChar(cleaned, '=');
        var withoutInit = (eqIdx > 0 ? cleaned[..eqIdx] : cleaned).Trim();
        var match = CppFieldNameRegex().Match(withoutInit);
        if (!match.Success) return;
        var name = match.Groups[1].Value;
        // Skip private-style underscore fields unless std::function callbacks, to keep index signal high.
        if (name.StartsWith('_') && !cleaned.Contains("std::function", StringComparison.Ordinal)) return;
        if (!IsValidIdentifier(name)) return;
        var sig = cleaned.Length > 220 ? cleaned[..220] + "..." : cleaned;
        output.Add(new("cpp", "field", name, $"{ownerQual}::{name}", ownerQual, null, sig, relativePath, startLine, "header", deprecated));
    }

    private static string[] ParseBaseClasses(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var result = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = Regex.Replace(part, @"\b(public|protected|private|virtual)\b", "").Trim();
            if (cleaned.Length > 0 && cleaned.Length < 80) result.Add(cleaned);
        }
        return [.. result];
    }

    private static string StripDeclarationAttributes(string text)
    {
        var s = CppMacroStripRegex().Replace(text, " ");
        return NormalizeSpaces(s);
    }

    private static string TrimSignatureBody(string decl, bool openedBrace)
    {
        if (!openedBrace) return decl;
        // Drop constructor member-initializer list (`) : _a(0)`) if present after closing paren.
        var lastParen = decl.LastIndexOf(')');
        if (lastParen > 0)
        {
            var colon = decl.IndexOf(':', lastParen + 1);
            if (colon > 0) return decl[..colon].Trim();
        }
        return decl;
    }

    private static int FindTopLevelChar(string s, char target)
    {
        var angle = 0;
        var paren = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '(') { if (target == '(' && angle == 0 && paren == 0) return i; paren++; }
            else if (c == ')' && paren > 0) paren--;
            else if (c == target && angle == 0 && paren == 0) return i;
        }
        return -1;
    }

    private static bool IsValidIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
        for (var i = 1; i < s.Length; i++)
            if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
        return true;
    }

    private static string NormalizeSpaces(string s)
        => MultiSpaceRegex().Replace(s.Trim(), " ");

    /// <summary>Replaces // and /* */ comments with spaces while keeping every newline so line indices stay exact.</summary>
    private static string[] StripCppCommentsPreservingLines(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inBlock = false;
        var inLine = false;
        var inString = false;
        var quote = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '\r') continue;
            if (c == '\n')
            {
                inLine = false;
                inString = false;
                sb.Append('\n');
                continue;
            }

            if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
                continue;
            }
            if (inLine) continue;

            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && next != '\0') { sb.Append(next); i++; continue; }
                if (c == quote) inString = false;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                quote = c;
                sb.Append(c);
                continue;
            }
            if (c == '/' && next == '/') { inLine = true; i++; continue; }
            if (c == '/' && next == '*') { inBlock = true; i++; continue; }
            sb.Append(c);
        }

        return sb.ToString().Split('\n');
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // v3 Lua Manifest JSON Indexer (generated/manifests/*.json)
    // ─────────────────────────────────────────────────────────────────────────────

    internal static void IndexV3LuaManifest(string jsonText, string relativePath, List<AxmolApiSymbol> output)
    {
        // Build a fast line map from the pretty-printed manifest so each class/enum/member gets its exact line number.
        var lines = jsonText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var classLines = new Dictionary<string, int>(StringComparer.Ordinal);
        var enumLines = new Dictionary<string, int>(StringComparer.Ordinal);
        var memberLines = new Dictionary<string, int>(StringComparer.Ordinal);
        string? currentOwner = null;
        var section = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("\"classes\":", StringComparison.Ordinal)) { section = "classes"; currentOwner = null; continue; }
            if (line.Contains("\"enums\":", StringComparison.Ordinal)) { section = "enums"; currentOwner = null; continue; }

            var m = JsonStringPropRegex().Match(line);
            if (!m.Success) continue;
            var prop = m.Groups[1].Value;
            var val = m.Groups[2].Value;
            var indent = line.Length - line.TrimStart().Length;

            if (section == "classes")
            {
                if (indent <= 6 && prop == "luaName")
                {
                    currentOwner = val;
                    classLines.TryAdd(val, i + 1);
                }
                else if (currentOwner is not null && indent > 6 && prop == "luaName")
                {
                    memberLines.TryAdd($"{currentOwner}.{val}", i + 1);
                }
            }
            else if (section == "enums")
            {
                if (indent <= 6 && prop is "qualifiedName" or "nativeName")
                {
                    currentOwner = val;
                    enumLines.TryAdd(val, i + 1);
                }
                else if (currentOwner is not null && indent > 6 && prop == "name")
                {
                    memberLines.TryAdd($"{currentOwner}.{val}", i + 1);
                }
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.TryGetProperty("classes", out var classes) && classes.ValueKind == JsonValueKind.Array)
            {
                foreach (var cls in classes.EnumerateArray())
                {
                    var luaName = GetStr(cls, "luaName");
                    var luaClassName = GetStr(cls, "luaClassName");
                    var qualifiedName = GetStr(cls, "qualifiedName");
                    if (luaName.Length == 0) continue;
                    var clsLine = classLines.GetValueOrDefault(luaName, 1);

                    string[]? bases = null;
                    if (cls.TryGetProperty("bases", out var basesEl) && basesEl.ValueKind == JsonValueKind.Array)
                        bases = basesEl.EnumerateArray().Select(static b => b.GetString() ?? "").Where(static b => b.Length > 0).ToArray();

                    output.Add(new("lua", "class", luaClassName.Length > 0 ? luaClassName : luaName, luaName,
                        null, bases is { Length: > 0 } ? bases : null,
                        $"lua class {luaName} (native {qualifiedName})", relativePath, clsLine, "manifest"));

                    if (cls.TryGetProperty("methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var method in methods.EnumerateArray())
                        {
                            var mName = GetStr(method, "luaName");
                            if (mName.Length == 0) continue;
                            var ret = GetStr(method, "returnType");
                            var isStatic = method.TryGetProperty("isStatic", out var st) && st.ValueKind == JsonValueKind.True;
                            var paramList = method.TryGetProperty("parameterTypes", out var pt) && pt.ValueKind == JsonValueKind.Array
                                ? string.Join(", ", pt.EnumerateArray().Select(static p => p.GetString() ?? ""))
                                : "";
                            var sig = $"{(isStatic ? "static " : "")}{ret} {mName}({paramList})".Trim();
                            var line = memberLines.GetValueOrDefault($"{luaName}.{mName}", clsLine);
                            output.Add(new("lua", "method", mName, $"{luaName}.{mName}", luaName, null, sig, relativePath, line, "manifest"));
                        }
                    }

                    if (cls.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var field in fields.EnumerateArray())
                        {
                            var fName = GetStr(field, "luaName");
                            if (fName.Length == 0) continue;
                            var fType = GetStr(field, "fieldType");
                            var line = memberLines.GetValueOrDefault($"{luaName}.{fName}", clsLine);
                            output.Add(new("lua", "field", fName, $"{luaName}.{fName}", luaName, null,
                                $"{fType} {fName}".Trim(), relativePath, line, "manifest"));
                        }
                    }
                }
            }

            if (root.TryGetProperty("enums", out var enums) && enums.ValueKind == JsonValueKind.Array)
            {
                var luaNs = GetStr(root, "luaNamespace");
                foreach (var en in enums.EnumerateArray())
                {
                    var nativeName = GetStr(en, "nativeName");
                    var qualifiedName = GetStr(en, "qualifiedName");
                    if (nativeName.Length == 0) continue;
                    var luaEnumQual = luaNs.Length > 0 ? $"{luaNs}.{nativeName}" : nativeName;
                    var enLine = enumLines.GetValueOrDefault(qualifiedName, enumLines.GetValueOrDefault(nativeName, 1));
                    output.Add(new("lua", "enum", nativeName, luaEnumQual, luaNs.Length > 0 ? luaNs : null, null,
                        $"lua enum {luaEnumQual} (native {qualifiedName})", relativePath, enLine, "manifest"));

                    if (en.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var val in values.EnumerateArray())
                        {
                            var vName = GetStr(val, "name");
                            if (vName.Length == 0) continue;
                            var vLine = memberLines.GetValueOrDefault($"{qualifiedName}.{vName}", enLine);
                            output.Add(new("lua", "constant", vName, $"{luaEnumQual}.{vName}", luaEnumQual, null,
                                $"{luaEnumQual}.{vName}", relativePath, vLine, "manifest"));
                        }
                    }
                }
            }
        }
        catch (JsonException) { }
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // ─────────────────────────────────────────────────────────────────────────────
    // v3 Lua Manual Adapter Indexer (adapters/**/*.cpp)
    // ─────────────────────────────────────────────────────────────────────────────

    internal static void IndexV3LuaAdapter(string content, string relativePath, List<AxmolApiSymbol> output)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? currentOwner = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith('}')) { currentOwner = null; continue; }

            var pushMatch = V3AdapterPushRegex().Match(line);
            if (pushMatch.Success)
            {
                currentOwner = pushMatch.Groups[1].Value;
                continue;
            }

            if (currentOwner is not null)
            {
                // clang-format wraps long registration calls across two lines; peek one line ahead when needed.
                var stmt = line.Contains(';') ? line : (i + 1 < lines.Length ? line + " " + lines[i + 1].Trim() : line);
                var fnMatch = V3AdapterSetFuncRegex().Match(stmt);
                if (fnMatch.Success)
                {
                    var name = fnMatch.Groups[1].Value;
                    var target = fnMatch.Groups[2].Value;
                    output.Add(new("lua", "method", name, $"{currentOwner}.{name}", currentOwner, null,
                        $"{currentOwner}:{name} -> {target}", relativePath, i + 1, "adapter"));
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // v2 Lua tolua++ Indexer (auto/*.cpp and manual/**/*.cpp)
    // ─────────────────────────────────────────────────────────────────────────────

    internal static void IndexV2LuaCpp(string content, string relativePath, string bindingSource, List<AxmolApiSymbol> output)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? currentOwner = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith('}') || line.Contains("tolua_endmodule", StringComparison.Ordinal))
            {
                currentOwner = null;
                continue;
            }

            var classMatch = V2ToluaClassRegex().Match(line);
            if (classMatch.Success)
            {
                var shortName = classMatch.Groups[1].Value;
                var fullName = classMatch.Groups[2].Value;
                var baseName = classMatch.Groups[3].Value;
                currentOwner = fullName;
                output.Add(new("lua", "class", shortName, fullName, null,
                    baseName.Length > 0 ? [baseName] : null,
                    $"tolua class {fullName}" + (baseName.Length > 0 ? $" : {baseName}" : ""),
                    relativePath, i + 1, bindingSource));
                continue;
            }

            var pushMatch = V2LuaPushTypeRegex().Match(line);
            if (pushMatch.Success)
            {
                currentOwner = pushMatch.Groups[1].Value;
                continue;
            }

            if (currentOwner is not null)
            {
                // clang-format wraps long tolua_function calls across two lines; peek one line ahead when needed.
                var stmt = line.Contains(';') ? line : (i + 1 < lines.Length ? line + " " + lines[i + 1].Trim() : line);
                var fnMatch = V2ToluaFunctionRegex().Match(stmt);
                if (fnMatch.Success)
                {
                    var name = fnMatch.Groups[1].Value;
                    var cFunc = fnMatch.Groups[2].Value;
                    output.Add(new("lua", "method", name, $"{currentOwner}.{name}", currentOwner, null,
                        $"{currentOwner}:{name} -> {cFunc}", relativePath, i + 1, bindingSource));
                }
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"^namespace\s+([A-Za-z_][A-Za-z0-9_:]*)\b")]
    private static partial Regex CppNamespaceRegex();

    [GeneratedRegex(@"^(?:template\s*<[^>]*>\s*)?(class|struct|enum)(?:\s+class|\s+struct)?\s+(?:(?:AX_[A-Z0-9_]+|CC_[A-Z0-9_]+|\[\[[^\]]*\]\])\s+)*([A-Za-z_][A-Za-z0-9_]*)(?:\s+final)?(?:\s*:\s*(.*))?$")]
    private static partial Regex CppTypeDeclRegex();

    [GeneratedRegex(@"\b(?:AX_DLL|AX_EX_DLL|AX_GUI_DLL|AX_STUDIO_DLL|AX_FORCE_INLINE|AX_DEPRECATED_ATTRIBUTE|AX_DEPRECATED\s*\([^)]*\)|\[\[[^\]]*\]\])\b")]
    private static partial Regex CppMacroStripRegex();

    [GeneratedRegex(@"(~?[A-Za-z_][A-Za-z0-9_]*|operator\s*[^\s(]+)\s*$")]
    private static partial Regex CppCallableNameRegex();

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?\s*$")]
    private static partial Regex CppFieldNameRegex();

    [GeneratedRegex("^\\s*\"(luaName|nativeName|qualifiedName|name)\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex JsonStringPropRegex();

    [GeneratedRegex(@"(?:axlua::adapter::push_literal|lua_pushstring)\s*\([^,]+,\s*""((?:ax|axui|axr|axexp|cc|ccui|fgui|sp)\.[A-Za-z0-9_.]+)""\s*\)")]
    private static partial Regex V3AdapterPushRegex();

    [GeneratedRegex(@"axlua::adapter::set_function\s*\([^,]+,\s*""([A-Za-z0-9_]+)""\s*,\s*([A-Za-z0-9_:]+)")]
    private static partial Regex V3AdapterSetFuncRegex();

    [GeneratedRegex(@"tolua_cclass\s*\([^,]+,\s*""([^""]+)""\s*,\s*""([^""]+)""\s*,\s*""([^""]*)""")]
    private static partial Regex V2ToluaClassRegex();

    [GeneratedRegex(@"lua_pushstring\s*\([^,]+,\s*""((?:ax|axui|axr|axexp|cc|ccui|ccs|ccb|ccexp|fgui|sp)\.[A-Za-z0-9_.]+)""\s*\)")]
    private static partial Regex V2LuaPushTypeRegex();

    [GeneratedRegex(@"tolua_function\s*\([^,]+,\s*""([A-Za-z0-9_]+)""\s*,\s*([A-Za-z0-9_:]+)")]
    private static partial Regex V2ToluaFunctionRegex();
}
