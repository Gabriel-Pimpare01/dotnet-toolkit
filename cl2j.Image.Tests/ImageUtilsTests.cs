using cl2j.Image;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using ImageRgba32 = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace cl2j.Image.Tests
{
    /// <summary>
    /// These tests exist because of a precise failure: `cl2j.Image` relied on `System.Drawing`,
    /// which throws `PlatformNotSupportedException` off Windows since .NET 6. The Appartogo site
    /// runs on Linux, so its portal produced **no thumbnail at all** between June 2025 and
    /// September 2026 — 5,400 images, zero thumbnails, without one line of error, because the
    /// entry points swallowed the exception to return `null` or the original bytes.
    ///
    /// CI runs `dotnet test` on `ubuntu-latest`. These tests therefore execute on exactly the
    /// system where the failure occurred: they would catch it on the first try.
    /// </summary>
    public class ImageUtilsTests
    {
        [Fact]
        public void CleanImage_resizes_an_oversized_image()
        {
            var bytes = Jpeg(4000, 2252);

            var cleaned = ImageUtils.CleanImage(bytes, 1280);

            Assert.NotNull(cleaned);
            using var image = ImageUtils.ReadImage(cleaned!);
            Assert.NotNull(image);
            Assert.Equal(1280, image!.Width);
            Assert.True(image.Height <= 1280);
            Assert.True(cleaned!.Length < bytes.Length, "the cleaned image must weigh less than the original");
        }

        [Fact]
        public void CleanImage_leaves_the_dimensions_of_an_already_small_image()
        {
            var bytes = Jpeg(640, 480);

            var cleaned = ImageUtils.CleanImage(bytes, 1280);

            using var image = ImageUtils.ReadImage(cleaned!);
            Assert.Equal(640, image!.Width);
            Assert.Equal(480, image.Height);
        }

        [Fact]
        public void CleanImage_throws_on_unreadable_bytes()
        {
            //Silence was the defect: the old version returned the original bytes, so an unreadable
            //image was stored as-is without anyone knowing.
            Assert.ThrowsAny<Exception>(() => ImageUtils.CleanImage([1, 2, 3, 4, 5], 1280));
        }

        [Fact]
        public void CreateThumbnailCropped_returns_a_thumbnail_450x337_pour_du_640x480()
        {
            //Dimensions taken on September 3rd 2026 from a real CDN thumbnail, produced by the old
            //System.Drawing implementation from a 640x480 source. The port must return exactly the
            //same geometry.
            using var source = new ImageRgba32(640, 480);

            using var thumbnail = ImageUtils.CreateThumbnailCropped(source, 450, 338);

            Assert.Equal(450, thumbnail.Width);
            Assert.Equal(337, thumbnail.Height);
        }

        [Fact]
        public void CreateThumbnailCropped_produces_a_readable_jpeg()
        {
            using var source = ImageUtils.ReadImage(Jpeg(4000, 2252))!;

            using var thumbnail = ImageUtils.CreateThumbnailCropped(source, 450, 338);
            var bytes = ImageSerialization.SaveJpegToBytes(thumbnail, 75L);

            Assert.NotEmpty(bytes);
            Assert.Equal("JPEG", SixLabors.ImageSharp.Image.Identify(bytes).Metadata.DecodedImageFormat?.Name);

            using var relue = ImageUtils.ReadImage(bytes);
            Assert.NotNull(relue);
            Assert.Equal(450, relue!.Width);
            Assert.Equal(338, relue.Height);
        }

        [Fact]
        public void ReadImage_returns_null_on_unreadable_bytes()
        {
            Assert.Null(ImageUtils.ReadImage([1, 2, 3, 4, 5]));
        }

        [Fact]
        public void IsImage_recognises_a_jpeg()
        {
            Assert.True(ImageUtils.IsImage(Jpeg(64, 48)));
        }

        [Theory]
        [InlineData("<!DOCTYPE html>\r\n<html lang=\"fr\"><head><title>LogisQuebec</title></head></html>")]
        [InlineData("")]
        [InlineData("not an image at all")]
        public void IsImage_refuses_what_is_not_an_image(string content)
        {
            //The case that matters is the first: a source that redirects its deleted photos to its
            //home page returns HTML with a 200, and HttpClient follows the redirect on its own.
            //Without this guard, the page is stored under a .jpg name.
            Assert.False(ImageUtils.IsImage(System.Text.Encoding.UTF8.GetBytes(content)));
        }

        [Fact]
        public void IsImage_refuses_missing_bytes()
        {
            Assert.False(ImageUtils.IsImage(null!));
        }

        [Fact]
        public void Strip_removes_the_exif_profile()
        {
            using var image = new ImageRgba32(10, 10);
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Copyright, "cl2j");

            var modifie = ExifUtils.Strip(image);

            Assert.True(modifie);
            Assert.Null(image.Metadata.ExifProfile);
        }

        [Fact]
        public void RotateFlipIfRequired_applies_the_orientation_then_removes_it()
        {
            //Orientation 6 = 90 degree rotation: a wide image must come back tall.
            using var image = new ImageRgba32(100, 50);
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);

            var modifie = ExifUtils.RotateFlipIfRequired(image);

            Assert.True(modifie);
            Assert.Equal(50, image.Width);
            Assert.Equal(100, image.Height);
        }

        [Fact]
        public void RotateFlipIfRequired_leaves_an_already_upright_image_alone()
        {
            using var image = new ImageRgba32(100, 50);

            Assert.False(ExifUtils.RotateFlipIfRequired(image));
            Assert.Equal(100, image.Width);
        }

        //A gradient rather than a flat image: a flat JPEG compresses to almost nothing, and the
        //size-reduction test would then prove very little.
        // --- Formats not rendered by browsers ----------------------------------------------------
        //
        // Context: of the 124 files the portal backfill refused on September 4th 2026, 93 were
        // HEIC and 2 were AVIF — iPhone photos uploaded as-is and stored under a `.jpg` name.
        // Client decision the same day: **no native library** will be added to decode them; the
        // conversion will happen in the browser. The server, for its part, must refuse cleanly and
        // be able to *name* what it refuses.
        //
        // TIFF serves as the control here: ImageSharp reads it, but no browser displays it. That
        // is exactly the case the new re-encoding rule must catch, and it can be generated without
        // installing anything.

        [Fact]
        public void CleanImage_reencodes_a_format_the_browser_does_not_render()
        {
            // The heart of the fix. A 320 x 240 image exceeds no dimension and has no EXIF to
            // straighten: the old version therefore returned the original bytes untouched.
            var bytes = Tiff(320, 240);

            var cleaned = ImageUtils.CleanImage(bytes, 1280);

            Assert.NotNull(cleaned);
            Assert.Equal("JPEG", SixLabors.ImageSharp.Image.DetectFormat(cleaned!).Name);
        }

        [Fact]
        public void CleanImage_does_not_reencode_an_already_compliant_JPEG()
        {
            // The counterpart of the previous test: the rule must not needlessly recompress what is
            // already served correctly.
            var bytes = Jpeg(320, 240);

            Assert.Same(bytes, ImageUtils.CleanImage(bytes, 1280));
        }

        [Theory]
        [InlineData("heic", "HEIC")]
        [InlineData("mif1", "HEIC")]
        [InlineData("avif", "AVIF")]
        [InlineData("qt  ", "video QuickTime")]
        [InlineData("mp42", "video MP4")]
        public void NameUnsupportedFormat_recognises_the_portal_brands(string marque, string expected)
        {
            // The five families actually found in listing-portal. Without this name, the portal
            // cannot tell the user why their photo was refused — and a mute refusal is precisely
            // what cost fifteen months of missing thumbnails.
            Assert.Equal(expected, ImageUtils.NommerUnFormatNonSupporte(BoiteIsoBmff(marque)));
        }

        [Fact]
        public void NameUnsupportedFormat_does_not_name_what_it_does_not_recognise()
        {
            Assert.Null(ImageUtils.NommerUnFormatNonSupporte(Jpeg(32, 32)));
            Assert.Null(ImageUtils.NommerUnFormatNonSupporte(BoiteIsoBmff("zzzz")));
            Assert.Null(ImageUtils.NommerUnFormatNonSupporte([1, 2, 3]));
            Assert.Null(ImageUtils.NommerUnFormatNonSupporte(null!));
        }

        [Fact]
        public void ReadImage_says_why_it_could_not_read()
        {
            //What the silence cost: fifteen months of missing thumbnails where the caller knew it
            //had failed and no one knew the decoder simply does not read HEIC.
            Assert.Null(ImageUtils.ReadImage(BoiteIsoBmff("heic"), out var heic));
            Assert.Contains("HEIC", heic);

            Assert.Null(ImageUtils.ReadImage([1, 2, 3, 4, 5], out var garbage));
            Assert.False(string.IsNullOrWhiteSpace(garbage));

            Assert.Null(ImageUtils.ReadImage([], out var empty));
            Assert.Equal("empty", empty);

            Assert.Null(ImageUtils.ReadImage(null!, out var none));
            Assert.Equal("no bytes", none);
        }

        [Fact]
        public void ReadImage_says_nothing_when_it_reads()
        {
            using var image = ImageUtils.ReadImage(Jpeg(64, 48), out var failure);

            Assert.NotNull(image);
            Assert.Null(failure);
        }

        private static byte[] BoiteIsoBmff(string marque)
        {
            // Four bytes of size, the `ftyp` tag, then the brand: the header of an ISO-BMFF
            // container. What follows does not matter, nothing decodes it.
            var bytes = new byte[16];
            bytes[3] = 16;
            System.Text.Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
            System.Text.Encoding.ASCII.GetBytes(marque).CopyTo(bytes, 8);
            return bytes;
        }

        private static byte[] Tiff(int width, int height)
        {
            using var image = new ImageRgba32(width, height);
            using var ms = new MemoryStream();
            image.Save(ms, new SixLabors.ImageSharp.Formats.Tiff.TiffEncoder());
            return ms.ToArray();
        }

        private static byte[] Jpeg(int width, int height)


        {
            using var image = new ImageRgba32(width, height);
            for (var y = 0; y < height; ++y)
            {
                for (var x = 0; x < width; ++x)
                    image[x, y] = new Rgba32((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256));
            }

            using var ms = new MemoryStream();
            image.Save(ms, new JpegEncoder { Quality = 90 });
            return ms.ToArray();
        }
    }
}
