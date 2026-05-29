using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RaycastPM.Services;

internal static class ClipboardImageNormalizer
{
    public static BitmapSource RestoreOpaqueAlphaIfFullyTransparent(BitmapSource image)
    {
        try
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            {
                return FreezeIfPossible(image);
            }

            var bgraImage = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

            if (bgraImage.CanFreeze)
            {
                bgraImage.Freeze();
            }

            var stride = checked((bgraImage.PixelWidth * bgraImage.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[checked(stride * bgraImage.PixelHeight)];
            bgraImage.CopyPixels(pixels, stride, 0);

            if (HasVisibleAlpha(pixels, bgraImage.PixelWidth, bgraImage.PixelHeight, stride))
            {
                return FreezeIfPossible(image);
            }

            MakeOpaque(pixels, bgraImage.PixelWidth, bgraImage.PixelHeight, stride);

            var restored = BitmapSource.Create(
                bgraImage.PixelWidth,
                bgraImage.PixelHeight,
                image.DpiX,
                image.DpiY,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            restored.Freeze();
            return restored;
        }
        catch
        {
            return FreezeIfPossible(image);
        }
    }

    private static bool HasVisibleAlpha(byte[] pixels, int width, int height, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                if (pixels[row + (x * 4) + 3] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void MakeOpaque(byte[] pixels, int width, int height, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                pixels[row + (x * 4) + 3] = 255;
            }
        }
    }

    private static BitmapSource FreezeIfPossible(BitmapSource image)
    {
        if (image.CanFreeze && !image.IsFrozen)
        {
            image.Freeze();
        }

        return image;
    }
}
