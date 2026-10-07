namespace AxmolHub.Core;

/// <summary>Statistics of one captured frame.</summary>
public sealed record FrameStats(int Width, int Height, int DistinctColors, double LuminanceVariance)
{
    /// <summary>
    /// Determines whether this frame is "blank".
    ///
    /// It exists because of a trap that **silently weakens evidence**: if any step in the pipeline
    /// is wrong (captured too early, rendering backend not up, window size 0), the output is a
    /// solid-color PNG, and the two assertions "process started and exited 0" + "file exists" still
    /// pass. Installation verification and CI would both get a fake green line from that.
    ///
    /// The criterion leans on **luminance variance**: a solid image has variance 0, and any UI with
    /// content (text, borders, separators) is far above 1. Distinct color count is only a **weak
    /// fallback** (&lt; 2, i.e. the whole image is one color), not the primary criterion — the lower
    /// bound was initially written as 8, and a black-background/white-text image got misjudged as
    /// blank: a minimal UI can perfectly well use only two colors. That misjudgment was caught by
    /// the self-check, not by reasoning.
    /// </summary>
    public bool IsBlank(double minVariance = 1.0)
        => Width <= 0 || Height <= 0 || LuminanceVariance < minVariance || DistinctColors < 2;
}

/// <summary>
/// What a rectangle of BGRA pixels looks like statistically. It lives in Core rather than beside the
/// renderer so the blank criterion itself — not just a real render of it — can be asserted with a
/// synthetic buffer: "will this misjudge a two-colour UI" is a question about arithmetic, and answering
/// it by actually painting something is the slower, flakier way round.
/// </summary>
public static class FrameAnalysis
{
    /// <summary>
    /// Measures a BGRA pixel buffer. A **pure function**, so the blank criterion itself can be
    /// asserted with a synthetic buffer — otherwise "will the criterion misjudge" could only be
    /// tested by actually rendering a white image.
    /// </summary>
    public static FrameStats Analyze(byte[] pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            return new FrameStats(0, 0, 0, 0);
        }

        // Per-pixel measurement is unnecessary and bloats the HashSet on large images; thin it out to roughly 40k samples.
        var step = Math.Max(1, (int)Math.Sqrt((double)width * height / 40000));
        var colors = new HashSet<uint>();
        double sum = 0;
        double sumOfSquares = 0;
        var count = 0;

        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var offset = (y * stride) + (x * 4);
                if (offset + 3 >= pixels.Length)
                {
                    continue;
                }

                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                var alpha = pixels[offset + 3];

                colors.Add(((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue);

                // Rec.601 luma. We only care about "is there variation", so imprecise coefficients are fine.
                var luma = (0.114 * blue) + (0.587 * green) + (0.299 * red);
                sum += luma;
                sumOfSquares += luma * luma;
                count++;
            }
        }

        if (count == 0)
        {
            return new FrameStats(width, height, 0, 0);
        }

        var mean = sum / count;
        var variance = (sumOfSquares / count) - (mean * mean);
        return new FrameStats(width, height, colors.Count, Math.Max(0, variance));
    }
}
