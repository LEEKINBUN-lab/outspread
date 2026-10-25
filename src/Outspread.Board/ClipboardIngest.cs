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

        return new IngestResult(IngestKind.Nothing, "剪贴板被其他程序占用，读取失败", Failure: last?.Message);
    }

    public static Task<IngestResult> FromDataViewAsync(DataPackageView view) => ReadAsync(view);

    /// <summary>
    /// 片段的落盘件：与账本同一个应用目录，不进仓、不进板子目录。
    /// <para>
    /// 为什么不走剪贴板的自定义格式：这一版 WinUI 投影里 <c>DataPackage</c> 只给了写
    /// （<c>SetData</c>），<c>DataPackageView</c> 没给读（<c>GetData</c> 与
    /// <c>IDataPackageView</c> 都不在可访问面上）——写进去的自定义那式自己再也认不回来。
    /// 所以剪贴板上放的是<b>几行谁都能读的字</b>（别的程序要什么就有那什么），带结构的这份
    /// 落在这里，贴的时候用剪贴板上那几行字对账：对不上就是人又复制了别的东西，这份不算数。
    /// </para>
    /// </summary>
    public static string FragmentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Outspread",
        "clipboard.canvas");

    /// <summary>
    /// 放一份复制：几行能读的字上剪贴板，带结构的片段落 <see cref="FragmentPath"/>。
    /// </summary>
    /// <returns>null＝放上去了；否则是那句"为什么没放上去"。剪贴板是别人也在抢的公共资源，
    /// 抢不到不是 bug，但必须说出来——一声不响的 Ctrl+C 会让人再按五遍。</returns>
    public static async Task<string?> PutFragmentAsync(string fragment, string plainText)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FragmentPath)!);
            File.WriteAllText(FragmentPath, fragment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"片段没处落：{ex.Message}";
        }

        Exception? last = null;

        for (var attempt = 1; attempt <= BusyAttempts; attempt++)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

                // 那几行字由 `BoardFragment.From` 一次算好：剪贴板上的与片段里存的是同一句，
                // 对账才对得上。
                package.SetText(plainText);

                Clipboard.SetContent(package);

                // 不留神这一发只在我们进程里活着：Flush 是"我要把这份交给剪贴板管理器了"。
                Clipboard.Flush();

                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                    || ex.HResult == unchecked((int)0x800401D0))
            {
                last = ex;
                await Task.Delay(BusyDelayMilliseconds * attempt);
            }
        }

        return last is null ? "剪贴板没收下这一发" : $"剪贴板被别的程序占着：{last.Message}";
    }

    /// <summary>
    /// 落盘件里那份片段还在不在、还是不是刚复制的那一发。
    /// </summary>
    /// <returns>读不到、不是片段、或剪贴板上的字已经不是我们那几行 ⇒ null，由调用方去走
    /// 文字／图片／文件那三条老路。</returns>
    public static async Task<string?> ReadFragmentAsync()
    {
        try
        {
            if (!File.Exists(FragmentPath))
            {
                return null;
            }

            var fragment = await File.ReadAllTextAsync(FragmentPath);

            if (!BoardFragment.LooksLikeFragment(fragment))
            {
                return null;
            }

            var expected = BoardFragment.ExpectedTextOf(fragment);

            if (expected is null || Clipboard.GetContent() is not { } view)
            {
                return null;
            }

            var onClipboard = view.Contains(StandardDataFormats.Text)
                ? await view.GetTextAsync()
                : null;

            // 对账：人在别处又复制了东西（哪怕只是一段一样的字以外的东西），这份就不作数。
            return string.Equals(onClipboard, expected, StringComparison.Ordinal) ? fragment : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or FormatException or InvalidOperationException
                || ex.HResult == unchecked((int)0x800401D0))
        {
            return null;
        }
    }

    private static async Task<IngestResult> ReadAsync(DataPackageView? view)
    {
        if (view is null)
        {
            return new IngestResult(IngestKind.Nothing, "剪贴板为空");
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
                    ? $"文本 {text.Length} 字，超过单格上限 {PastedImage.MaxTextCharacters}，已截断 {dropped} 字"
                    : $"文本 {text.Length} 字",
                Text: text);
        }

        var available = AvailableFormats(view);

        return new IngestResult(IngestKind.Nothing,
            available.Length == 0 ? "剪贴板为空" : $"剪贴板内容无法放置到板面：{available}");
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
                ? $"剪贴板图片 {width}×{height}（原图 alpha 全为 0，已按不透明修正）"
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
            $"文件 {Path.GetFileName(path)}（{(width > 0 ? $"{width}×{height}" : "尺寸无法读取，按默认尺寸放置")}）",
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
