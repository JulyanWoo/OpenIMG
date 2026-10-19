using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenIMG.App.Helpers;

namespace OpenIMG.Tests;

[Trait("Category", TestCategories.Unit)]
public class BitmapHelperTests
{
    [Fact]
    public void LoadBitmapWithExifOrientation_FileNotFound_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"openimg-missing-{Guid.NewGuid():N}.jpg");

        Assert.Throws<FileNotFoundException>(() =>
            BitmapHelper.LoadBitmapWithExifOrientation(missingPath));
    }

    [Fact]
    public void LoadBitmapWithExifOrientation_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            BitmapHelper.LoadBitmapWithExifOrientation(string.Empty));
    }

    [Fact]
    public void LoadBitmapWithExifOrientation_SyntheticExifOrientation6_RotatesUpright()
    {
        RunOnSta(() =>
        {
            using var image = SyntheticJpeg.Create(width: 80, height: 40, orientation: 6);

            var result = BitmapHelper.LoadBitmapWithExifOrientation(image.Path);

            Assert.Equal(40, result.PixelWidth);
            Assert.Equal(80, result.PixelHeight);
        });
    }

    [Fact]
    public void LoadBitmapWithExifOrientation_WithDecodePixelWidth_ScalesProportionally()
    {
        RunOnSta(() =>
        {
            using var image = SyntheticJpeg.Create(width: 1600, height: 800, orientation: 1);

            var result = BitmapHelper.LoadBitmapWithExifOrientation(image.Path, decodePixelWidth: 1280);

            Assert.Equal(1280, result.PixelWidth);
            Assert.Equal(640, result.PixelHeight);
        });
    }

    [Fact]
    public void LoadBitmapWithExifOrientation_MatchesOpenCvDecodedDimensions()
    {
        RunOnSta(() =>
        {
            using var image = SyntheticJpeg.Create(width: 160, height: 80, orientation: 6);
            var wpfBitmap = BitmapHelper.LoadBitmapWithExifOrientation(image.Path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(wpfBitmap));
            using var encoded = new MemoryStream();
            encoder.Save(encoded);

            using var wpfDecoded = Cv2.ImDecode(encoded.ToArray(), ImreadModes.Color);
            using var openCvDecoded = Cv2.ImRead(image.Path, ImreadModes.Color);

            Assert.False(wpfDecoded.Empty());
            Assert.False(openCvDecoded.Empty());
            Assert.Equal(openCvDecoded.Width, wpfDecoded.Width);
            Assert.Equal(openCvDecoded.Height, wpfDecoded.Height);
        });
    }

    private static void RunOnSta(Action action) => RunOnSta(() =>
    {
        action();
        return true;
    });

    private static T RunOnSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return result!;
    }

    private sealed class SyntheticJpeg : IDisposable
    {
        private SyntheticJpeg(string directoryPath, string path)
        {
            DirectoryPath = directoryPath;
            Path = path;
        }

        private string DirectoryPath { get; }
        public string Path { get; }

        public static SyntheticJpeg Create(int width, int height, ushort orientation)
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenIMG-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, "fixture.jpg");

            var pixels = new byte[checked(width * height * 3)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * width + x) * 3;
                    pixels[offset] = (byte)(x % 251);
                    pixels[offset + 1] = (byte)(y % 251);
                    pixels[offset + 2] = (byte)((x + y) % 251);
                }
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgr24,
                palette: null,
                pixels,
                stride: width * 3);
            bitmap.Freeze();

            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
            var frame = BitmapFrame.Create(bitmap, thumbnail: null, metadata, colorContexts: null);
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            encoder.Frames.Add(frame);
            using (var output = File.Create(path))
                encoder.Save(output);

            return new SyntheticJpeg(directoryPath, path);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
