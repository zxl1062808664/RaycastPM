using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RaycastPM.Models;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataObject = System.Windows.IDataObject;
using WpfDataFormats = System.Windows.DataFormats;

namespace RaycastPM.Services;

public sealed class ClipboardMonitor
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private string? _lastText;
    private string? _lastImageFingerprint;
    private string? _normalizingImageFingerprint;

    public ClipboardMonitor()
    {
        _timer.Tick += (_, _) => Poll();
    }

    public event EventHandler<ClipboardEntry>? ClipboardChanged;
    public bool IgnoreNextChange { get; set; }

    public void Start()
    {
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void RememberText(string text)
    {
        _lastText = text.Trim();
        _lastImageFingerprint = null;
        _normalizingImageFingerprint = null;
    }

    public void RememberImage(BitmapSource image)
    {
        image = ClipboardImageNormalizer.RestoreOpaqueAlphaIfFullyTransparent(image);
        var bytes = EncodePng(image);
        _lastImageFingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        _normalizingImageFingerprint = null;
        _lastText = null;
    }

    private void Poll()
    {
        try
        {
            if (IgnoreNextChange)
            {
                IgnoreNextChange = false;
                return;
            }

            var data = WpfClipboard.GetDataObject();
            var files = TryGetClipboardFiles(data);
            if (files.Count > 0)
            {
                var content = ClipboardClassifier.NormalizeFileContent(files);
                if (!string.IsNullOrWhiteSpace(content) && content != _lastText)
                {
                    _lastText = content;
                    _lastImageFingerprint = null;
                    _normalizingImageFingerprint = null;
                    ClipboardChanged?.Invoke(this, new ClipboardEntry
                    {
                        Kind = ClipboardItemKind.File,
                        Content = content,
                        Source = "Windows"
                    });
                }

                return;
            }

            var image = TryGetClipboardImage(out var needsNormalization);
            if (image is not null)
            {
                image = ClipboardImageNormalizer.RestoreOpaqueAlphaIfFullyTransparent(image);
                var bytes = EncodePng(image);
                var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));

                if (needsNormalization && fingerprint != _normalizingImageFingerprint)
                {
                    _normalizingImageFingerprint = fingerprint;
                    _lastText = null;
                    WpfClipboard.SetImage(image);
                    return;
                }

                if (fingerprint != _lastImageFingerprint)
                {
                    _lastImageFingerprint = fingerprint;
                    _normalizingImageFingerprint = null;
                    _lastText = null;
                    ClipboardChanged?.Invoke(this, new ClipboardEntry
                    {
                        Kind = ClipboardItemKind.Image,
                        Content = $"图片 {image.PixelWidth}x{image.PixelHeight}",
                        ImageBytes = bytes,
                        Source = "Windows"
                    });
                }

                return;
            }

            if (WpfClipboard.ContainsText())
            {
                var text = WpfClipboard.GetText();
                var content = text.Trim();
                if (!string.IsNullOrWhiteSpace(content) && content != _lastText)
                {
                    _lastText = content;
                    _lastImageFingerprint = null;
                    _normalizingImageFingerprint = null;
                    ClipboardChanged?.Invoke(this, new ClipboardEntry
                    {
                        Kind = ClipboardClassifier.ClassifyText(content),
                        Content = content,
                        Source = "Windows"
                    });
                }

                return;
            }
        }
        catch
        {
            // Clipboard can be temporarily locked by another process.
        }
    }

    private static BitmapSource? TryGetClipboardImage(out bool needsNormalization)
    {
        needsNormalization = false;

        if (WpfClipboard.ContainsImage())
        {
            var image = WpfClipboard.GetImage();
            if (image is not null)
            {
                return image;
            }
        }

        var data = WpfClipboard.GetDataObject();
        if (data is null)
        {
            return null;
        }

        if (TryDecodeBitmap(GetData(data, WpfDataFormats.Bitmap), false) is { } bitmap)
        {
            needsNormalization = true;
            return bitmap;
        }

        foreach (var file in TryGetClipboardImageFiles(data))
        {
            if (TryLoadImageFile(file) is { } fileImage)
            {
                needsNormalization = true;
                return fileImage;
            }
        }

        foreach (var format in PreferredImageFormats(data))
        {
            if (TryDecodeBitmap(GetData(data, format), IsDibFormat(format)) is { } image)
            {
                needsNormalization = true;
                return image;
            }
        }

        var nativeImage = TryGetNativeClipboardImage();
        needsNormalization = nativeImage is not null;
        return nativeImage;
    }

    private static BitmapSource? TryGetNativeClipboardImage()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            if (TryDecodeNativeBitmapHandle(CfBitmap) is { } bitmap)
            {
                return bitmap;
            }

            foreach (var (format, isDib) in new[]
            {
                (CfDibV5, true),
                (CfDib, true),
                (RegisterClipboardFormat("PNG"), false),
                (RegisterClipboardFormat("image/png"), false),
                (RegisterClipboardFormat("JFIF"), false)
            })
            {
                if (format == 0)
                {
                    continue;
                }

                var bytes = ReadNativeClipboardBytes(format);
                if (bytes is not null && TryDecodeBitmap(bytes, isDib) is { } image)
                {
                    return image;
                }
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static BitmapSource? TryDecodeNativeBitmapHandle(uint format)
    {
        try
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadNativeClipboardBytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = GlobalSize(handle);
        if (size == UIntPtr.Zero || size.ToUInt64() > int.MaxValue)
        {
            return null;
        }

        var locked = GlobalLock(handle);
        if (locked == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[(int)size];
            System.Runtime.InteropServices.Marshal.Copy(locked, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static object? GetData(WpfDataObject data, string format)
    {
        try
        {
            if (data.GetDataPresent(format, false))
            {
                return data.GetData(format, false);
            }

            return data.GetDataPresent(format, true) ? data.GetData(format, true) : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> PreferredImageFormats(WpfDataObject data)
    {
        var preferred = new[]
        {
            "PNG",
            "image/png",
            "JFIF",
            "JPEG",
            "image/jpeg",
            "DeviceIndependentBitmapV5",
            WpfDataFormats.Dib,
            WpfDataFormats.Bitmap
        };

        var discovered = (data.GetFormats(false) ?? [])
            .Concat(data.GetFormats(true) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return preferred
            .Concat(discovered)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsDibFormat(string format)
    {
        return format.Equals(WpfDataFormats.Dib, StringComparison.OrdinalIgnoreCase)
            || format.Equals("DeviceIndependentBitmapV5", StringComparison.OrdinalIgnoreCase)
            || format.Contains("DIB", StringComparison.OrdinalIgnoreCase)
            || format.Contains("DeviceIndependentBitmap", StringComparison.OrdinalIgnoreCase);
    }

    private static BitmapSource? TryDecodeBitmap(object? value, bool isDib)
    {
        try
        {
            if (value is BitmapSource source)
            {
                return source;
            }

            if (TryDecodeDrawingBitmap(value) is { } drawingBitmap)
            {
                return drawingBitmap;
            }

            var bytes = value switch
            {
                byte[] raw => raw,
                MemoryStream memory => memory.ToArray(),
                Stream stream => ReadAllBytes(stream),
                _ => null
            };

            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            if (isDib && TryDecodeImageBytes(WrapDibAsBmp(bytes)) is { } dibImage)
            {
                return dibImage;
            }

            return TryDecodeImageBytes(bytes) ?? TryDecodeImageBytes(WrapDibAsBmp(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryDecodeDrawingBitmap(object? value)
    {
        try
        {
            if (value is not System.Drawing.Bitmap bitmap)
            {
                return null;
            }

            var handle = bitmap.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    handle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryDecodeImageBytes(byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0)
            {
                return null;
            }

            using var imageStream = new MemoryStream(bytes);
            var image = BitmapFrame.Create(
                imageStream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> TryGetClipboardImageFiles(WpfDataObject data)
    {
        foreach (var file in TryGetClipboardFiles(data))
        {
            yield return file;
        }

        foreach (var path in TryGetImageFilesFromHtml(GetData(data, WpfDataFormats.Html) as string))
        {
            yield return path;
        }

        foreach (var path in TryGetImageFilesFromHtml(GetData(data, "HTML Format") as string))
        {
            yield return path;
        }
    }

    private static IReadOnlyList<string> TryGetClipboardFiles(WpfDataObject? data)
    {
        if (data is null)
        {
            return [];
        }

        var files = new List<string>();
        AddClipboardFiles(files, GetData(data, WpfDataFormats.FileDrop));

        foreach (var format in new[] { "FileNameW", "FileName" })
        {
            AddClipboardFiles(files, GetData(data, format));
        }

        return files
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddClipboardFiles(List<string> files, object? value)
    {
        switch (value)
        {
            case string file:
                files.Add(file);
                break;
            case string[] droppedFiles:
                files.AddRange(droppedFiles);
                break;
            case System.Collections.Specialized.StringCollection fileCollection:
                files.AddRange(fileCollection.Cast<string>());
                break;
        }
    }

    private static IEnumerable<string> TryGetImageFilesFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(html, """src\s*=\s*["'](?<src>[^"']+)["']""", RegexOptions.IgnoreCase))
        {
            var src = match.Groups["src"].Value;
            if (Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                yield return uri.LocalPath;
            }
            else if (File.Exists(src))
            {
                yield return src;
            }
        }
    }

    private static BitmapSource? TryLoadImageFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var extension = Path.GetExtension(path);
            if (!new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" }
                    .Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return TryDecodeImageBytes(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static byte[] WrapDibAsBmp(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return dib;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var colorsUsed = headerSize >= 40 && dib.Length >= 36 ? BitConverter.ToInt32(dib, 32) : 0;
        var paletteSize = colorsUsed > 0
            ? colorsUsed * 4
            : bitCount <= 8
                ? (1 << bitCount) * 4
                : 0;
        var pixelOffset = 14 + headerSize + paletteSize;
        var fileSize = 14 + dib.Length;
        var bmp = new byte[fileSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
