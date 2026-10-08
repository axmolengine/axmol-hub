namespace AxmolHub;

/// <summary>
/// One picture the window-level viewer can show: enough to caption it, and a way to get its bytes when — and
/// only when — it is the one on screen.
///
/// The loader is a closure rather than stored bytes on purpose. A session with a dozen captures must not
/// decode a dozen bitmaps to look at one, and a draft's bytes live in memory while a sent picture's live in a
/// file beside the session JSON; the closure is the one shape that covers both without either paying for the
/// other.
/// </summary>
internal sealed record PictureRef(string Key, string Caption, long Bytes, Func<byte[]?> Load);
