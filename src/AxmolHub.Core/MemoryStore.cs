using System.Text;

namespace AxmolHub.Core;

/// <summary>Whose memory this is: one project, or the user across projects.</summary>
public enum MemoryScope
{
    Project,
    Global,
}

/// <summary>One topic file as its frontmatter describes it.</summary>
public sealed record MemoryEntry(string Name, string Title, string Description, string Type, DateTimeOffset Updated);

public readonly record struct MemoryWriteResult(bool Written, string Message, string Path);

/// <summary>
/// The assistant's durable memory: topic files under <c>topics/</c>, each with frontmatter, plus an index that
/// is <b>derived</b> from that frontmatter on every write. Deriving it is the point — an index the model also
/// edits is an index that eventually disagrees with the files, and a stale index is worse than none because it
/// is injected into the system prompt every turn.
///
/// In a project workspace the index is <c>AXHUB.md</c>, never <c>MEMORY.md</c>: that name may already belong to
/// a person or to another agent working in the same repository, and Hub has no business rewriting it. Hub reads
/// it when it is there. Under the data root Hub owns the directory, so the index is <c>MEMORY.md</c>.
/// </summary>
public static class MemoryStore
{
    public const string TopicsDirectory = "topics";
    public const string ProjectIndexFile = "AXHUB.md";
    public const string GlobalIndexFile = "MEMORY.md";
    public const int MaxIndexCharacters = 2048;
    public const int MaxEntryBytes = 64 * 1024;

