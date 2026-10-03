using System.Text.Json;

namespace AxmolHub.Core;

public sealed class RecipeProfile
{
    public string EngineVersion { get; set; } = "";

    /// <summary>
    /// The "recipes" already verified for this engine version. A recipe is an implementation in code
    /// (currently the Android packaging check); the manifest only declares that it has been verified
    /// for this engine version — so supporting a new engine version means adding a profile, no C# change.
    /// </summary>
    public string[] VerifiedRecipes { get; set; } = [];
}

public sealed class RecipeManifest
{
    public List<RecipeProfile> Profiles { get; set; } = [];
}

/// <summary>
/// The "version verification boundary" for packaging recipes.
///
/// This boundary used to be a hard-coded literal (`engine.Version != "2.11.5"`). That was a
/// **code-level** engine-version binding — adding an engine version meant changing C#. Now the recipe
/// name and version are declared together in recipe-manifest.json.
///
/// A property kept deliberately: **still fail-closed**. An undeclared engine version is always
/// refused, never falling back to another version's profile. So this is not "loosening verification";
/// it is moving the boundary from code into data — allowing a version still requires someone to have
/// actually verified it first.
/// </summary>
public static class PackagingRecipes
{
    public const string AndroidPackaging = "android-packaging";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "manifests/recipe-manifest.json");
    public static void RequireVerified(EngineEntry engine, string recipe)
    {
        var path = ManifestPath;
        // A missing manifest also fails the check: this is a verification gate, so it must not pass just because the evidence could not be read.
        if (!File.Exists(path)) throw new InvalidOperationException($"Cannot verify {recipe} for Axmol {engine.Version}: recipe manifest not found at {path}.");
        var catalogue = JsonSerializer.Deserialize<RecipeManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty recipe manifest.");
        var profile = catalogue.Profiles.FirstOrDefault(item => item.EngineVersion == engine.Version)
            ?? throw new InvalidOperationException($"No verified recipe profile for Axmol {engine.Version}.");
        if (!profile.VerifiedRecipes.Contains(recipe, StringComparer.Ordinal))
            throw new InvalidOperationException($"Recipe {recipe} is not verified for Axmol {engine.Version}.");
    }
}
