namespace AxmolHub.Core;

/// <summary>Whether one picture may be attached to a message, as decided by <see cref="ChatImageFormat"/>.</summary>
public enum ChatImageVerdict
{
    Accepted,
    Empty,
    Unrecognized,
    TooLarge,
    TooMany,
}

/// <summary>
/// What a store decided about one attachment: the verdict, and for an accepted picture the record the turn has to
/// carry. A refusal writes nothing, so <see cref="Image"/> is null exactly when nothing may be sent.
/// </summary>
public readonly record struct ImageAdmission(ChatImageVerdict Verdict, ChatImage? Image)
{
    public bool Accepted => Verdict == ChatImageVerdict.Accepted;
}

/// <summary>
/// Recognizing, sizing and counting the images a conversation carries.
///
/// Recognition is by <b>file header</b>, never by extension or by what the sender claims: a picture pasted from
/// the clipboard has no file name to read, and the media type that goes to the gateway has to agree with the bytes
/// or the request is rejected for a reason nobody can work out from the transcript. Hub is asked for a screenshot,
/// so "the thing in the clipboard" is the common case rather than an edge one.
///
/// Nothing here re-encodes or resizes. A picture the user took is the evidence, and silently rescaling it to save
/// tokens changes what is being shown — the limit is therefore a refusal with a number in it, not a transformation.
/// </summary>
public static class ChatImageFormat
{
    /// <summary>The largest single picture Hub will put on the wire. Generous on purpose: a 5K screenshot saves
    /// as a PNG well under this, and a limit that rejects real captures would be a limit on the feature.</summary>
    public const long MaxImageBytes = 8L * 1024 * 1024;

    /// <summary>How many pictures one message may carry. Four is what an assistant can actually talk about in one
    /// answer; more than that reads as a pile of attachments rather than a question.</summary>
    public const int MaxImagesPerMessage = 4;

    /// <summary>A recognized format: the media type for the request and the extension the file is stored under.
    /// The two live together because a stored suffix that disagrees with the header cannot be recognized again.</summary>
    public readonly record struct Kind(string MediaType, string Extension);

    public static readonly Kind Png = new("image/png", ".png");
    public static readonly Kind Jpeg = new("image/jpeg", ".jpg");
    public static readonly Kind Gif = new("image/gif", ".gif");
    public static readonly Kind WebP = new("image/webp", ".webp");

    /// <summary>The format these bytes are, or <c>null</c> when they are not a picture Hub knows. Reads only the
    /// signature, so it costs nothing on a multi-megabyte file and needs no seekable stream.</summary>
    public static Kind? Identify(byte[]? bytes)
    {
        if (bytes is null) return null;
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return Jpeg;
        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8'
            && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a') return Gif;
        // RIFF is a container, so the four bytes at offset 8 are what makes it a WebP and not a WAV.
        if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return WebP;
        return null;
    }

    /// <summary>The whole admission decision as one pure function: bytes in, how many the message already carries
    /// in, verdict out. Ordered so the answer names the problem the sender can act on first — a picture that is
    /// both oversized and one too many is oversized, because "attach four smaller ones" is not the fix.</summary>
    public static ChatImageVerdict Admit(byte[] bytes, int alreadyAttached)
    {
        if (bytes is null || bytes.Length == 0) return ChatImageVerdict.Empty;
        if (Identify(bytes) is null) return ChatImageVerdict.Unrecognized;
        if (bytes.LongLength > MaxImageBytes) return ChatImageVerdict.TooLarge;
        if (alreadyAttached >= MaxImagesPerMessage) return ChatImageVerdict.TooMany;
        return ChatImageVerdict.Accepted;
    }

    /// <summary>The sentence for a refusal, in the shape every other guard in this project uses: the fact, the
    /// alternative, and no permission to retry the same input. A refusal the model reads as "try again" costs the
    /// whole turn budget on the same bytes.</summary>
    public static string ResultFor(ChatImageVerdict verdict, long bytes = 0) => verdict switch
    {
        ChatImageVerdict.Empty =>
            "Refused: there are no image bytes to attach. Pick a file or paste the picture again; do not retry an "
            + "empty attachment.",
        ChatImageVerdict.Unrecognized =>
            "Refused: these bytes are not a PNG, JPEG, GIF or WebP image, so there is no media type to send. Attach "
            + "one of those formats; do not retry this file as it is.",
        ChatImageVerdict.TooLarge =>
            $"Refused: the image is {bytes} bytes and Hub sends at most {MaxImageBytes} per picture. Capture a "
            + "smaller region or ask the user for another file; do not retry the same bytes.",
        ChatImageVerdict.TooMany =>
            $"Refused: this message already carries {MaxImagesPerMessage} images, the most Hub sends in one turn. "
            + "Send the rest in the next message; do not retry adding more to this one.",
        _ => "The image was accepted.",
    };
}
