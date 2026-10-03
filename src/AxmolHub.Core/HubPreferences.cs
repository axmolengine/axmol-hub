namespace AxmolHub.Core;

public sealed class HubPreferences
{
    /// <summary>冷启动语言：没有设置文件时的取值，由 <see cref="HubTexts.DefaultLanguage"/> 单点定义（英文）。</summary>
    public string Language { get; set; } = HubTexts.DefaultLanguage;
    public string? DataRoot { get; set; }
    public string? ProjectDirectory { get; set; }
}

public sealed class PreferencesStore(string path)
{
    public HubPreferences Load()
    {
        var preferences = File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<HubPreferences>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty Hub preferences.") : new();
        // 受支持的语言清单只有 HubTexts 一处定义：这里再写一遍字面量，就等于给自己留一个
        // "加了语言却忘了改这里"的静默分叉点。
        if (!HubTexts.IsSupported(preferences.Language)) preferences.Language = HubTexts.DefaultLanguage;
        return preferences;
    }
    public void Save(HubPreferences preferences) => StateStore.WriteJson(path, preferences);
    public static string VerifyDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (fullPath == Path.GetPathRoot(fullPath)) throw new IOException("Choose a dedicated Hub directory, not a drive root.");
        Directory.CreateDirectory(fullPath);
        var probe = Path.Combine(fullPath, ".hub-write-check-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, ""); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        return fullPath;
    }
}
