using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// The server-side tools a provider can be asked to run <b>inside its own service</b>, named in
/// <c>serverTools</c> in the manifest and in the user's own <c>ai/providers.json</c>.
///
/// <para>These are not Hub tools. Nothing here has a body, a sandbox, or an approval card: the declaration tells
/// the endpoint "you may search", the endpoint decides when to do it, and the answer arrives as part of the
/// assistant's own reply. That is the shape the two shipping benchmarks of this app settled on — GitHub Copilot's
/// SDK carries a live progress event for a <i>provider-hosted</i> tool whose only kind today is
/// <c>web_search</c>, and stores the resulting items "verbatim for replay" on its side of the wire.</para>
///
/// <para>The name is the provider's, not ours: a hosted tool is advertised by the kind string its service
/// recognises. That is also why the pair on screen reads <c>web_fetch</c> (a Hub body) and <c>web_search</c>
/// (their body) rather than two verbs.</para>
/// </summary>
public static class ProviderServerTools
{
    /// <summary>The only one any gateway measured in this build has implemented.</summary>
    public const string WebSearch = "web_search";

    public static bool IsKnown(string name) => string.Equals(name, WebSearch, StringComparison.Ordinal);

    /// <summary>Whether a declaration list names hosted search. Ordinal, because the kind string goes on the wire
    /// verbatim and a case-folded match would let a manifest say <c>Web_Search</c> and send nothing.</summary>
    public static bool DeclaresWebSearch(IReadOnlyList<string>? declared)
        => declared is not null && declared.Contains(WebSearch, StringComparer.Ordinal);
}

/// <summary>What decided a hosted-search declaration one way or the other.</summary>
public enum WebSearchHostedVerdict
{
    /// <summary>Send it: declared, in a mode that has tools, and allowed out.</summary>
    On,

    /// <summary>Neither the manifest nor the user's provider entry names <c>web_search</c>. The default for every
    /// provider shipping today, and the one the decision table exists to make visible rather than infer.</summary>
    NotDeclared,

    /// <summary>The outbound switch is off. Same preference that governs <see cref="WebFetch"/> — see
    /// <see cref="WebSearchHosted.Decide"/> for why one switch covers both.</summary>
    SwitchedOff,

    /// <summary>The mode sends no tools at all, so there is nowhere to put the declaration. Ask mode is the case
    /// this names.</summary>
    ModeWithoutTools,
}

/// <summary>
/// The decision, with the field it will be written into. <see cref="WireField"/> is not decoration: it is the
/// thing a self-check can compare the serialized request against, which is the only way "we declared it" and
/// "the bridge sent it" stay one statement rather than two that can drift.
/// </summary>
/// <param name="Verdict">Which of the four answers this is.</param>
/// <param name="WireField">The request field the declaration lands in on this wire — empty when the verdict is
/// not <see cref="WebSearchHostedVerdict.On"/>.</param>
/// <param name="Reason">One English line for the audit trail and the headless probe.</param>
public readonly record struct WebSearchHostedDecision(WebSearchHostedVerdict Verdict, string WireField, string Reason);

/// <summary>
/// Whether this request should tell its endpoint that it may search the web — one pure function over four facts,
/// because everything about this feature except the decision is somebody else's code.
///
/// <para><b>Measured, not assumed (2026-10-10, against the shipped packages, on a loopback URL).</b> Both of this
/// build's bridges translate the declaration; they do not ignore it and they do not throw. On
/// <c>chat/completions</c> it becomes the top-level <c>web_search_options</c> object; on <c>responses</c> it
/// becomes an entry <c>{"type":"web_search"}</c> in <c>tools</c>, beside the function tools that keep riding in
/// the same array. So the wire is not a gate any more, and what is left to gate is a claim the endpoint has to
/// honour: a gateway that never implemented search will either ignore the field or answer 400 to it, and only one
/// real request to the user's own endpoint can tell which. That is why <c>serverTools</c> ships empty everywhere
/// and why the field is user-editable — the switch to try it is a line in a JSON file, not a build.</para>
///
/// <para><b>One switch, not two.</b> A hosted search leaves the machine in the same direction as
/// <see cref="WebFetch"/> and with the same reach — the provider's servers fetch the page, not this process — so
/// a second preference would be a second answer to a question only asked once. It is a weaker gate though, and
/// that is said out loud rather than smoothed over: no approval tier can stop a hosted search per call, because
/// the endpoint decides mid-generation and never asks Hub. The approval ladder governs the tools this process
/// runs; a server-side tool is governed only by whether the request that offered it was allowed to leave.</para>
/// </summary>
public static class WebSearchHosted
{
    /// <summary>The field on the chat-completions body the bridge writes the declaration into.</summary>
    public const string ChatWireField = "web_search_options";

