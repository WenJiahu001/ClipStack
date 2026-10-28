using System.IO;
using System.Windows.Media.Imaging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClipStack.App.Platform;

internal static class BitmapCodec
{
    private static readonly SemaphoreSlim imageWork = new(1, 1);
    // 原图解码、编码及预览共享一个重任务槽，避免多个大图处理同时抬高峰值。
    internal static async Task<T> ProcessAsync<T>(Func<T> work)
    {
        await imageWork.WaitAsync();
        try { return await Task.Run(work); }
        finally { imageWork.Release(); }
    }
    internal static BitmapImage Decode(byte[] bytes, int decodePixelWidth = 0)
    {
        using var stream = new MemoryStream(bytes, false);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
