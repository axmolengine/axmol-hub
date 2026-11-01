using System.Security.Cryptography;
using System.Text;

namespace AxmolHub.Core;

public enum TextFileState { Text, Missing, TooLarge, UnsupportedEncoding, Binary, Failed }
public enum TextLineEndings { None, Lf, CrLf, Cr, Mixed }
public enum TextReplaceVerdict { Written, Unchanged, ChangedSince, RefusedPath, Failed }
public sealed record TextReplaceResult(TextReplaceVerdict Verdict, string? Detail = null);

/// <summary>A bounded disk snapshot. Absence is not an empty file, and the identity includes BOM and EOL bytes.</summary>
public sealed class TextFileSnapshot
{
    internal byte[]? Bytes { get; }
    public TextFileState State { get; }
    public string Text { get; }
    public Encoding Encoding { get; }
    public bool HasBom => Encoding.GetPreamble().Length != 0;
    public TextLineEndings LineEndings { get; }
    public bool EndsInNewline => Text.EndsWith('\n') || Text.EndsWith('\r');
    public string? Identity { get; }
    public string? Detail { get; }

    internal TextFileSnapshot(TextFileState state, byte[]? bytes = null, string text = "",
        Encoding? encoding = null, string? detail = null)
    {
        State = state;
        Bytes = bytes;
        Text = text;
        Encoding = encoding ?? new UTF8Encoding(false, true);
        Identity = bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        LineEndings = TextFile.LineEndingsOf(text);
        Detail = detail;
    }
}

