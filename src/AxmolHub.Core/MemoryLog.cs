using System.Text;

namespace AxmolHub.Core;

/// <summary>What one run is worth remembering in the project's daily log.</summary>
public sealed record MemoryRunSummary(
    string ConversationId,
    string Title,
    string Mode,
    IReadOnlyList<string> ToolOutcomes,
    string? StopReason,
    bool Compacted);

public readonly record struct MemoryLogAppend(bool Written, string Path, string Message);

/// <summary>
/// The project's daily log: one file per day at <c>.agents/memory/&lt;yyyy-MM-dd&gt;.md</c>. Hub writes it and
/// the model never retrieves from it — it is the human-facing trail of what the assistant did in this
/// repository, and injecting it would spend the window on prose the summary layer already covers.
///
/// One file per day means parallel sessions append to the same file. The append excludes other writers for the
/// length of one small block and retries, which is what keeps both sessions' lines: sharing the file between
/// writers instead loses updates silently, because each buffers against the end-of-file it saw at open.
/// </summary>
public static class MemoryLog
{
    public const int MaxFileBytes = 256 * 1024;
    private const int Attempts = 3;

    public static string DirectoryFor(string workspaceRoot) => Path.Combine(workspaceRoot, WorkspacePaths.MemoryDirectory);

    public static string FileFor(string workspaceRoot, DateTimeOffset day)
        => Path.Combine(DirectoryFor(workspaceRoot), $"{day:yyyy-MM-dd}.md");

    public static string ShortId(string conversationId)
        => string.IsNullOrEmpty(conversationId) ? "—" : conversationId.Length <= 8 ? conversationId : conversationId[..8];

    /// <summary>Built from facts the run already produced — no model call, so the log costs nothing and cannot
    /// invent anything.</summary>
    public static IReadOnlyList<string> LinesForRun(MemoryRunSummary summary, DateTimeOffset at)
    {
        var lines = new List<string> { $"## {at:HH:mm} · 会话 {ShortId(summary.ConversationId)} · {summary.Title}" };
        if (summary.ToolOutcomes.Count == 0 && summary.StopReason is null && !summary.Compacted)
        {
            lines.Add("- 只对话，没有调用工具");
            return lines;
        }

        foreach (var outcome in summary.ToolOutcomes) lines.Add($"- 工具：{outcome}");
        if (summary.Compacted) lines.Add("- 已自动压缩上下文");
        if (summary.StopReason is { Length: > 0 }) lines.Add($"- 结束：{summary.StopReason}");
        return lines;
    }

    /// <summary>Written at the moment compaction happens rather than folded into the run's closing block: a long
    /// session may compact several times, and a run killed by hand would otherwise leave no trace of any of
    /// them.</summary>
    public static IReadOnlyList<string> LinesForCompaction(string conversationId, DateTimeOffset at)
        => [$"- {at:HH:mm} · 会话 {ShortId(conversationId)} 自动压缩了上下文"];

    public static MemoryLogAppend Append(string path, IReadOnlyList<string> lines, int maxFileBytes = MaxFileBytes)
    {
        if (lines.Count == 0) return new MemoryLogAppend(false, path, "nothing to record");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var block = new StringBuilder();
        if (!File.Exists(path)) block.Append($"# {Path.GetFileNameWithoutExtension(path)}\n\n");
        foreach (var line in lines) block.Append(line).Append('\n');
        block.Append('\n');
        var bytes = Encoding.UTF8.GetBytes(block.ToString());

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // FileShare.Read, not ReadWrite: two writers sharing the file each buffer against the EOF they
                // saw at open, and the second flush overwrites the first — measured as 8 lost lines out of 40
                // on two parallel sessions. Excluding other writers plus a bounded retry is what actually
                // serializes them; the block is small, so nobody waits long.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (stream.Length >= maxFileBytes) return new MemoryLogAppend(false, path, $"log reached its {maxFileBytes / 1024} KiB cap");
                if (stream.Length + bytes.Length > maxFileBytes)
                {
                    stream.Write(Encoding.UTF8.GetBytes($"…（日志已达 {maxFileBytes / 1024} KiB 上限，后续记录停止写入）\n"));
                    return new MemoryLogAppend(false, path, "log reached its cap while writing this run");
                }

                stream.Write(bytes);
                return new MemoryLogAppend(true, path, "");
            }
            catch (IOException) when (attempt < Attempts)
            {
                // Another session in the same repository is mid-append.
                Thread.Sleep(attempt * 10);
            }
        }
    }
}
