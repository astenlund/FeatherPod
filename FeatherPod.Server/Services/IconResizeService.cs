using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace FeatherPod.Server.Services;

public class IconResizeService(IBlobStorageService blobStorageService, IMemoryCache memoryCache, ILogger<IconResizeService> logger)
{
    private static readonly HashSet<int> AllowedSizes = [192, 512];

    public static bool IsValidSize(int size) => AllowedSizes.Contains(size);

    public void InvalidateCache(string feedId)
    {
        foreach (var size in AllowedSizes)
        {
            memoryCache.Remove($"icon-{feedId}-{size}");
        }
    }

    public async Task<byte[]?> GetResizedIconAsync(string feedId, int size)
    {
        var cacheKey = $"icon-{feedId}-{size}";

        if (memoryCache.TryGetValue(cacheKey, out byte[]? cached))
        {
            return cached;
        }

        try
        {
            // Buffer the blob stream into memory — blob storage streams are non-seekable
            await using var blobStream = await blobStorageService.DownloadIconAsync(feedId);
            using var memoryStream = new MemoryStream();
            await blobStream.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var originalBitmap = SKBitmap.Decode(memoryStream);
            if (originalBitmap == null)
            {
                logger.LogWarning("SkiaSharp failed to decode icon for feed {FeedId}", feedId);

                return null;
            }

            using var resizedBitmap = originalBitmap.Resize(new SKSizeI(size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (resizedBitmap == null)
            {
                logger.LogWarning("SkiaSharp failed to resize icon for feed {FeedId} to {Size}", feedId, size);

                return null;
            }

            using var image = SKImage.FromBitmap(resizedBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = data.ToArray();

            var cacheOptions = new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(1)
            };
            memoryCache.Set(cacheKey, bytes, cacheOptions);

            return bytes;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resize icon for feed {FeedId} to {Size}", feedId, size);

            return null;
        }
    }
}
