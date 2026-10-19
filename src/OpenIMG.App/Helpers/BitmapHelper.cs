using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace OpenIMG.App.Helpers;

/// <summary>
/// Helper for loading bitmaps with correct EXIF orientation handling so that
/// portrait-mode camera images display upright in WPF (matching OpenCV, browsers, and mobile viewers).
/// </summary>
public static class BitmapHelper
{
    public static BitmapSource LoadBitmapWithExifOrientation(string filePath, int decodePixelWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Image file not found.", filePath);

        // Use the same decoder as Barrier 3. OpenCV applies EXIF orientation for
        // ImreadModes.Color, keeping the WPF viewer aligned with local validation.
        // The source is decoded in memory and is never rewritten.
        using var decoded = Cv2.ImRead(filePath, ImreadModes.Color);
        if (decoded.Empty())
            throw new InvalidOperationException($"Unable to decode image file: {filePath}");

        using var display = new Mat();
        if (decodePixelWidth > 0 && decoded.Width > decodePixelWidth)
        {
            var scale = (double)decodePixelWidth / decoded.Width;
            var targetHeight = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            Cv2.Resize(
                decoded,
                display,
                new OpenCvSharp.Size(decodePixelWidth, targetHeight),
                interpolation: InterpolationFlags.Area);
        }
        else
        {
            decoded.CopyTo(display);
        }

        var stride = checked(display.Width * (int)display.ElemSize());
        var pixels = new byte[checked(stride * display.Height)];
        Marshal.Copy(display.Data, pixels, 0, pixels.Length);

        var bitmap = BitmapSource.Create(
            display.Width,
            display.Height,
            96,
            96,
            PixelFormats.Bgr24,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static ushort GetExifOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata)
            return 1;

        // Standard EXIF orientation tag is 0x0112 (274)
        string[] queryPaths = ["/app1/ifd/{ushort=274}", "/app1/ifd/exif/{ushort=274}"];

        foreach (var query in queryPaths)
        {
            try
            {
                if (metadata.ContainsQuery(query))
                {
                    var val = metadata.GetQuery(query);
                    if (val is ushort uVal)
                        return uVal;
                    if (val is short sVal)
                        return (ushort)sVal;
                    if (val != null && ushort.TryParse(val.ToString(), out ushort parsed))
                        return parsed;
                }
            }
            catch
            {
                // Ignore metadata query errors
            }
        }

        return 1;
    }
}