    public static string? RootFor(MemoryScope scope, string? workspaceRoot, string? dataRoot) => scope switch
    {
        MemoryScope.Global => string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "ai", "memory"),
        _ => string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.Combine(workspaceRoot, WorkspacePaths.MemoryDirectory),
    };

    public static string IndexFileName(MemoryScope scope) => scope == MemoryScope.Global ? GlobalIndexFile : ProjectIndexFile;

    /// <summary>Topic names are kebab-case with a <c>.md</c> suffix and nothing else: no separators, no drive,
    /// no index name. Keeping the shape narrow is what makes the containment check below a formality.</summary>
    public static bool IsTopicName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0) return false;
        if (string.Equals(name, ProjectIndexFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, GlobalIndexFile, StringComparison.OrdinalIgnoreCase)) return false;

        var stem = name[..^3];
        return stem.Length > 0
               && !stem.StartsWith('-') && !stem.EndsWith('-')
               && !stem.Contains("--", StringComparison.Ordinal)
               && stem.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
    }

    public static IReadOnlyList<MemoryEntry> Index(string root)
    {
        var topics = Path.Combine(root, TopicsDirectory);
        if (!Directory.Exists(topics)) return [];

        var entries = new List<MemoryEntry>();
        foreach (var file in Directory.EnumerateFiles(topics, "*.md").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (!IsTopicName(name)) continue;
            var document = Read(file);
            entries.Add(new MemoryEntry(
                name,
                document.Title.Length > 0 ? document.Title : TitleFrom(name),
                document.Description,
                document.Type,
                File.GetLastWriteTime(file)));
        }
        return entries;
    }

    public static string RenderIndex(IReadOnlyList<MemoryEntry> entries, int maxCharacters = MaxIndexCharacters)
    {
        var builder = new StringBuilder();
        var shown = 0;
        foreach (var entry in entries)
        {
            var line = $"- [{entry.Title}]({TopicsDirectory}/{entry.Name}) — {entry.Description}\n";
            if (builder.Length + line.Length > maxCharacters) break;
            builder.Append(line);
            shown++;
        }
        if (shown < entries.Count) builder.Append($"…(+{entries.Count - shown} 条)\n");
        return builder.ToString().TrimEnd();
    }

    public static MemoryWriteResult Write(
        string? root, MemoryScope scope, string name, string title, string description, string type, string content, bool append)
    {
        if (string.IsNullOrWhiteSpace(root)) return new MemoryWriteResult(false, NoRootMessage(scope), "");
        if (!IsTopicName(name)) return new MemoryWriteResult(false, BadNameMessage(name), "");
        if (string.IsNullOrWhiteSpace(content)) return new MemoryWriteResult(false, "Refused: the memory content is empty. Write what should be remembered, or leave the file alone.", "");

        var path = TopicPath(root, name);
        if (path is null) return new MemoryWriteResult(false, BadNameMessage(name), "");

        var existing = append && File.Exists(path) ? Read(path) : MemoryDocument.Empty;
        var body = append && existing.Body.Length > 0
            ? existing.Body.TrimEnd() + "\n\n" + content.Trim()
            : content.Trim();
        if (Encoding.UTF8.GetByteCount(body) > MaxEntryBytes)
            return new MemoryWriteResult(false, $"Refused: {name} would exceed {MaxEntryBytes / 1024} KiB. Split it into a second topic instead of growing this one.", path);

        var document = new MemoryDocument(
            First(title, existing.Title, TitleFrom(name)),
            First(description, existing.Description),
            First(type, existing.Type, "note"),
            body);
        WriteAtomic(path, Render(name, document));
        RefreshIndex(root, scope);
        return new MemoryWriteResult(true, $"Saved {TopicsDirectory}/{name}.", path);
    }

    /// <summary>Reads a topic's body, or an index file verbatim. Returns null when there is nothing to read,
    /// which the caller turns into a sentence the model can act on.</summary>
    public static string? ReadText(string? root, MemoryScope scope, string name)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(name)) return null;
        if (string.Equals(name, IndexFileName(scope), StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, GlobalIndexFile, StringComparison.OrdinalIgnoreCase))
        {
            // Hub never writes a project's MEMORY.md, but it reads one: a repository that already keeps memory
            // should not have to duplicate it for the assistant.
            var index = Path.Combine(root, name);
            return File.Exists(index) ? File.ReadAllText(index) : null;
        }

        if (!IsTopicName(name)) return null;
        var path = TopicPath(root, name);
        return path is not null && File.Exists(path) ? Read(path).Body : null;
    }

    private static string? TopicPath(string root, string name)
    {
        var topics = Path.GetFullPath(Path.Combine(root, TopicsDirectory));
        var path = Path.GetFullPath(Path.Combine(topics, name));
        return path.StartsWith(topics + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? path
            : null;
    }

    private sealed record MemoryDocument(string Title, string Description, string Type, string Body)
    {
        public static readonly MemoryDocument Empty = new("", "", "", "");
    }

    private static MemoryDocument Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return MemoryDocument.Empty;
        }

        if (!text.StartsWith("---", StringComparison.Ordinal)) return new MemoryDocument("", "", "", text.Trim());
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return new MemoryDocument("", "", "", text.Trim());

        var title = "";
        var description = "";
        var type = "";
        foreach (var line in text[3..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var value = line[(separator + 1)..].Trim();
            switch (line[..separator].Trim().ToLowerInvariant())
            {
                case "title": title = value; break;
                case "description": description = value; break;
                case "type": type = value; break;
            }
        }
        return new MemoryDocument(title, description, type, text[(end + 4)..].TrimStart('\r', '\n').Trim());
    }

    private static string Render(string name, MemoryDocument document) => new StringBuilder()
        .Append("---\n")
        .Append("name: ").Append(name[..^3]).Append('\n')
        .Append("title: ").Append(OneLine(document.Title)).Append('\n')
        .Append("description: ").Append(OneLine(document.Description)).Append('\n')
        .Append("type: ").Append(OneLine(document.Type)).Append('\n')
        .Append("---\n\n")
        .Append(document.Body.TrimEnd()).Append('\n')
        .ToString();

    /// <summary>Frontmatter is line-oriented, so a newline inside a value would forge the next key.</summary>
    private static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    private static void RefreshIndex(string root, MemoryScope scope)
    {
        var entries = Index(root);
        var body = new StringBuilder()
            .Append("<!-- 由 Axmol Hub 从 ").Append(TopicsDirectory).Append("/*.md 的 frontmatter 生成，改主题文件而不是改这里 -->\n\n")
            .Append(scope == MemoryScope.Global ? "# 全局记忆索引\n\n" : "# 项目记忆索引\n\n")
            .Append(entries.Count == 0 ? "（还没有记忆条目）\n" : RenderIndex(entries) + "\n")
            .ToString();
        WriteAtomic(Path.Combine(root, IndexFileName(scope)), body);
    }

    /// <summary>The same atomic shape <c>StateStore.WriteJson</c> uses: a partial write must never be readable
    /// as the whole file, because the next session injects this text into its system prompt.</summary>
    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string TitleFrom(string name)
    {
        var stem = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        return stem.Replace('-', ' ') is { Length: > 0 } words
            ? char.ToUpperInvariant(words[0]) + words[1..]
            : stem;
    }

    private static string First(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    private static string NoRootMessage(MemoryScope scope) => scope == MemoryScope.Project
        ? WorkspacePaths.ResultFor(WorkspacePathVerdict.NoWorkspace, "")
        : "Global memory is unavailable because Hub has no data root. Do not retry; tell the user.";

    private static string BadNameMessage(string name) =>
        $"Refused: '{name}' is not a memory topic name. Use kebab-case with a .md suffix (for example "
        + "build-conventions.md); the index files are generated and cannot be written. Do not retry this name.";
}