    /// <summary>The array on the responses body, named by the tool entry it gains.</summary>
    public const string ResponsesWireField = "tools[].type=web_search";

    /// <summary>Which request field carries the declaration on this wire. Unknown protocols answer as chat, the
    /// same way <see cref="ModelCapabilities.Protocol"/> and the client factory do — silence is chat, because every
    /// provider but Copilot publishes nothing about endpoints.</summary>
    public static string WireFieldFor(string? protocol)
        => string.Equals(protocol, ModelProtocols.Responses, StringComparison.OrdinalIgnoreCase)
            ? ResponsesWireField
            : ChatWireField;

    /// <summary>
    /// The whole table. Read the arguments in the order they bite: a provider that never declared it is not
    /// affected by the switch, and a mode that sends no tools is not affected by either.
    /// </summary>
    public static WebSearchHostedDecision Decide(
        IReadOnlyList<string>? declared, string? protocol, string? mode, bool outboundAllowed)
    {
        if (!ProviderServerTools.DeclaresWebSearch(declared))
            return new WebSearchHostedDecision(WebSearchHostedVerdict.NotDeclared, "",
                $"nothing declares {ProviderServerTools.WebSearch} for this provider (manifest serverTools and "
                + "the provider's own entry are both silent)");

        if (!outboundAllowed)
            return new WebSearchHostedDecision(WebSearchHostedVerdict.SwitchedOff, "",
                "this Hub has outbound fetching turned off (设置 → 允许助手抓取网页), so no search is offered to the "
                + "endpoint either");

        if (string.Equals(mode, ChatModes.Ask, StringComparison.OrdinalIgnoreCase))
            return new WebSearchHostedDecision(WebSearchHostedVerdict.ModeWithoutTools, "",
                $"the {ChatModes.Ask} mode sends no tools, so there is nowhere to put the declaration");

        var field = WireFieldFor(protocol);
        return new WebSearchHostedDecision(WebSearchHostedVerdict.On, field,
            $"declared by the provider and sent on the {field} field of a "
            + (string.Equals(protocol, ModelProtocols.Responses, StringComparison.OrdinalIgnoreCase)
                ? ModelProtocols.Responses
                : ModelProtocols.Chat)
            + " request; the endpoint decides when to search and no card can stop it mid-reply");
    }

    /// <summary>The line the headless probe prints when the verdict is not <see cref="WebSearchHostedVerdict.On"/>:
    /// refusal first, and what would have to change second, in that order — the same rule every other refusal in
    /// this folder follows.</summary>
    public static string Explain(WebSearchHostedDecision decision) => decision.Verdict switch
    {
        WebSearchHostedVerdict.On => $"offered on {decision.WireField}",
        WebSearchHostedVerdict.NotDeclared =>
            $"Not offered: add \"{ProviderServerTools.WebSearch}\" to serverTools in ai/providers.json for this "
            + $"provider (or in the built-in manifest) — {decision.Reason}.",
        WebSearchHostedVerdict.SwitchedOff =>
            $"Not offered: the outbound switch is off — {decision.Reason}. Turn it on in "
            + "「设置 → 工具权限」and the next request offers search again.",
        WebSearchHostedVerdict.ModeWithoutTools =>
            $"Not offered: {decision.Reason}. Switch the conversation to plan or agent mode.",
        _ => decision.Reason,
    };
}

