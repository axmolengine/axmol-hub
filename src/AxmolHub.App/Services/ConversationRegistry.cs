using System;
using System.Collections.Generic;
using AxmolHub.Core;

namespace AxmolHub.App;

/// <summary>
/// Holds the one live <see cref="Conversation"/> instance per session id, and is the shell's only way into
/// <see cref="ConversationStore"/>.
///
/// Loading a second copy of a session is what used to lose messages: renaming or pinning a session that was
/// streaming wrote a whole file built from a freshly loaded copy, taking every turn appended since that load
/// with it. Mutating one instance under a per-session gate keeps the transcript the only version of the truth
/// — and it is what lets a session that is not on screen keep writing while the user works somewhere else.
/// </summary>
internal sealed class ConversationRegistry(ConversationStore store)
{
    /// <summary>Guards the two maps only; a session's own gate is what serializes its writes.</summary>
    private readonly object _maintenance = new();

    private readonly Dictionary<string, Conversation> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _gates = new(StringComparer.Ordinal);

    public IReadOnlyList<ConversationSummary> List() => store.List();

    /// <summary>The cached instance, or <c>null</c> when this shell has never opened the session.</summary>
    public Conversation? Peek(string id)
    {
        lock (_maintenance) return _cache.TryGetValue(id, out var conversation) ? conversation : null;
    }

    /// <summary>The single instance for <paramref name="id"/>, reading it off disk on first use. <c>null</c> when unknown.</summary>
    public Conversation? Load(string id)
    {
        lock (_maintenance)
        {
            if (_cache.TryGetValue(id, out var cached)) return cached;
            var conversation = store.Load(id);
            if (conversation is not null) _cache[id] = conversation;
            return conversation;
        }
    }

    /// <summary>Takes ownership of a session the shell just created — new or branched — and writes it once.</summary>
    public void Adopt(Conversation conversation)
    {
        lock (_maintenance) _cache[conversation.Id] = conversation;
        store.Save(conversation);
    }

    /// <summary>Forgets a session. Later writes for it are refused rather than resurrecting the file.</summary>
    public void Drop(string id)
    {
        lock (_maintenance)
        {
            _cache.Remove(id);
            _gates.Remove(id);
        }
    }

    /// <summary>Reads the file behind a session, past the cache. Only a self-check should want this: it is how
    /// a test proves what actually landed on disk rather than what the shell remembers telling itself.</summary>
    public Conversation? LoadFromDisk(string id) => store.Load(id);

    public void Delete(string id)
    {
        store.Delete(id);
        Drop(id);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the single instance and persists it. Returns <c>false</c> when the
    /// session is gone: deleting is an intent, so a late write from a run that had not been told yet must not
    /// bring the file back.
    ///
    /// The delegate is synchronous and must not call back into this class for the same id — the gate is a
    /// monitor, and a nested write would be the caller's bug, not this class's.
    /// </summary>
    public bool TryUpdate(string id, Action<Conversation> change)
    {
        var conversation = Load(id);
        if (conversation is null) return false;
        lock (Gate(id))
        {
            change(conversation);
            store.Save(conversation);
        }
        return true;
    }

    /// <summary>
    /// Copies the first <paramref name="count"/> turns with the session locked, or returns <c>null</c> when the
    /// history is shorter than that. A fork's cut point has to be decided against a list a running session may
    /// be growing, so neither the count nor the enumeration can happen outside the gate.
    /// </summary>
    public List<ChatTurn>? Snapshot(string id, int count)
    {
        var conversation = Load(id);
        if (conversation is null) return null;
        lock (Gate(id))
        {
            return count < 1 || count > conversation.Messages.Count
                ? null
                : [.. conversation.Messages.Take(count)];
        }
    }

    private object Gate(string id)
    {
        lock (_maintenance)
        {
            if (!_gates.TryGetValue(id, out var gate)) _gates[id] = gate = new object();
            return gate;
        }
    }
}
