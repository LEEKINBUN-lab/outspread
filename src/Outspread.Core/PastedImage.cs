namespace Outspread.Core;

/// <summary>
/// What a pasted image looks like in raw pixels, before it becomes a file.
/// </summary>
/// <remarks>
/// Bitmaps that come from the clipboard arrive as CF_DIB, and GDI does not fill the
/// alpha byte of a 32-bit DIB: it stays zero. Decoded literally, that is an image
/// whose every pixel is fully transparent, so the board shows an empty rectangle and
/// the paste looks like it worked. Repairing it is only correct in the case where
/// *nothing* is opaque, so this distinguishes the two rather than forcing alpha on.
/// </remarks>
public static class PastedImage
{
    public const int MaxTextCharacters = 200_000;

    /// <summary>
    /// Bytes per row, derived from the length of what the decoder handed over. Anything
    /// that does not divide into whole rows, or comes out narrower than one BGRA row, is
    /// treated as already tight rather than guessed at.
    /// </summary>
    /// <remarks>
    /// The row length is measured from the data instead of read from a property, because
    /// <c>PixelDataProvider</c> in this SDK exposes no stride, and a length cannot
    /// disagree with the buffer it describes.
    /// </remarks>
    public static int StrideFromLength(long length, int bytesPerRow, int rows)
    {
        if (rows <= 0 || bytesPerRow <= 0 || length < ((long)bytesPerRow * rows) || (length % rows) != 0)
        {
            return bytesPerRow;
        }

        var candidate = (int)(length / rows);

        return candidate >= bytesPerRow ? candidate : bytesPerRow;
    }

    /// <summary>
    /// Copies padded scanlines into a tight buffer. A decoder that returns rows aligned
    /// to 4 bytes hands over more bytes per row than the image needs; passing those
    /// straight to an encoder shears every line sideways instead of raising an error.
    /// </summary>
    public static byte[] RepackRows(byte[] padded, int stride, int rowBytes, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, rowBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(rows);

        var tight = new byte[checked(rowBytes * rows)];

        for (var row = 0; row < rows; row++)
        {
            var from = (row * stride) + rowBytes;

            if (from > padded.Length)
            {
                // Short source: the rows already copied stand, the rest stay zero rather
                // than reading past the end.
                break;
            }

            Array.Copy(padded, row * stride, tight, row * rowBytes, rowBytes);
        }

        return tight;
    }

    /// <summary>
    /// Sets every alpha byte to fully opaque when the whole image is transparent,
    /// and reports whether it did so.
    /// </summary>
    /// <param name="bgra">Pixel bytes in BGRA order.</param>
    public static bool RepairBlankAlpha(Span<byte> bgra)
    {
        if (bgra.Length < 4 || (bgra.Length % 4) != 0)
        {
            return false;
        }

        var anyOpaque = false;

        for (var offset = 3; offset < bgra.Length; offset += 4)
        {
            if (bgra[offset] != 0)
            {
                anyOpaque = true;
                break;
            }
        }

        if (anyOpaque)
        {
            return false;
        }

        for (var offset = 3; offset < bgra.Length; offset += 4)
        {
            bgra[offset] = 255;
        }

        return true;
    }

    /// <summary>
    /// Shrinks a pasted body to what one note can hold. The excess is reported rather
    /// than silently cut, because a truncated note that looks complete is worse than a
    /// refusal.
    /// </summary>
    public static (string Text, int DroppedCharacters) TrimPastedText(string? raw)
    {
        var normalized = JsonText.NormalizeTextNewlines(raw ?? string.Empty);

        if (normalized.Length <= MaxTextCharacters)
        {
            return (normalized, 0);
        }

        return (normalized[..MaxTextCharacters], normalized.Length - MaxTextCharacters);
    }
}
