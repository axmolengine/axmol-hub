using System.Text;
using System.Text.RegularExpressions;

namespace AxmolHub.Core;

/// <summary>Extra engine arguments, with -xc separated so it can be merged with Hub's managed options.</summary>
public sealed record ProjectBuildArguments(string[] Arguments, string[] CmakeOptions)
{
    // Target/configuration and operation switches are owned by Hub. Tool destinations and packaging
    // must also keep the engine-tree/APK contracts used by tool detection and artifact discovery.
    private static readonly HashSet<string> Managed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "a", "d", "O", "c", "f", "sdk", "i", "u", "dm", "prefix", "scope", "xt", "aab",
    };
    // PowerShell accepts abbreviated names for these switches before the engine's own argument loop runs.
    private static readonly string[] ModeSwitches = ["configOnly", "setupOnly", "forceConfig", "ndkOnly", "rebuild", "EngineRoot"];
    // Only engine options whose meaning has been reviewed are forwarded; unknown options are errors.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cc", "t", "xc", "xb", "j", "minsdk", "ide",
    };
    private static readonly Regex ManagedDefinition = new(
        @"^(?:AX_PREBUILT_DIR|AX_ROOT|AXMOL_ROOT|CMAKE_BUILD_TYPE|CMAKE_CONFIGURATION_TYPES|CMAKE_HOME_DIRECTORY|CMAKE_SYSTEM_NAME|CMAKE_SYSTEM_PROCESSOR|CMAKE_OSX_ARCHITECTURES|CMAKE_OSX_SYSROOT|CMAKE_GENERATOR_PLATFORM|CMAKE_TOOLCHAIN_FILE|ANDROID_ABI)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ProjectBuildArguments Parse(bool enabled, string text)
    {
        if (!enabled) return new([], []);
        var tokens = Tokenize(TextEol.ToLf(text));
        var arguments = new List<string>();
        var cmake = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith('-') || token.StartsWith("--") || token.Length == 1)
                throw Invalid("Expected an axmol build option: " + token);
            var name = token[1..].Split(':', '=')[0];
            if (Managed.Contains(name) || Regex.IsMatch(name, @"^O(?:[-+]?\d|$)", RegexOptions.IgnoreCase)
                || ModeSwitches.Any(mode => mode.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                throw Invalid("Hub manages this option: " + token);

            if (token == "-" + name && Regex.IsMatch(name, @"^j\d+$"))
            {
                ValidateJobs(name[1..]);
                arguments.Add(token);
                continue;
            }
            if (name == "dll" && token == "-dll")
            {
                arguments.Add(token);
                continue;
            }
            if (ValueOptions.Contains(name))
            {
                if (token != "-" + name || index + 1 == tokens.Count || tokens[index + 1].Length == 0)
                    throw Invalid("This option needs a separate, nonempty value: " + token);
                var value = tokens[++index];
                if (name.Equals("j", StringComparison.OrdinalIgnoreCase)) ValidateJobs(value);
                if (name.Equals("xc", StringComparison.OrdinalIgnoreCase))
                {
                    var options = value.Split(',').Select(option => option.Trim()).ToArray();
                    foreach (var option in options)
                    {
                        if (option.Length == 0) throw Invalid("-xc contains an empty option.");
                        ValidateCmake(option);
                    }
                    cmake.AddRange(options);
                }
                else
                {
                    if (value.StartsWith('-') && !name.Equals("xb", StringComparison.OrdinalIgnoreCase))
                        throw Invalid("Missing value for " + token);
                    if (name.Equals("xb", StringComparison.OrdinalIgnoreCase)) ValidateBuildOptions(value);
                    arguments.Add(token);
                    arguments.Add(value);
                }
            }
            else
            {
                throw Invalid("Unsupported additional option: " + token);
            }
        }
        return new(arguments.ToArray(), cmake.ToArray());
    }

    private static void ValidateJobs(string value)
    {
        if (!int.TryParse(value, out var jobs) || jobs <= 0) throw Invalid("Job count must be a positive integer.");
    }

    private static void ValidateCmake(string option)
    {
        // Limit -xc to definitions, not CMake actions, presets, cache scripts or source/build paths.
        var definition = Regex.Match(option, @"^-(D|P)([A-Za-z_][A-Za-z0-9_]*)(?::[A-Za-z]+)?=");
        if (!definition.Success) throw Invalid("-xc supports -DNAME=value or -PNAME=value definitions only: " + option);
        var name = definition.Groups[2].Value;
        if (name.StartsWith("_1K_", StringComparison.OrdinalIgnoreCase)) name = name[4..];
        if (ManagedDefinition.IsMatch(name) || name.StartsWith("__1K", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Hub manages this CMake/Gradle definition: " + option);
    }

    private static void ValidateBuildOptions(string value)
    {
        var options = value.Split(',');
        for (var index = 0; index < options.Length; index++)
        {
            var option = options[index].Trim();
            if (option == "--verbose") continue;
            if (option is "--parallel" or "--target")
            {
                if (++index == options.Length || string.IsNullOrWhiteSpace(options[index]) || options[index].StartsWith('-'))
                    throw Invalid("Missing value for build option " + option);
                if (option == "--parallel") ValidateJobs(options[index]);
                continue;
            }
            throw Invalid("-xb supports --parallel, --target and --verbose only: " + option);
        }
    }

    /// <summary>Whitespace separates arguments; quotes group values, doubled quotes embed a quote,
    /// and paths keep literal backslashes. No shell expansion or command evaluation is supported.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var value = new StringBuilder();
        char quote = '\0';
        var started = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '\0' || (char.IsControl(ch) && !char.IsWhiteSpace(ch)))
                throw Invalid("Control characters are not supported.");
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote)
                    {
                        value.Append(quote);
                        index++;
                    }
                    else quote = '\0';
                }
                else value.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (started)
                {
                    tokens.Add(value.ToString());
                    value.Clear();
                    started = false;
                }
            }
            else if (ch is '\'' or '"')
            {
                quote = ch;
                started = true;
            }
            else
            {
                if (ch is ';' or '|' or '&' or '>' or '<' or '`' or '$')
                    throw Invalid("Shell syntax is not supported; quote literal values.");
                value.Append(ch);
                started = true;
            }
        }
        if (quote != '\0') throw Invalid("A quoted value is not closed.");
        if (started) tokens.Add(value.ToString());
        return tokens;
    }

    private static InvalidDataException Invalid(string detail) => new("Invalid additional build parameters. " + detail);
}
