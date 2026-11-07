using System.Net.Http.Headers;

namespace AxmolHub.Core;

/// <summary>
/// The headers a provider's manifest declared as static (<see cref="ModelProvider.ExtraHeaders"/>), and the one
/// place that decides which declared names are refused.
///
/// <para><b>Why this is not a method on the policy.</b> <c>ProviderRequestPolicy</c> is a connector
/// <c>PipelinePolicy</c> and lives in AxmolHub.Agent, which AxmolHub.Core cannot reference — while the callers that
/// most need this rule are a plain <see cref="HttpRequestMessage"/> send in Core (both catalog sources) and one in
/// the app (the key probe), neither of which has a pipeline to hang a policy on. Putting the rule here gives every
/// one of them the same answer, and lets the policy call it instead of keeping a second loop.</para>
///
/// <para><b>Three names are refused.</b> <c>authorization</c>, because the bearer is built from the credential and
/// a declared copy would replace the real token with a stale one — the failure a gateway reports as a confusing 4xx
/// rather than as a duplicate header. <c>host</c> and <c>content-length</c>, because both describe a request this
/// code only partly knows: the first decides which service is asked, and the second has to agree with a body that
/// is not visible here. Everything else goes through
/// <see cref="HttpRequestHeaders.TryAddWithoutValidation"/> — a manifest is data, and a value that a strongly typed
/// header would reject as malformed is still the value the service asked for.</para>
/// </summary>
public static class ProviderStaticHeaders
{
    /// <summary>Names the credential writer, the endpoint writer and the transport own.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "host",
        "content-length",
    };

    /// <summary>The declared headers, in declaration order, minus the reserved names.</summary>
    public static IEnumerable<(string Name, string Value)> Of(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) yield break;
        foreach (var (name, value) in headers)
            if (name.Length > 0 && !Reserved.Contains(name))
                yield return (name, value);
    }

    /// <summary>The headers a provider request carries. An explicitly declared <c>user-agent</c> wins; otherwise
    /// Hub identifies itself with its stable major/minor product version.</summary>
    public static IEnumerable<(string Name, string Value)> Effective(IReadOnlyDictionary<string, string>? headers)
    {
        var hasUserAgent = false;
        foreach (var (name, value) in Of(headers))
        {
            if (name.Equals("user-agent", StringComparison.OrdinalIgnoreCase)) hasUserAgent = true;
            yield return (name, value);
        }

        if (!hasUserAgent) yield return ("user-agent", AxmolHubUserAgent.Value);
    }

    /// <summary>
    /// Writes the effective provider headers onto a request Hub assembles itself.
    ///
    /// <para>Remove-then-add rather than add outright: <see cref="HttpRequestHeaders"/> has no setter,
    /// <c>TryAddWithoutValidation</c> appends a second value to a name already present, and a gateway that asked
    /// for the header is entitled to reject the duplicate instead. The case that matters is <c>user-agent</c> — a
    /// handler or a connector has usually put its own there already, and a declared value is meant to replace it,
    /// not to ride along beside it.</para>
    /// </summary>
    public static void ApplyTo(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        foreach (var (name, value) in Effective(headers))
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