/// <summary>Strict decoding and the single guarded source-file replacement road. Journal ownership stays with
/// the caller. The gate coordinates Hub writers, not external processes; the final check is not a filesystem CAS.</summary>
public static class TextFile
{
    public const int MaxEditorBytes = 2 * 1024 * 1024;
    public const int MaxDiffBytes = 1024 * 1024;
    private const int TextDetectionCharacters = 20;
    private const int MaxTextDetectionBytes = 84;
    private sealed class Gate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }
    private static readonly Dictionary<string, Gate> Gates = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static TextFileSnapshot Read(string path, int maxBytes = MaxEditorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes) return new(TextFileState.TooLarge, detail: $"File exceeds {maxBytes} bytes.");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, (long)maxBytes - buffer.Length + 1))) > 0)
            {
                if (buffer.Length + count > maxBytes) return new(TextFileState.TooLarge, detail: $"File exceeds {maxBytes} bytes.");
                buffer.Write(chunk, 0, count);
            }
            return Decode(buffer.ToArray());
        }
        catch (FileNotFoundException) { return new(TextFileState.Missing); }
        catch (DirectoryNotFoundException) { return new(TextFileState.Missing); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(TextFileState.Failed, detail: ex.Message); }
    }

    /// <summary>Quickly classify the first 20 characters of a file without loading its full contents.</summary>
    public static bool HasTextFilePrefix(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> sample = stackalloc byte[MaxTextDetectionBytes];
            var length = 0;
            while (length < sample.Length)
            {
                var count = stream.Read(sample[length..]);
                if (count == 0) break;
                length += count;
            }
            return HasTextPrefix(sample[..length]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Classify at most the first 20 characters of proposed text content.</summary>
    public static bool HasTextContentPrefix(string text)
        => IsTextSample(text.AsSpan(0, Math.Min(text.Length, TextDetectionCharacters)));

    private static bool HasTextPrefix(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding;
        var skip = 0;
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
        { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
        { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        { encoding = new UTF8Encoding(true, true); skip = 3; }
        else if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
        { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
        { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else encoding = new UTF8Encoding(false, true);

        var length = bytes.Length - skip;
        while (length >= 0)
        {
            try
            {
                var sample = encoding.GetString(bytes.Slice(skip, length));
                return IsTextSample(sample.AsSpan(0, Math.Min(sample.Length, TextDetectionCharacters)));
            }
            catch (DecoderFallbackException) when (length > 0) { length--; }
        }
        return false;
    }

    private static bool IsTextSample(ReadOnlySpan<char> sample)
    {
        foreach (var character in sample)
            if (char.IsControl(character) && character is not ('\t' or '\r' or '\n' or '\f')) return false;
        return true;
    }

    public static TextFileSnapshot Decode(byte[] bytes)
    {
        // UTF-32 LE must precede UTF-16 LE because the first two BOM bytes are identical.
        Encoding encoding;
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
        { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
        { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        { encoding = new UTF8Encoding(true, true); skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else encoding = new UTF8Encoding(false, true);
        try
        {
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f')))
                return new(TextFileState.Binary, detail: "Text contains binary control characters.");
            return new(TextFileState.Text, (byte[])bytes.Clone(), text, encoding);
        }
        catch (DecoderFallbackException ex)
        { return new(TextFileState.UnsupportedEncoding, detail: ex.Message); }
    }

    public static TextLineEndings LineEndingsOf(string text)
    {
        var lf = false;
        var crlf = false;
        var cr = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf = true; i++; }
                else cr = true;
            }
            else if (text[i] == '\n') lf = true;
        }
        return (lf ? 1 : 0) + (crlf ? 1 : 0) + (cr ? 1 : 0) > 1 ? TextLineEndings.Mixed
            : lf ? TextLineEndings.Lf : crlf ? TextLineEndings.CrLf : cr ? TextLineEndings.Cr : TextLineEndings.None;
    }

    /// <summary>Acquire before reading the pre-image and hold through journal completion. Entries are retired
    /// after their last waiter, so visiting many files does not leave an unbounded path cache.</summary>
    public static WriteLease AcquireWrite(string path)
    {
        var full = Path.GetFullPath(path);
        var key = OperatingSystem.IsMacOS() ? full.Normalize(NormalizationForm.FormC) : full;
        Gate gate;
        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out gate!)) Gates.Add(key, gate = new Gate());
            gate.Users++;
        }
        gate.Semaphore.Wait();
        return new WriteLease(full, () =>
        {
            gate.Semaphore.Release();
            lock (Gates)
                if (--gate.Users == 0) { Gates.Remove(key); gate.Semaphore.Dispose(); }
        });
    }

    public sealed class WriteLease : IDisposable
    {
        private readonly string _full;
        private Action? _release;
        internal WriteLease(string full, Action release) { _full = full; _release = release; }

        /// <summary>Restore a pre-image without changing its original encoding or line endings.</summary>
        public TextReplaceResult ReplaceRaw(Func<WorkspacePath> resolveWrite, byte[]? expectedBytes, byte[] bytes)
        {
            ObjectDisposedException.ThrowIf(_release is null, this);
            string? temporary = null;
            TextReplaceResult? Validate()
            {
                var path = resolveWrite();
                if (!path.IsAllowed || path.Full != _full)
                    return new(TextReplaceVerdict.RefusedPath, "The permitted path changed. Reopen the file.");
                if (!Matches(_full, expectedBytes))
                    return new(TextReplaceVerdict.ChangedSince, "The file changed on disk. The buffer was kept.");
                return null;
            }
            try
            {
                if (Validate() is { } refused) return refused;
                Directory.CreateDirectory(Path.GetDirectoryName(_full)!);
                temporary = _full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                if (expectedBytes is not null && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(_full));
                if (Validate() is { } finalRefusal) return finalRefusal;
                if (expectedBytes is null) File.Move(temporary, _full);
                else File.Replace(temporary, _full, null);
                temporary = null;
                return new(TextReplaceVerdict.Written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { return new(TextReplaceVerdict.Failed, ex.Message); }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        /// <summary>A manual save does not enter the AI journal. An unchanged buffer must not touch any bytes,
        /// metadata or BOM, even if the source was CRLF. Fresh authorization and raw identity are still checked.</summary>
        public TextReplaceResult Save(Func<WorkspacePath> resolveWrite, TextFileSnapshot baseline, string text)
        {
            if (baseline.State is not (TextFileState.Text or TextFileState.Missing))
                return new(TextReplaceVerdict.Failed, "The baseline is not supported text.");
            return Replace(resolveWrite, baseline.Bytes, text, baseline.Encoding,
                unchanged: baseline.State == TextFileState.Text && text == baseline.Text);
        }

        /// <summary>Null replacement means delete, null expected bytes means absent. Every changed text write is
        /// LF; callers choose encoding explicitly, so editor formatting never changes the agent's UTF-8 contract.</summary>
        public TextReplaceResult Replace(Func<WorkspacePath> resolveWrite, byte[]? expectedBytes, string? text,
            Encoding? encoding = null, bool unchanged = false)
        {
            ObjectDisposedException.ThrowIf(_release is null, this);
            string? temporary = null;
            TextReplaceResult? Validate()
            {
                var path = resolveWrite();
                if (!path.IsAllowed || path.Full != _full)
                    return new(TextReplaceVerdict.RefusedPath, "The permitted path changed. Reopen the file.");
                if (!Matches(_full, expectedBytes))
                    return new(TextReplaceVerdict.ChangedSince, "The file changed on disk. The buffer was kept.");
                return null;
            }
            try
            {
                if (Validate() is { } refused) return refused;
                if (unchanged || text is null && expectedBytes is null) return new(TextReplaceVerdict.Unchanged);
                if (text is not null)
                {
                    encoding ??= new UTF8Encoding(false, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(_full)!);
                    temporary = _full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                               4096, FileOptions.WriteThrough))
                    {
                        stream.Write(encoding.GetPreamble());
                        stream.Write(encoding.GetBytes(TextEol.ToLf(text)));
                        stream.Flush(true);
                    }
                    if (expectedBytes is not null && !OperatingSystem.IsWindows())
                        File.SetUnixFileMode(temporary, File.GetUnixFileMode(_full));
                }
                // Resolve permissions and all path components again after staging. No direct-overwrite fallback.
                if (Validate() is { } finalRefusal) return finalRefusal;
                if (text is null) File.Delete(_full);
                else if (expectedBytes is null) File.Move(temporary!, _full);
                else File.Replace(temporary!, _full, null);
                temporary = null;
                return new(TextReplaceVerdict.Written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or EncoderFallbackException)
            { return new(TextReplaceVerdict.Failed, ex.Message); }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private static bool Matches(string path, byte[]? expected)
    {
        // Opening rather than File.Exists distinguishes errors from absence and does not read an arbitrarily
        // enlarged file into memory during the last-moment conflict check.
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (expected is null || stream.Length != expected.Length) return false;
            var buffer = new byte[8192];
            var offset = 0;
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                if (offset + count > expected.Length || !buffer.AsSpan(0, count).SequenceEqual(expected.AsSpan(offset, count))) return false;
                offset += count;
            }
            return offset == expected.Length;
        }
        catch (FileNotFoundException) { return expected is null; }
        catch (DirectoryNotFoundException) { return expected is null; }
    }
}
