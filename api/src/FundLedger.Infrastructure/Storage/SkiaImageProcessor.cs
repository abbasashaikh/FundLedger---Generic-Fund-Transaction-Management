using FundLedger.Application.Attachments;
using FundLedger.Application.Errors;
using SkiaSharp;

namespace FundLedger.Infrastructure.Storage;

/// <summary>
/// Re-encodes uploaded photos with SkiaSharp (MIT licence). Decoding and encoding again drops every metadata block (EXIF,
/// GPS location, camera and owner details), which is the point (TR-051). The photo's orientation is applied first so it still
/// looks upright. Images over 40 megapixels are refused before they are decoded, so a small file can't expand into gigabytes.
/// </summary>
public sealed class SkiaImageProcessor : IImageProcessor
{
    private const long MaxPixels = 40_000_000;

    public ProcessedImage Reencode(byte[] data, string contentType, int maxEdge)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(data)) ?? throw NotAnImage();
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
            {
                throw new UnsupportedMediaException("This photo is too large to process. Use a smaller photo.");
            }

            using var decoded = SKBitmap.Decode(codec) ?? throw NotAnImage();
            using var upright = Orient(decoded, codec.EncodedOrigin);
            using var sized = Fit(upright, maxEdge);

            using var image = SKImage.FromBitmap(sized);
            var (format, quality, outType) = contentType switch
            {
                "image/png" => (SKEncodedImageFormat.Png, 100, "image/png"),
                "image/webp" => (SKEncodedImageFormat.Webp, 85, "image/webp"),
                _ => (SKEncodedImageFormat.Jpeg, 85, "image/jpeg"),
            };
            using var encoded = image.Encode(format, quality) ?? throw NotAnImage();
            return new ProcessedImage(encoded.ToArray(), outType, sized.Width, sized.Height);
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw NotAnImage();
        }
    }

    private static UnsupportedMediaException NotAnImage() => new("This file isn't a readable photo. Take the photo again, or attach a PDF.");

    private static SKBitmap Fit(SKBitmap source, int maxEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxEdge)
        {
            return source.Copy();
        }

        var scale = (double)maxEdge / longEdge;
        var target = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), source.ColorType, source.AlphaType);
        return source.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? source.Copy();
    }

    /// <summary>Applies the EXIF orientation ("rotate 90° clockwise", "mirrored", ...) so the pixels themselves are upright.</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swap ? (source.Height, source.Width) : (source.Width, source.Height);
        var result = new SKBitmap(w, h, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return result;
    }
}
