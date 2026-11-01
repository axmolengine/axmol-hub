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
    public string DataRoot => root;
    private static readonly JsonSerializerOptions Json = StateStore.ReadOptions;

    /// <summary>
    /// The index is read-modify-written as a whole file, so two saves that overlap would drop one of the two
    /// rows. Every path that reads or rewrites it takes this gate; it is re-entrant, so the helpers below can
    /// call each other.
    /// </summary>
    private readonly object _indexGate = new();

    private string SessionsDirectory => Path.Combine(root, "ai", "sessions");
    private string IndexPath => Path.Combine(SessionsDirectory, "index.json");
    private string ConversationPath(string id) => Path.Combine(SessionsDirectory, SanitizeId(id) + ".json");

    /// <summary>The on-disk JSON file for a conversation, including before it has been saved.</summary>
    public string SessionFilePath(string id) => ConversationPath(id);

    /// <summary>Where one conversation's attachments live: a directory named after the session, beside its JSON.
    /// Images stay out of the session file because that file is rewritten on every message and replayed into
    /// every later request, and a megabyte of PNG re-encoded as JSON escapes each time is not a transcript, it is
    /// a tarball.</summary>
    private string ImageDirectory(string id) => Path.Combine(SessionsDirectory, SanitizeId(id));

    /// <summary>The directory one session's attachments live in, whether or not it has any yet. Exposed because
    /// "the pictures went away with the session" is a fact about the disk, and a check that reads it has to be
    /// able to name the place.</summary>
    public string SessionImageDirectory(string id) => ImageDirectory(id);

    /// <summary>Stores one image and returns what the turn has to remember: the file name it landed under, the
    /// media type its own header says it is, and how many bytes are on disk. A name rather than a path, for the
    /// same reason the undo pre-images are kept by name — a session file that spells out the data root stops
    /// meaning anything once the root moves. Size and count are not enforced here: those are rules about a
    /// <i>message</i>, and the caller that admits one uses <see cref="ChatImageFormat.Admit"/>.</summary>
    public ChatImage SaveImage(string conversationId, byte[] bytes)
    {
        var kind = ChatImageFormat.Identify(bytes)
                   ?? throw new ArgumentException(
                       "Hub stores an image only if it can recognize the format from its header.", nameof(bytes));
        var directory = ImageDirectory(conversationId);
        Directory.CreateDirectory(directory);
        var name = $"{Directory.EnumerateFiles(directory).Count() + 1}{kind.Extension}";
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
        return new ChatImage(name, kind.MediaType, bytes.LongLength);
    }

    /// <summary>The bytes of a stored image, or <c>null</c> when it is not there. A folder copied by hand can keep
    /// the JSON and lose the pictures, and a missing attachment is a fact the transcript can show — not a reason
    /// to throw while rendering a session that still has everything else.</summary>
    public byte[]? ReadImage(string conversationId, string file)
    {
        var path = ImagePath(conversationId, file);
        return path is null ? null : File.ReadAllBytes(path);
    }

    /// <summary>Where a stored image sits, or <c>null</c> when the name is not a plain file name or nothing is on
    /// disk under it. A view wants the path rather than the bytes: a bitmap given a file name decodes lazily,
    /// while reading the bytes here would put megabytes on the UI thread that renders a transcript row.</summary>
    public string? ImagePath(string conversationId, string file)
    {
        // Only a plain name reaches the disk: the name comes from a session file, which is user-editable data,
        // and "…/../../state.json" is not an image of this conversation.
        var name = Path.GetFileName(file);
        if (name.Length == 0 || !string.Equals(name, file, StringComparison.Ordinal)) return null;
        var path = Path.Combine(ImageDirectory(conversationId), name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Admits and stores one picture in a single step, so a message can never be built out of bytes the request
    /// would refuse to send. Ordering matters: the file is written before the turn is appended, because a turn
    /// that names a missing attachment reads as a lost picture rather than as a refusal that happened first.
    /// A refusal writes nothing.
    /// </summary>
    public ImageAdmission AttachImage(string conversationId, byte[] bytes, int alreadyAttached)
    {
        var verdict = ChatImageFormat.Admit(bytes, alreadyAttached);
        if (verdict != ChatImageVerdict.Accepted) return new ImageAdmission(verdict, null);
        return new ImageAdmission(ChatImageVerdict.Accepted, SaveImage(conversationId, bytes));
    }

    /// <summary>
    /// Reads one picture a person is offering, from a file name, and says whether it could ever be sent — without
    /// storing anything. The composer holds a picture before it belongs to any conversation, and a draft that is
    /// never sent must not leave a file on disk; how many pictures one message may carry is a rule about the
    /// message, so the caller counts its own queue and asks <see cref="ChatImageFormat.Admit"/> about that.
    /// The size is checked <b>before</b> the file is read, so picking something oversized costs a stat rather than
    /// a gigabyte of memory.
    /// </summary>
    public static byte[]? ReadCandidateFile(string path, out ChatImageVerdict verdict)
    {
        var length = new FileInfo(path).Length;
        verdict = length switch
        {
            0 => ChatImageVerdict.Empty,
            > ChatImageFormat.MaxImageBytes => ChatImageVerdict.TooLarge,
            _ => ChatImageVerdict.Accepted,
        };
        if (verdict != ChatImageVerdict.Accepted) return null;

        var bytes = File.ReadAllBytes(path);
        if (ChatImageFormat.Identify(bytes) is null)
        {
            verdict = ChatImageVerdict.Unrecognized;
            return null;
        }

        verdict = ChatImageVerdict.Accepted;
        return bytes;
    }

    /// <summary>Session headers, pinned first then most recently engaged first. Rebuilds the index when it is
    /// absent or unreadable.</summary>
    public List<ConversationSummary> List()
    {
        lock (_indexGate)
        {
            if (!File.Exists(IndexPath)) return RebuildIndex();
            try
            {
                var rows = JsonSerializer.Deserialize<SessionIndexFile>(File.ReadAllText(IndexPath), Json)?.Sessions;
                return rows is null ? RebuildIndex() : [.. Order(rows)];
            }
            // An index this build cannot read is rebuilt rather than trusted, and that is also the whole
            // migration story for the workspace and archive columns: the file written before them is a bare
            // array, which does not bind to <see cref="SessionIndexFile"/> at all. Reading it as "no workspace
            // anywhere" would fold every project into the plain-chat bucket forever, while the session files
            // beside it already know where each session works.
            catch (JsonException) { return RebuildIndex(); }
        }
    }

    /// <summary>
    /// The guards this store can build by itself: its own data root, and no engine list — engines are an app fact
    /// that lives above it. That is enough to keep the one directory that must never be written through out of a
    /// session file's writable half, where it is demoted to the read-only half instead; an engine root is demoted
    /// where that list exists, and every path is graded against the full guards again when a tool uses it.
    /// </summary>
    private WorkspaceGuards OwnGuards => new(root, []);

    /// <summary>
    /// Both grant lists through their shape rules, at the one door both the load and the save go through — see
    /// <see cref="WorkspacePaths.SplitRoots"/>. A protected location found in the writable half is moved to the
    /// read-only half rather than dropped: the card that put it there opened the reading, and a demotion keeps
    /// exactly that. The granted files go through <see cref="WorkspacePaths.NormalizeFiles"/>, which takes no
    /// guards on purpose: a file's permission is asked at every resolution, so there is no half to move it to.
    /// </summary>
    private void NormalizeGrants(Conversation conversation)
    {
        conversation.GoalProgressFingerprints = (conversation.GoalProgressFingerprints ?? [])
            .TakeLast(GoalProgress.WindowSize).ToList();
        conversation.GoalWaitingFor ??= [];
        conversation.GoalReportedTokens = Math.Max(0, conversation.GoalReportedTokens);
        conversation.GoalEstimatedTokens = Math.Max(0, conversation.GoalEstimatedTokens);
        if (conversation.GoalState == ChatGoalStates.Paused && conversation.GoalStopReason == "blocked")
            conversation.GoalState = ChatGoalStates.Blocked;
        var (write, read) = WorkspacePaths.SplitRoots(conversation.GrantedRoots, conversation.ReadOnlyRoots,
            OwnGuards);
        conversation.GrantedRoots = write;
        conversation.ReadOnlyRoots = read;
        conversation.GrantedFiles = WorkspacePaths.NormalizeFiles(conversation.GrantedFiles);
    }

    /// <summary>Loads one conversation; <c>null</c> when the id is unknown.
    ///
    /// <para>The granted directories go through their shape rules on the way out — see
    /// <see cref="NormalizeGrants"/>. A session file is user-editable data: one that grew past the cap, named a
    /// directory twice in two spellings, kept a folder that has since been deleted, or wrote a protected location
    /// into the writable half must arrive at the guard as a list it can trust, or every later reader would have to
    /// re-check it and one of them would eventually forget.</para></summary>
    public Conversation? Load(string id)
    {
        var path = ConversationPath(id);
        if (!File.Exists(path)) return null;
        var conversation = Read(path);
        if (conversation is not null)
        {
            NormalizeGrants(conversation);
            if (conversation.MigrateRetiredFsTools() > 0) Save(conversation);
        }
        return conversation;
    }

    /// <summary>
    /// One session file, parsed straight from the file's own bytes.
    ///
    /// <para>Deliberately not <c>File.ReadAllText</c>: that hands the parser a UTF-16 string which it has to
    /// encode back to UTF-8 before it can read a token, so a long session is copied through memory twice for
    /// nothing — the file is UTF-8 and the parser reads that form natively. The contract is otherwise identical
    /// (same options, same result), which is why the difference is only visible in a profile.</para>
    /// </summary>
    private static Conversation? Read(string path)
        => JsonSerializer.Deserialize<Conversation>(File.ReadAllBytes(path), Json);

    /// <summary>
    /// The session file's own text, before anything is deserialized. For the self-check only, and it exists
    /// because "the plan body left the session file" is a claim about these bytes: read back through
    /// <see cref="Load"/> it would be answered by the hydrated object, which holds the body again the moment the
    /// reference is resolved.
    /// </summary>
    public string? ReadStoredText(string id)
    {
        var path = ConversationPath(id);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Writes a conversation and refreshes its index entry, atomically. The granted directories are put
    /// through their shape rules first: this is the one door every writer goes through, so a list that grew past
    /// the cap or kept a spelling that no longer resolves cannot reach the disk from any caller.</summary>
    public void Save(Conversation conversation)
    {
        NormalizeGrants(conversation);
        StateStore.WriteJson(ConversationPath(conversation.Id), conversation);
        lock (_indexGate)
        {
            var index = List();
            index.RemoveAll(summary => summary.Id == conversation.Id);
            index.Add(ConversationSummary.From(conversation));
            WriteIndex(index);
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
            WriteIndex(index);
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
                        var conversation = Read(file);
                        if (conversation is not null) summaries.Add(ConversationSummary.From(conversation));
                    }
                    catch (JsonException) { /* A corrupt session file is skipped, not fatal — the rest still load. */ }
                }
            }

            summaries = [.. Order(summaries)];
            StateStore.WriteJson(IndexPath, new SessionIndexFile { Sessions = summaries });
            return summaries;
        }
    }

    /// <summary>Writes the index in the one ordering every reader expects. Ordering the write rather than
    /// leaving it to the caller is the point: a delete that reshuffled the list would reorder rows the user
    /// never touched.</summary>
    private void WriteIndex(IEnumerable<ConversationSummary> rows)
        => StateStore.WriteJson(IndexPath, new SessionIndexFile { Sessions = [.. Order(rows)] });

    /// <summary>The single ordering rule for the session list: pinned first, then most recently engaged.
    /// Engagement is a person acting or a run ending — not mid-run transcript writes — so several parallel
    /// conversations answering at once do not reshuffle the list with every tool round. Rows from before the
    /// key existed fall back to their last update time.</summary>
    private static IEnumerable<ConversationSummary> Order(IEnumerable<ConversationSummary> summaries)
        => summaries.OrderByDescending(summary => summary.Pinned)
            .ThenByDescending(summary => summary.EngagedAt ?? summary.UpdatedAt);

    /// <summary>
    /// What <c>index.json</c> holds: the headers, wrapped in an object that can carry a shape marker. The envelope
    /// is not ceremony — a bare array has nowhere to put a schema, and this is the second time the row shape has
    /// changed (branch source, then workspace and archive). With the array written directly, a reader cannot tell
    /// "rows from before the column existed" from "rows that say the column is empty", and the first of those
    /// would silently file every project's sessions under plain chats. Under the envelope the old file simply does
    /// not bind, and <see cref="List"/>'s existing repair — rebuild from the session files, which do know — runs.
    /// </summary>
    private sealed class SessionIndexFile
    {
        public int Schema { get; set; } = 1;
        public List<ConversationSummary> Sessions { get; set; } = [];
    }

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
