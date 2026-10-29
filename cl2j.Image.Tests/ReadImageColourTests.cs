using cl2j.Image;
using SkiaSharp;
using Xunit;

namespace cl2j.Image.Tests
{
    /// <summary>
    /// Reading in sRGB, added in 6.4.0. A phone records its photos in a colour space wider than
    /// sRGB (Display P3) and says so in an ICC profile. The encoders of this library write no
    /// profile, so a photo read as stored and encoded again is displayed as sRGB numbers it never
    /// had: duller than what was shot.
    /// </summary>
    public class ReadImageColourTests
    {
        //The colour painted, as an sRGB screen shows it. The P3 file stores other numbers for it.
        private static readonly (byte R, byte G, byte B) Painted = (200, 100, 50);

        [Fact]
        public void A_wide_gamut_photo_read_in_sRGB_keeps_the_colour_it_shows()
        {
            using var image = ImageUtils.ReadImage(DisplayP3Jpeg(), new ImageUtils.ReadSettings { ConvertToSrgb = true }, out var failure)!;

            Assert.Null(failure);
            AssertClose(Painted, image.GetPixel(8, 8));
        }

        [Fact]
        public void A_wide_gamut_photo_is_read_as_stored_by_default()
        {
            //What every consumer before 6.4.0 gets, and must keep getting: their pixels, thumbnails
            //and duplicate verdicts are built on the numbers as stored.
            using var image = ImageUtils.ReadImage(DisplayP3Jpeg())!;

            var (r, g, b) = image.GetPixel(8, 8);
            Assert.True(Math.Abs(r - Painted.R) > 6 || Math.Abs(g - Painted.G) > 6 || Math.Abs(b - Painted.B) > 6,
                $"the default read converted the colour: ({r}, {g}, {b})");
        }

        [Fact]
        public void A_photo_without_a_profile_reads_the_same_in_sRGB()
        {
            var bytes = Gradient(48, 32);

            using var stored = ImageUtils.ReadImage(bytes)!;
            using var srgb = ImageUtils.ReadImage(bytes, new ImageUtils.ReadSettings { ConvertToSrgb = true }, out _)!;

            for (var y = 0; y < stored.Height; ++y)
            {
                for (var x = 0; x < stored.Width; ++x)
                    Assert.Equal(stored.GetPixel(x, y), srgb.GetPixel(x, y));
            }
        }

        [Fact]
        public void Reading_in_sRGB_still_says_why_it_could_not_read()
        {
            var settings = new ImageUtils.ReadSettings { ConvertToSrgb = true };

            Assert.Null(ImageUtils.ReadImage([1, 2, 3, 4, 5], settings, out var garbage));
            Assert.False(string.IsNullOrWhiteSpace(garbage));

            Assert.Null(ImageUtils.ReadImage([], settings, out var empty));
            Assert.Equal("empty", empty);
        }

        private static void AssertClose((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual)
        {
            //A flat area at quality 100 comes back within a level or two of what was encoded.
            Assert.True(Math.Abs(expected.R - actual.R) <= 3 && Math.Abs(expected.G - actual.G) <= 3 && Math.Abs(expected.B - actual.B) <= 3,
                $"expected about {expected}, got {actual}");
        }

        // A flat JPEG tagged Display P3. Erase takes an sRGB colour and stores its P3 numbers, which
        // is what a phone does with what it shoots; Skia's JPEG encoder writes the ICC profile.
        private static byte[] DisplayP3Jpeg()
        {
            var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
            using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul, p3));
            bitmap.Erase(new SKColor(Painted.R, Painted.G, Painted.B));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
            return data.ToArray();
        }

        private static byte[] Gradient(int width, int height)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            for (var y = 0; y < height; ++y)
            {
                for (var x = 0; x < width; ++x)
                    bitmap.SetPixel(x, y, new SKColor((byte)(x * 5), (byte)(y * 7), 128));
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
    }
}
