namespace AxmolHub.Core;

public sealed class HubPreferences
{
    /// <summary>Cold-start language: the value used when there is no settings file, defined in a single place by <see cref="HubTexts.DefaultLanguage"/> (English).</summary>
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
        // The list of supported languages is defined in exactly one place, HubTexts: duplicating the
        // literal here would create a silent fork point where "a language was added but this spot was
        // forgotten".
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