/// <summary>
/// What an endpoint reported about one search it ran for itself. Recorded, priced, and drawn — never replayed.
///
/// <para><b>Why it is not sent back, measured rather than assumed.</b> Feeding an assistant message that carries a
/// search item through either bridge produces nothing useful: on <c>chat/completions</c>
/// the message serializes as <c>{"role":"assistant","content":""}</c> — the item is dropped <i>and</i> the text
/// with it — and on <c>responses</c> the message disappears from <c>input</c> entirely. Neither throws, which is
/// the dangerous part: a history that quietly loses a turn is a conversation whose next reply contradicts the
/// last one. So Hub keeps this as a fact about the transcript it shows and saves, and leaves replay to whoever
/// owns the conversation state — Copilot's SDK stores those items "verbatim for replay" on its side, which is
/// the honest place for that job.</para>
/// </summary>
/// <param name="CallId">The provider's own id for the search, when it sent one. Used to keep two searches in one
/// reply apart, and nothing else — the value never goes back on the wire.</param>
/// <param name="Queries">What the endpoint said it looked for, in the order it said so.</param>
/// <param name="Sources">The addresses it read, de-duplicated. May be empty: a search whose answer cites nothing
/// is a search the model did not read, and the row says so rather than inventing a source.</param>
/// <param name="Started">Whether this notice is the live progress signal (a search is running) rather than the
/// finished one. Only the finished notice is stored on a turn; a start is drawn and then replaced.</param>
public readonly record struct ServerSearchNotice(
    string CallId,
    IReadOnlyList<string> Queries,
    IReadOnlyList<string> Sources,
    bool Started)
{
    /// <summary>Nothing worth recording: an item that carried neither a query nor a source. Answered as no notice
    /// rather than as an empty row, because an activity line that says "searched for nothing" is noise the user
    /// has to read past.</summary>
    public bool IsEmpty => Queries.Count == 0 && Sources.Count == 0;

    /// <summary>The stored line for this one search. A turn keeps a <see cref="ServerSearchLog"/>, which is what
    /// writes and reads the field; this exists so a caller holding one notice has something to ask for.</summary>
    public string? ToStored() => new ServerSearchLog([this]).ToStored();

    /// <summary>The newest search in a stored line, or null when the turn says nothing. Prefer
    /// <see cref="ServerSearchLog.Read"/>, which is what wrote it and which keeps every search rather than one.</summary>
    public static ServerSearchNotice? Parse(string? stored)
    {
        var log = ServerSearchLog.Read(stored);
        return log.Entries.Count == 0 ? null : log.Entries[^1];
    }
}

/// <summary>
/// The searches one assistant reply reports, stored on that turn as <see cref="ChatTurn.WebSearch"/>.
///
/// <para>A list rather than one entry because a reply can make the endpoint search twice — once for the question
/// and once for the thing it turned out to mean — and a transcript that kept only the last would say the model
/// answered from a query it never asked. One compact JSON line, written and read by this type alone, so the
/// format has exactly one owner.</para>
///
/// <para><b>Read-back is tolerant by design.</b> Anything this build cannot make sense of comes back as no
/// searches rather than as a failure: a conversation file written by a newer Hub has to keep opening, and losing
/// one activity line is cheap while refusing to load a session is not.</para>
/// </summary>
public sealed record ServerSearchLog(IReadOnlyList<ServerSearchNotice> Entries)
{
    /// <summary>Nothing was searched, or nothing worth showing.</summary>
    public static readonly ServerSearchLog Empty = new([]);

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>Every address across every search, in the order they were first reported, de-duplicated. Shown
    /// under the reply: a search that cited nothing has to read as unread rather than as an answer from nowhere.</summary>
    public IReadOnlyList<string> Sources()
    {
        var seen = new List<string>();
        foreach (var entry in Entries)
            foreach (var source in entry.Sources)
                if (!seen.Contains(source, StringComparer.Ordinal)) seen.Add(source);
        return seen;
    }

    /// <summary>The queries, in order, de-duplicated. Empty while a search is still running, which is what the
    /// "searching" state is drawn from.</summary>
    public IReadOnlyList<string> Queries()
    {
        var seen = new List<string>();
        foreach (var entry in Entries)
            foreach (var query in entry.Queries)
                if (!seen.Contains(query, StringComparer.Ordinal)) seen.Add(query);
        return seen;
    }

    /// <summary>The stored line, or null when the log has nothing to say. Null rather than an empty document
    /// because "this build looked and found nothing" and "this turn never searched" have to stay different after a
    /// reload — the first is a fact about a gateway, the second is the ordinary case.</summary>
    public string? ToStored()
    {
        var payload = new ServerSearchPayload
        {
            Searches = [.. Entries
                .Where(entry => !entry.IsEmpty)
                .Select(entry => new ServerSearchEntry
                {
                    Id = entry.CallId,
                    Queries = [.. entry.Queries],
                    Sources = [.. entry.Sources],
                })],
        };
        return payload.Searches.Count == 0 ? null : JsonSerializer.Serialize(payload, Stored);
    }

    public static ServerSearchLog Read(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<ServerSearchPayload>(stored, Stored);
            if (payload?.Searches is not { Count: > 0 } rows) return Empty;
            var entries = rows
                .Select(row => new ServerSearchNotice(row.Id ?? "", row.Queries ?? [], row.Sources ?? [], false))
                .Where(entry => !entry.IsEmpty)
                .ToList();
            return entries.Count == 0 ? Empty : new ServerSearchLog(entries);
        }
        catch (JsonException) { return Empty; }
        catch (InvalidOperationException) { return Empty; }
    }

    private static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web);

    private sealed class ServerSearchPayload
    {
        public List<ServerSearchEntry> Searches { get; set; } = [];
    }

    private sealed class ServerSearchEntry
    {
        public string? Id { get; set; }
        public List<string> Queries { get; set; } = [];
        public List<string> Sources { get; set; } = [];
    }
}
