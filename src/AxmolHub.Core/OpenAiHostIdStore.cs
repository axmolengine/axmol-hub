namespace AxmolHub.Core;

/// <summary>Persists the opaque identifier for the ChatGPT plan usage host in the authentication root.</summary>
public sealed class OpenAiHostIdStore(string root)
{
    private string PathName => Path.Combine(root, "ai", "openai-host-id");

    /// <summary>Returns the existing UUID host identifier or atomically creates one on first use.</summary>
    public string GetOrCreate()
    {
        var path = PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate = AcquireLock(path + ".lock");
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, "urn:uuid:" + Guid.NewGuid().ToString("D") + "\n");
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        var id = File.ReadAllText(path).TrimEnd('\n');
        const string prefix = "urn:uuid:";
        var uuid = id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : "";
        if (uuid.Length != 36 || !Guid.TryParseExact(uuid, "D", out var parsed)
            || parsed.ToString("D") != uuid || uuid[14] != '4' || "89ab".IndexOf(uuid[19]) < 0)
            throw new InvalidOperationException("The OpenAI host identifier is invalid; restore the original host identifier before signing in.");
        return id;
    }

    private static FileStream AcquireLock(string path)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(10); }
        }
    }
}
