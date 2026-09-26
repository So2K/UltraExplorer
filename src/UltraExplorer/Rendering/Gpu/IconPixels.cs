namespace UltraExplorer.Rendering.Gpu;

/// <summary>
/// The pixel work between a Shell icon and an atlas slot, kept apart from the
/// Shell calls so it can be checked on made-up pictures: telling straight
/// alpha from premultiplied, spotting the jumbo image that is really a small
/// one in a corner, scaling to 64 pixels and building the levels under it.
///
/// Everything here works on BGRA rows packed without padding, and everything
/// after <see cref="Normalise"/> works in premultiplied space.  That is what
/// keeps the edges of a scaled icon from going dark: averaging straight colour
/// lets the black of fully transparent pixels bleed into the ones beside them,
/// averaging premultiplied colour does not.  Every filter here is a weighted
/// average with weights that add up to one, and colour is clamped to alpha at
/// the end, so premultiplied stays premultiplied - no channel ever exceeds
/// its pixel's alpha - which the blend on the GPU (one, inverse source alpha)
/// relies on.
/// </summary>
internal static class IconPixels
{
    /// <summary>
    /// The side of the small image a jumbo list returns for a type that has no
    /// 256-pixel frame, in a process that is not DPI-aware.  In a DPI-aware
    /// one it is the extra-large list's side instead: 72 pixels at 150%.
    /// </summary>
    public const int SmallFrameInJumbo = 48;

    /// <summary>
    /// Whether <paramref name="bgra"/>, a jumbo image, is the known case of a
    /// type with no 256-pixel frame: the image list then hands back a 256
    /// canvas with the extra-large icon (<paramref name="frame"/> pixels) in
    /// its top-left corner and nothing else.  There is no call that says so;
    /// the only sign is that every pixel outside that corner is fully
    /// transparent.  Checked on the raw pixels, before anything decides what
    /// an alpha of zero means.
    /// </summary>
    public static bool HoldsOnlyTopLeftFrame(ReadOnlySpan<byte> bgra, int width, int height, int frame = SmallFrameInJumbo)
    {
        if (width <= frame || height <= frame || bgra.Length < width * height * 4)
        {
            return false;
        }

        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * width * 4, width * 4);
            for (var x = y < frame ? frame : 0; x < width; x++)
            {
                if (row[x * 4 + 3] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Makes an icon's pixels premultiplied, whatever they were.  Icons come
    /// in three kinds and nothing says which:
    /// <list type="bullet">
    /// <item>Straight alpha, the usual kind: some colour channel is brighter
    /// than its alpha somewhere - an antialiased edge is enough - and then
    /// every pixel is multiplied by its alpha.</item>
    /// <item>Already premultiplied, which some icon handlers return: no channel
    /// exceeds its alpha anywhere, and the pixels are left as they are.</item>
    /// <item>No alpha at all, an old icon drawn with a mask: every alpha is
    /// zero.  Then <paramref name="mask"/>, the icon's AND mask read as 32-bit
    /// pixels, says which pixels are transparent (white) and which are opaque
    /// (black); with no mask every pixel is taken as opaque.</item>
    /// </list>
    /// </summary>
    public static void Normalise(Span<byte> bgra, ReadOnlySpan<byte> mask)
    {
        var anyAlpha = false;
        var straight = false;
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            var alpha = bgra[index + 3];
            anyAlpha |= alpha != 0;
            straight |= bgra[index] > alpha || bgra[index + 1] > alpha || bgra[index + 2] > alpha;
        }

        if (!anyAlpha)
        {
            var useMask = mask.Length >= bgra.Length;
            for (var index = 0; index + 3 < bgra.Length; index += 4)
            {
                var opaque = !useMask || mask[index] == 0 && mask[index + 1] == 0 && mask[index + 2] == 0;
                if (opaque)
                {
                    bgra[index + 3] = 255;
                }
                else
                {
                    bgra[index] = 0;
                    bgra[index + 1] = 0;
                    bgra[index + 2] = 0;
                }
            }

            return;
        }

        if (!straight)
        {
            return;
        }

        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            var alpha = bgra[index + 3];
            bgra[index] = (byte)((bgra[index] * alpha + 127) / 255);
            bgra[index + 1] = (byte)((bgra[index + 1] * alpha + 127) / 255);
            bgra[index + 2] = (byte)((bgra[index + 2] * alpha + 127) / 255);
        }
    }

