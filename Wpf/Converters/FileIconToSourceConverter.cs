using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RaycastPM.Converters;

public sealed class FileIconToSourceConverter : IValueConverter
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value?.ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Cache.GetOrAdd(path, LoadIcon);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }

    private static BitmapSource? LoadIcon(string path)
    {
        try
        {
            var result = SHGetFileInfo(
                path,
                0,
                out var fileInfo,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                ShgfiIcon | ShgfiLargeIcon);

            if (result == IntPtr.Zero || fileInfo.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(
                    fileInfo.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(36, 36));
                image.Freeze();
                return image;
            }
            finally
            {
                DestroyIcon(fileInfo.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        out SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
