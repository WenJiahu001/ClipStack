using System.IO;
using System.Windows.Media.Imaging;

namespace ClipStack.App.Platform;

internal static class BitmapCodec
{
    internal static BitmapImage Decode(byte[] bytes, int decodePixelWidth = 0)
    {
        using var stream = new MemoryStream(bytes, false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