    /// <summary>Whether no colour channel anywhere exceeds its pixel's alpha.</summary>
    public static bool IsPremultiplied(ReadOnlySpan<byte> bgra)
    {
        for (var index = 0; index + 3 < bgra.Length; index += 4)
        {
            var alpha = bgra[index + 3];
            if (bgra[index] > alpha || bgra[index + 1] > alpha || bgra[index + 2] > alpha)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The top-left <paramref name="frame"/> x <paramref name="frame"/> pixels
    /// of an image <paramref name="width"/> pixels wide: the small icon out of
    /// a jumbo canvas, when the extra-large list cannot be had.
    /// </summary>
    public static byte[] Crop(ReadOnlySpan<byte> bgra, int width, int frame)
    {
        var cropped = new byte[frame * frame * 4];
        for (var y = 0; y < frame; y++)
        {
            bgra.Slice(y * width * 4, frame * 4).CopyTo(cropped.AsSpan(y * frame * 4));
        }

        return cropped;
    }

    /// <summary>
    /// Builds a slot from a normalised image of any size and, when there are
    /// any, the hand-drawn 32 and 16 pixel frames.  Level 0 is the image
    /// scaled to 64: a plain 4 x 4 average from the 256-pixel jumbo frame,
    /// area averaging from other larger sizes, bilinear from smaller ones.
    /// Levels 1 and 2 are the hand-drawn frames when they are exactly 32 and
    /// 16 pixels - those are hinted to the pixel grid and look sharper than
    /// any average of the big one - and otherwise halve the level above.  The
    /// rest always halve the level above with a 2 x 2 average.
    /// </summary>
    public static IconSlotData BuildSlot(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        ReadOnlySpan<byte> frame32,
        ReadOnlySpan<byte> frame16)
    {
        var pixels = IconSlotData.NewPixelBuffer();
        Resample(image, width, height, pixels.AsSpan(0, IconSlotData.MipByteCount(0)), IconSlotData.Size, IconSlotData.Size);
        for (var mip = 1; mip < IconSlotData.MipLevels; mip++)
        {
            var target = pixels.AsSpan(IconSlotData.MipOffset(mip), IconSlotData.MipByteCount(mip));
            if (mip == 1 && frame32.Length == target.Length)
            {
                frame32.CopyTo(target);
            }
            else if (mip == 2 && frame16.Length == target.Length)
            {
                frame16.CopyTo(target);
            }
            else
            {
                var above = pixels.AsSpan(IconSlotData.MipOffset(mip - 1), IconSlotData.MipByteCount(mip - 1));
                Halve(above, IconSlotData.MipSize(mip - 1), target);
            }
        }

        return IconSlotData.Adopt(pixels);
    }

    /// <summary>
    /// Halves a square premultiplied level: every pixel of the result is the
    /// rounded average of a 2 x 2 block of the source.
    /// </summary>
    public static void Halve(ReadOnlySpan<byte> source, int sourceSize, Span<byte> destination)
    {
        var size = sourceSize / 2;
        var sourceRow = sourceSize * 4;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var top = (2 * y) * sourceRow + 2 * x * 4;
                var bottom = top + sourceRow;
                var target = (y * size + x) * 4;
                var alpha = (source[top + 3] + source[top + 7] + source[bottom + 3] + source[bottom + 7] + 2) >> 2;
                destination[target + 3] = (byte)alpha;
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = source[top + channel] + source[top + 4 + channel]
                        + source[bottom + channel] + source[bottom + 4 + channel];
                    destination[target + channel] = (byte)Math.Min((sum + 2) >> 2, alpha);
                }
            }
        }
    }

    /// <summary>
    /// Scales a premultiplied image to <paramref name="width"/> x
    /// <paramref name="height"/>.  The same size is a copy; a whole-number
    /// reduction (256 to 64) is a plain box average; anything else goes
    /// through a separable filter that averages areas when shrinking and
    /// interpolates linearly when growing.
    /// </summary>
    public static void Resample(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, Span<byte> destination, int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height)
        {
            source[..(width * height * 4)].CopyTo(destination);
            return;
        }

        if (sourceWidth > width && sourceHeight > height && sourceWidth % width == 0 && sourceHeight % height == 0)
        {
            BoxReduce(source, sourceWidth, sourceWidth / width, sourceHeight / height, destination, width, height);
            return;
        }

        var across = Weights(sourceWidth, width);
        var down = Weights(sourceHeight, height);

        // Across first, into floats, one row of the source at a time.
        var middle = new float[sourceHeight * width * 4];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var target = (y * width + x) * 4;
                foreach (var (index, weight) in across[x])
                {
                    var from = (y * sourceWidth + index) * 4;
                    middle[target] += weight * source[from];
                    middle[target + 1] += weight * source[from + 1];
                    middle[target + 2] += weight * source[from + 2];
                    middle[target + 3] += weight * source[from + 3];
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float blue = 0, green = 0, red = 0, alpha = 0;
                foreach (var (index, weight) in down[y])
                {
                    var from = (index * width + x) * 4;
                    blue += weight * middle[from];
                    green += weight * middle[from + 1];
                    red += weight * middle[from + 2];
                    alpha += weight * middle[from + 3];
                }

                var target = (y * width + x) * 4;
                var a = ToByte(alpha);
                destination[target] = Math.Min(ToByte(blue), a);
                destination[target + 1] = Math.Min(ToByte(green), a);
                destination[target + 2] = Math.Min(ToByte(red), a);
                destination[target + 3] = a;
            }
        }
    }

    private static void BoxReduce(ReadOnlySpan<byte> source, int sourceWidth, int factorX, int factorY, Span<byte> destination, int width, int height)
    {
        var count = factorX * factorY;
        var half = count / 2;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int blue = 0, green = 0, red = 0, alpha = 0;
                for (var row = 0; row < factorY; row++)
                {
                    var from = ((y * factorY + row) * sourceWidth + x * factorX) * 4;
                    for (var column = 0; column < factorX; column++, from += 4)
                    {
                        blue += source[from];
                        green += source[from + 1];
                        red += source[from + 2];
                        alpha += source[from + 3];
                    }
                }

                var target = (y * width + x) * 4;
                var a = (byte)((alpha + half) / count);
                destination[target] = (byte)Math.Min((blue + half) / count, a);
                destination[target + 1] = (byte)Math.Min((green + half) / count, a);
                destination[target + 2] = (byte)Math.Min((red + half) / count, a);
                destination[target + 3] = a;
            }
        }
    }

    /// <summary>
    /// For each of <paramref name="length"/> output pixels along one axis, the
    /// source pixels it takes from and how much of each.  Shrinking, an output
    /// pixel covers a stretch of the source and takes each source pixel in
    /// proportion to how much of it lies inside; growing, it takes the two
    /// source pixels either side of its centre.
    /// </summary>
    private static (int Index, float Weight)[][] Weights(int sourceLength, int length)
    {
        var weights = new (int Index, float Weight)[length][];
        var scale = (double)sourceLength / length;
        for (var target = 0; target < length; target++)
        {
            if (sourceLength > length)
            {
                var start = target * scale;
                var end = start + scale;
                var list = new List<(int, float)>();
                for (var index = (int)Math.Floor(start); index < Math.Ceiling(end) && index < sourceLength; index++)
                {
                    var covered = Math.Min(end, index + 1) - Math.Max(start, index);
                    if (covered > 0)
                    {
                        list.Add((index, (float)(covered / scale)));
                    }
                }

                weights[target] = [.. list];
            }
            else
            {
                var centre = (target + 0.5) * scale - 0.5;
                var left = (int)Math.Floor(centre);
                var fraction = (float)(centre - left);
                var first = Math.Clamp(left, 0, sourceLength - 1);
                var second = Math.Clamp(left + 1, 0, sourceLength - 1);
                weights[target] = [(first, 1 - fraction), (second, fraction)];
            }
        }

        return weights;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
