using System.Text.Json;

namespace AxmolHub.Core;

/// <summary>
/// Persists conversations under <c>data-root/ai/sessions/</c>: one <c>{id}.json</c> per conversation plus an
/// <c>index.json</c> of headers.
///
/// The index exists so the session picker never has to read every message body — listing is the common
/// operation and message files grow without bound. The index is a derived cache of the headers: if it is
/// missing or stale, <see cref="List"/> rebuilds it from the individual files, so it can never be the only
/// copy of anything.
/// </summary>
public sealed class ConversationStore(string root)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The index is read-modify-written as a whole file, so two saves that overlap would drop one of the two
    /// rows. Every path that reads or rewrites it takes this gate; it is re-entrant, so the helpers below can
    /// call each other.
    /// </summary>
    private readonly object _indexGate = new();

    private string SessionsDirectory => Path.Combine(root, "ai", "sessions");
    private string IndexPath => Path.Combine(SessionsDirectory, "index.json");
    private string ConversationPath(string id) => Path.Combine(SessionsDirectory, SanitizeId(id) + ".json");

    /// <summary>Where one conversation's attachments live: a directory named after the session, beside its JSON.
    /// Images stay out of the session file because that file is rewritten on every message and replayed into
    /// every later request, and a megabyte of PNG re-encoded as JSON escapes each time is not a transcript, it is
    /// a tarball.</summary>
    private string ImageDirectory(string id) => Path.Combine(SessionsDirectory, SanitizeId(id));

    /// <summary>Stores one image and returns what the turn has to remember: the file name it landed under, the
    /// media type it was given as, and how many bytes are on disk. A name rather than a path, for the same reason
    /// the undo pre-images are kept by name — a session file that spells out the data root stops meaning anything
    /// once the root moves.</summary>
    public ChatImage SaveImage(string conversationId, byte[] bytes, string mediaType)
    {
        var directory = ImageDirectory(conversationId);
        Directory.CreateDirectory(directory);
        var name = $"{Directory.EnumerateFiles(directory).Count() + 1}{ExtensionFor(mediaType)}";
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
        return new ChatImage(name, mediaType, bytes.LongLength);
    }

    /// <summary>The bytes of a stored image, or <c>null</c> when it is not there. A folder copied by hand can keep
    /// the JSON and lose the pictures, and a missing attachment is a fact the transcript can show — not a reason
    /// to throw while rendering a session that still has everything else.</summary>
    public byte[]? ReadImage(string conversationId, string file)
    {
        // Only a plain name reaches the disk: the name comes from a session file, which is user-editable data,
        // and "…/../../state.json" is not an image of this conversation.
        var name = Path.GetFileName(file);
        if (name.Length == 0 || !string.Equals(name, file, StringComparison.Ordinal)) return null;
        var path = Path.Combine(ImageDirectory(conversationId), name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Session headers, pinned first then newest first. Rebuilds the index when it is absent or unreadable.</summary>
    public List<ConversationSummary> List()
    {
        lock (_indexGate)
        {
            if (!File.Exists(IndexPath)) return RebuildIndex();
            try
            {
                var index = JsonSerializer.Deserialize<List<ConversationSummary>>(File.ReadAllText(IndexPath), Json);
                return index is null ? RebuildIndex() : [.. Order(index)];
            }
            catch (JsonException) { return RebuildIndex(); }
        }
    }

    /// <summary>Loads one conversation; <c>null</c> when the id is unknown.</summary>
    public Conversation? Load(string id)
    {
        var path = ConversationPath(id);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path), Json)
            : null;
    }

    /// <summary>Writes a conversation and refreshes its index entry, atomically.</summary>
    public void Save(Conversation conversation)
    {
        StateStore.WriteJson(ConversationPath(conversation.Id), conversation);
        lock (_indexGate)
        {
            var index = List();
            index.RemoveAll(summary => summary.Id == conversation.Id);
            index.Add(ConversationSummary.From(conversation));
            StateStore.WriteJson(IndexPath, Order(index).ToList());
        }
    }

    /// <summary>Deletes a conversation and its index entry. Deleting an unknown id is not an error.</summary>
    public void Delete(string id)
    {
        var path = ConversationPath(id);
        if (File.Exists(path)) File.Delete(path);
        // The attachments live in a directory named after the session, and once the JSON is gone nothing can
        // reach them again: a deleted conversation would otherwise leave its screenshots on the disk forever.
        var images = ImageDirectory(id);
        if (Directory.Exists(images)) Directory.Delete(images, recursive: true);
        lock (_indexGate)
        {
            var index = List();
            index.RemoveAll(summary => summary.Id == id);
            // Ordered like every other write: an unordered rewrite would reshuffle the list on a delete,
            // which is the one operation the user never asked to reorder.
            StateStore.WriteJson(IndexPath, Order(index).ToList());
        }
    }

    private List<ConversationSummary> RebuildIndex()
    {
        lock (_indexGate)
        {
            var summaries = new List<ConversationSummary>();
            if (Directory.Exists(SessionsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(SessionsDirectory, "*.json"))
                {
                    if (Path.GetFileName(file).Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var conversation = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file), Json);
                        if (conversation is not null) summaries.Add(ConversationSummary.From(conversation));
                    }
                    catch (JsonException) { /* A corrupt session file is skipped, not fatal — the rest still load. */ }
                }
            }

            summaries = [.. Order(summaries)];
            StateStore.WriteJson(IndexPath, summaries);
            return summaries;
        }
    }

    /// <summary>The single ordering rule for the session list: pinned first, then most recently updated.</summary>
    private static IEnumerable<ConversationSummary> Order(IEnumerable<ConversationSummary> summaries)
        => summaries.OrderByDescending(summary => summary.Pinned).ThenByDescending(summary => summary.UpdatedAt);

    /// <summary>The file name extension a media type is stored under. The four are what a model can be sent as an
    /// image today; anything else is refused here rather than written with a made-up suffix, because a file whose
    /// extension disagrees with its bytes cannot be recognized again by the reader that sniffs the header.</summary>
    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => throw new ArgumentException($"Hub has no file name for the media type 「{mediaType}」.", nameof(mediaType)),
    };

    /// <summary>
    /// Ids are GUIDs generated by <see cref="Conversation.Create"/>, but they arrive from filenames and UI
    /// callers alike — anything outside <c>[A-Za-z0-9_-]</c> is dropped so a hostile id cannot escape the
    /// sessions directory (path traversal).
    /// </summary>
    private static string SanitizeId(string id)
    {
        var clean = new string([.. id.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')]);
        if (clean.Length == 0) throw new ArgumentException("Conversation id is empty.", nameof(id));
        return clean;
    }
}
