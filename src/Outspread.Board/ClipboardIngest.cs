using System.IO;
using Outspread.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Outspread.Board;

public enum IngestKind
{
    Nothing,
    Text,
    Image,
    Files,
}

/// <summary>
/// What came off the clipboard or out of a drop, already in the form the board stores.
/// </summary>
/// <param name="Description">What the board says it pasted — including what it says when
/// nothing usable was found, so a no-op paste is never silent.</param>
/// <param name="ImageBytes">Decoded and re-encoded PNG for a clipboard bitmap, or the file's
/// own bytes for a dropped image.</param>
public sealed record IngestResult(
    IngestKind Kind,
    string Description,
    string? Text = null,
    byte[]? ImageBytes = null,
    int Width = 0,
    int Height = 0,
    IReadOnlyList<string>? Files = null,
    string? Failure = null)
{
    public bool Failed => Failure is not null;
}

/// <summary>
/// Reading the clipboard and drops.
/// </summary>
/// <remarks>
/// Order is deliberate: files, then an image, then text. A screenshot tool hands over
/// CF_DIB whose alpha byte GDI never fills in, which is why the pixels pass through
/// <see cref="PastedImage.RepairBlankAlpha"/> before anything is written — a paste that
/// produces an invisible note is the failure this exists to avoid.
///
/// RTF and HTML are read never: storing them would put formatting this board cannot
/// render into a file an agent has to edit.
/// </remarks>
internal static class ClipboardIngest
{
    private const int BusyAttempts = 3;

    private const int BusyDelayMilliseconds = 40;

    public static async Task<IngestResult> FromClipboardAsync()
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= BusyAttempts; attempt++)
        {
            try
            {
                return await ReadAsync(Clipboard.GetContent()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                last = ex;
                await Task.Delay(BusyDelayMilliseconds * attempt).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x800401D0))
            {
                // CLIPBRD_E_CANT_OPEN: another program still holds the clipboard open.
                last = ex;
                await Task.Delay(BusyDelayMilliseconds * attempt).ConfigureAwait(false);
            }
        }

        return new IngestResult(IngestKind.Nothing, "剪贴板被别的程序占着，没读到", Failure: last?.Message);
    }

    public static Task<IngestResult> FromDataViewAsync(DataPackageView view) => ReadAsync(view);

    private static async Task<IngestResult> ReadAsync(DataPackageView? view)
    {
        if (view is null)
        {
            return new IngestResult(IngestKind.Nothing, "剪贴板是空的");
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var paths = (await view.GetStorageItemsAsync())
                .Where(item => item is IStorageFile)
                .Select(item => item.Path)
                .ToList();

            if (paths.Count > 0)
            {
                var images = paths.Where(IsImage).ToList();

                if (images.Count > 0)
                {
                    return new IngestResult(IngestKind.Files,
                        $"{images.Count} 个图片文件", Files: images);
                }

                // Not an image: a file the board cannot show still belongs on it as a link.
                return new IngestResult(IngestKind.Files,
                    $"{paths.Count} 个文件（按链接放置）", Files: paths);
            }
        }

        if (view.Contains(StandardDataFormats.Bitmap))
        {
            return await ReadBitmapAsync(view);
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            var (text, dropped) = PastedImage.TrimPastedText(await view.GetTextAsync());

            return new IngestResult(
                IngestKind.Text,
                dropped > 0
                    ? $"文字 {text.Length} 字，超出单格上限 {PastedImage.MaxTextCharacters}，截掉了 {dropped} 字"
                    : $"文字 {text.Length} 字",
                Text: text);
        }

        var available = AvailableFormats(view);

        return new IngestResult(IngestKind.Nothing,
            available.Length == 0 ? "剪贴板是空的" : $"剪贴板上的东西这板子放不下：{available}");
    }

    private static async Task<IngestResult> ReadBitmapAsync(DataPackageView view)
    {
        var reference = await view.GetBitmapAsync();

        using var stream = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;

        if (width <= 0 || height <= 0)
        {
            return new IngestResult(IngestKind.Nothing, "剪贴板上的图读不出尺寸", Failure: "zero-sized bitmap");
        }

        // Asking the decoder for BGRA straight away, rather than decoding to a
        // SoftwareBitmap and locking it: this projection has no lock on SoftwareBitmap, and
        // the decoder path is also the one that honours EXIF rotation.
        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        var raw = provider.DetachPixelData();

        // The encoder wants rows packed tight. The decoder may return padded rows, and a
        // padded buffer handed over as if it were tight shifts every line by a few pixels,
        // which looks like a sheared image rather than an error. The row length is derived
        // from the byte count rather than a property: PixelDataProvider does not carry one
        // in this projection, and the length cannot disagree with the data.
        var tight = width * 4;
        var stride = PastedImage.StrideFromLength(raw.LongLength, tight, height);

        var pixels = stride == tight ? raw : PastedImage.RepackRows(raw, stride, tight, height);

        var repaired = PastedImage.RepairBlankAlpha(pixels);

        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);

        await encoder.FlushAsync();

        var png = new byte[encoded.Size];

        using (var reader = new DataReader(encoded.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)encoded.Size);
            reader.ReadBytes(png);
        }

        return new IngestResult(
            IngestKind.Image,
            repaired
                ? $"剪贴板图片 {width}×{height}（原图 alpha 全为 0，已按不透明补回，否则会是一张看不见的图）"
                : $"剪贴板图片 {width}×{height}",
            ImageBytes: png,
            Width: width,
            Height: height);
    }

    /// <summary>Takes a dropped or copied image file as it is, without re-encoding.</summary>
    public static async Task<IngestResult> FromImageFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);

        var (width, height) = await MeasureAsync(bytes);

        return new IngestResult(
            IngestKind.Image,
            $"文件 {Path.GetFileName(path)}（{(width > 0 ? $"{width}×{height}" : "尺寸读不出，按默认格放")}）",
            ImageBytes: bytes,
            Width: width,
            Height: height);
    }

    /// <summary>
    /// Asks the decoder what size the file is, keeping the bytes untouched. A photo stays
    /// a photo: re-encoding to PNG to learn its dimensions would cost about five times
    /// the space on disk for every dropped image.
    /// </summary>
    private static async Task<(int Width, int Height)> MeasureAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream.GetOutputStreamAt(0));

            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);

            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                   or InvalidOperationException)
        {
            return (0, 0);
        }
    }

    public static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".tiff";
    }

    private static string AvailableFormats(DataPackageView view)
    {
        var names = new List<string>();

        if (view.Contains(StandardDataFormats.Text))
        {
            names.Add("text");
        }

        if (view.Contains(StandardDataFormats.Bitmap))
        {
            names.Add("bitmap");
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            names.Add("files");
        }

        if (view.Contains(StandardDataFormats.Html))
        {
            names.Add("html");
        }

        if (view.Contains(StandardDataFormats.Rtf))
        {
            names.Add("rtf");
        }

        return string.Join(", ", names);
    }
}
