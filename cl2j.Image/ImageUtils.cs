using ImageRgba32 = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using cl2j.Tooling.Exceptions;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace cl2j.Image
{
    /// <summary>
    /// Image utilities, built on ImageSharp.
    ///
    /// **Ported from System.Drawing on September 3rd 2026.** The reason is not modernisation:
    /// `System.Drawing.Common` throws `PlatformNotSupportedException` on anything but Windows
    /// since .NET 6. The Appartogo site runs on Linux, so its portal had **never** produced a
    /// single thumbnail since it opened in June 2025 — 5,400 images, zero thumbnails, without one
    /// line of error, because both entry points swallowed the exception to return `null` or the
    /// original bytes. See entry s19 of the cl2j repository journal.
    ///
    /// The same failure was waiting for the crawler: it would have fired the day aggregation
    /// leaves the Windows VM for a Function or a Linux container.
    ///
    /// **Ownership convention, unchanged:** the images returned belong to the caller, who must
    /// dispose them. Some methods return the instance they received when there is nothing to do —
    /// `Resize` at equal size, `Crop` with no border, `CropCenter` on an already smaller image.
    /// Do not dispose a result without knowing whether it is the original.
    /// </summary>
    public static class ImageUtils
    {
        public class OptimizeReasult
        {
            public int Width { get; set; }
            public int Height { get; set; }

            public bool Modified { get; set; }
        }

        public static OptimizeReasult? OptimizeImage(ref byte[] bytes, int max = 1280, long quality = 75L)
        {
            using var image = ReadImage(bytes);
            if (image != null)
            {
                var modified = ExifUtils.RotateFlipIfRequired(image);
                modified |= ExifUtils.Strip(image);

                if (image.Width > max || image.Height > max)
                {
                    using var resized = ImageResizer.ResizeIfOversize(image, max, max);
                    bytes = ImageSerialization.SaveJpegToBytes(resized, quality);

                    return new OptimizeReasult
                    {
                        Modified = true,
                        Width = resized.Width,
                        Height = resized.Height
                    };
                }

                //Same reason as in CleanImage: a format browsers do not render must come back
                //re-encoded, even when nothing else calls for it.
                if (modified || !EstUnFormatDuWeb(bytes))
                {
                    bytes = ImageSerialization.SaveJpegToBytes(image, quality);
                    modified = true;
                }

                return new OptimizeReasult
                {
                    Modified = modified,
                    Width = image.Width,
                    Height = image.Height
                };
            }

            return null;
        }

        public static ImageRgba32 CreateThumbnailCropped(byte[] bytes, int w, int h)
        {
            using var image = ReadImage(bytes) ?? throw new ValidationException("Invalid image");
            return CreateThumbnailCropped(image, w, h);
        }

        public static ImageRgba32 CreateThumbnailCropped(ImageRgba32 image, int w, int h)
        {
            var currentRatio = Math.Round((decimal)image.Width / image.Height, 2);
            var targetRatio = Math.Round((decimal)w / h, 2);

            if (currentRatio == targetRatio)
                return ImageResizer.ResizeIfOversize(image, w, h);

            int newW;
            int newH;
            if (currentRatio < targetRatio)
            {
                newW = image.Width;
                newH = (int)Math.Round(image.Width / targetRatio, 0);
            }
            else
            {
                newW = (int)Math.Round(image.Height * targetRatio, 0);
                newH = image.Height;
            }

            var croppedImage = CropCenter(image, newW, newH, out var cropped);

            if (croppedImage.Width == w && croppedImage.Height == h)
                return cropped ? croppedImage : croppedImage.Clone();

            var thumbnail = ImageResizer.Resize(croppedImage, w, h);
            if (cropped && !ReferenceEquals(thumbnail, croppedImage))
                croppedImage.Dispose();
            return thumbnail;
        }

        public static ImageRgba32 CreateThumbnail(ImageRgba32 image, int w, int h, Rgba32 backgroundColor)
        {
            var currentRatio = Math.Round((decimal)image.Width / image.Height, 2);
            var targetRatio = Math.Round((decimal)w / h, 2);

            if (currentRatio == targetRatio)
                return ImageResizer.ResizeIfOversize(image, w, h);

            int newW;
            int newH;
            int x;
            int y;
            if (currentRatio < targetRatio)
            {
                newW = (int)Math.Round(h * currentRatio, 0);
                newH = h;
                x = (w - newW) / 2;
                y = 0;
            }
            else
            {
                newW = w;
                newH = (int)Math.Round(w / currentRatio, 0);
                x = 0;
                y = (h - newH) / 2;
            }

            using var resized = ImageResizer.Resize(image, Math.Max(1, newW), Math.Max(1, newH));

            var target = new ImageRgba32(w, h);
            target.Mutate(g =>
            {
                g.BackgroundColor(backgroundColor);
                g.DrawImage(resized, new SixLabors.ImageSharp.Point(x, y), 1f);
            });

            return target;
        }

        public static ImageRgba32 CreateThumbnailWithRatio(ImageRgba32 image, int w)
        {
            var ratio = Math.Round((decimal)image.Width / image.Height, 2);
            int h = (int)Math.Round(w / ratio, 0);

            return ImageResizer.Resize(image, w, Math.Max(1, h));
        }

        public static ImageRgba32 Crop(ImageRgba32 bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;

            int topmost = 0;
            for (int row = 0; row < h; ++row)
            {
                if (IsAllColorRow(bmp, row))
                    topmost = row + 1;
                else
                    break;
            }

            int bottommost = 0;
            for (int row = h - 1; row >= 0; --row)
            {
                if (IsAllColorRow(bmp, row))
                    bottommost = row;
                else
                    break;
            }

            int leftmost = 0;
            for (int col = 0; col < w; ++col)
            {
                if (IsAllColorColumn(bmp, col))
                    leftmost = col + 1;
                else
                    break;
            }

            int rightmost = 0;
            for (int col = w - 1; col >= 0; --col)
            {
                if (IsAllColorColumn(bmp, col))
                    rightmost = col;
                else
                    break;
            }

            if (rightmost == 0)
                rightmost = w; // As reached left
            if (bottommost == 0)
                bottommost = h; // As reached top.

            int croppedWidth = rightmost - leftmost;
            int croppedHeight = bottommost - topmost;

            if (croppedWidth == 0) // No border on left or right
            {
                leftmost = 0;
                croppedWidth = w;
            }

            if (croppedHeight == 0) // No border on top or bottom
            {
                topmost = 0;
                croppedHeight = h;
            }

            if (croppedWidth == bmp.Width && croppedHeight == bmp.Height)
                return bmp;

            //An entirely white image gives crossed bounds: the old version then threw a
            //BadRequestException from Graphics.DrawImage. We keep the same signal, but throw up
            //front rather than waiting for the library.
            if (croppedWidth <= 0 || croppedHeight <= 0 || leftmost + croppedWidth > w || topmost + croppedHeight > h)
                throw new BadRequestException($"Values are topmost={topmost} btm={bottommost} left={leftmost} right={rightmost} croppedWidth={croppedWidth} croppedHeight={croppedHeight}");

            return bmp.Clone(x => x.Crop(new SixLabors.ImageSharp.Rectangle(leftmost, topmost, croppedWidth, croppedHeight)));
        }

        public static ImageRgba32 CropCenter(ImageRgba32 bmp, int w, int h, out bool modified)
        {
            modified = false;
            if (bmp.Width < w || bmp.Height < h)
                return bmp;
            if (bmp.Width == w && bmp.Height == h)
                return bmp;

            int x = (bmp.Width - w) / 2;
            int y = (bmp.Height - h) / 2;

            modified = true;

            return bmp.Clone(c => c.Crop(new SixLabors.ImageSharp.Rectangle(x, y, w, h)));
        }

        public class ImageCompareSettings
        {
            public int PixelDifferenceTolerance { get; set; } = 19;
            public int CompareDifferenceMax { get; set; } = 30;
            public double PourcentEqualsMin { get; set; } = 0.6;
        }

        public static bool AreImagesIdentical(ImageRgba32 image1, ImageRgba32 image2, ImageCompareSettings settings)
        {
            if (image1 == null || image2 == null)
                return false;

            ImageRgba32? newImage = null;
            ImageRgba32? newImageCrawler = null;
            try
            {
                //Crop images (remove white lines/columns) surronding
                newImage = Crop(image2);
                var imageRatio = (double)newImage.Width / newImage.Height;

                newImageCrawler = Crop(image1);
                var imageCrawlerRatio = (double)newImageCrawler.Width / newImageCrawler.Height;

                //Ratio is different --> Images are differents
                if (Math.Abs(imageRatio - imageCrawlerRatio) > 0.01)
                    return false;

                //Resize images if required to have the same size for the comparaison
                if (newImage.Width > newImageCrawler.Width)
                    newImage = Remplacer(newImage, image2, ImageResizer.Resize(newImage, newImageCrawler.Width, newImageCrawler.Height));
                else
                    newImageCrawler = Remplacer(newImageCrawler, image1, ImageResizer.Resize(newImageCrawler, newImage.Width, newImage.Height));

                //Compare
                var res = Compare(newImage, newImageCrawler, out var diff);
                if (res)
                {
                    var pourcentEquals = Equals(newImage, newImageCrawler, settings.PixelDifferenceTolerance);
                    if (diff <= settings.CompareDifferenceMax && pourcentEquals >= settings.PourcentEqualsMin)
                        return true;
                }

                return false;
            }
            finally
            {
                //The intermediates are ours, the originals are not. Crop and Resize sometimes
                //return the instance they received: that is what the reference comparison checks.
                //Without this care, aggregation disposed its caller images — and across tens of
                //thousands of comparisons, disposing nothing at all cost memory.
                Liberer(newImage, image1, image2);
                Liberer(newImageCrawler, image1, image2);
            }
        }

        private static ImageRgba32 Remplacer(ImageRgba32 ancienne, ImageRgba32 original, ImageRgba32 nouvelle)
        {
            if (!ReferenceEquals(ancienne, original) && !ReferenceEquals(ancienne, nouvelle))
                ancienne.Dispose();
            return nouvelle;
        }

        private static void Liberer(ImageRgba32? image, ImageRgba32 original1, ImageRgba32 original2)
        {
            if (image is not null && !ReferenceEquals(image, original1) && !ReferenceEquals(image, original2))
                image.Dispose();
        }

        public static bool Compare(ImageRgba32 image1, ImageRgba32 image2, out int diff)
        {
            diff = 0;

            if (image1.Width != image2.Width || image1.Height != image2.Height)
                return false;

            long total = 0;
            for (int y = 0; y < image1.Height; ++y)
            {
                for (int x = 0; x < image1.Width; ++x)
                    total += image1[x, y].DiffGrayscale(image2[x, y]);
            }

            diff = (int)(total / (image1.Width * image1.Height));

            return true;
        }

        public static double Equals(ImageRgba32 image1, ImageRgba32 image2, int pixelDiffMax)
        {
            if (image1.Width != image2.Width || image1.Height != image2.Height)
                return 0;

            int nbPixelEquals = 0;
            for (int y = 0; y < image1.Height; ++y)
            {
                for (int x = 0; x < image1.Width; ++x)
                {
                    if (image1[x, y].DiffGrayscale(image2[x, y]) <= pixelDiffMax)
                        ++nbPixelEquals;
                }
            }

            return (double)nbPixelEquals / (image1.Width * image1.Height);
        }

        public static byte[]? CleanImage(byte[] bytes, int max = 1280)
        {
            if (bytes == null)
                return bytes;

            //No more fallback, and that is the heart of the fix. The old version chained two
            //attempts that both ended in System.Drawing, then returned the original bytes without
            //saying anything: on Linux, every image came back as-is, not resized — 2.4 MB measured
            //on one portal photo.
            //
            //The exception is now allowed to propagate. An unreadable image is an error the caller
            //must see, not a silence to store.
            using var image = ReadImage(bytes)
                ?? throw new ValidationException("Invalid image: no decoder could read it.");

            var modified = ExifUtils.RotateFlipIfRequired(image);
            modified |= ExifUtils.Strip(image);

            if (image.Width > max || image.Height > max)
            {
                using var resized = ImageResizer.ResizeIfOversize(image, max, max);
                return ImageSerialization.SaveJpegToBytes(resized, 75L);
            }

            //The format decides as much as the size does. A 1200 x 900 HEIC exceeds nothing, has
            //nothing to straighten, and would therefore come back untouched — that is exactly how
            //93 iPhone photos ended up stored as HEIC under a `.jpg` name, invisible in every
            //browser. Anything that is not a web format is re-encoded, unconditionally.
            if (modified || !EstUnFormatDuWeb(bytes))
                return ImageSerialization.SaveJpegToBytes(image, 75L);

            return bytes;
        }

        public static ImageRgba32 CleanImage(this ImageRgba32 image, int max, out bool modified)
        {
            modified = ExifUtils.RotateFlipIfRequired(image);
            modified |= ExifUtils.Strip(image);

            if (image.Width > max || image.Height > max)
            {
                var modifiedImage = ImageResizer.ResizeIfOversize(image, max, max);
                modified = true;
                return modifiedImage;
            }

            return image;
        }

        public static ImageRgba32 GenerateDiffImage(ImageRgba32 image1, ImageRgba32 image2)
        {
            if (image1.Width != image2.Width || image1.Height != image2.Height)
                throw new BadRequestException("Images sizes must match");

            var result = new ImageRgba32(image1.Width, image1.Height);
            for (int y = 0; y < image1.Height; ++y)
            {
                for (int x = 0; x < image1.Width; ++x)
                    result[x, y] = image1[x, y].Diff(image2[x, y]);
            }
            return result;
        }

        public static bool IsAllColorRow(ImageRgba32 image, int n)
        {
            for (int i = 0; i < image.Width; ++i)
            {
                if (!image[i, n].CloseToWhite())
                    return false;
            }
            return true;
        }

        public static bool IsAllColorColumn(ImageRgba32 image, int n)
        {
            for (int i = 0; i < image.Height; ++i)
            {
                if (!image[n, i].CloseToWhite())
                    return false;
            }
            return true;
        }

        public static bool CloseToWhite(this Rgba32 c, byte threshold = 230)
        {
            var g = c.ToGrayscale();
            return g >= threshold;
        }

        public static byte ToGrayscale(this Rgba32 c)
        {
            return (byte)(0.3 * c.R + 0.59 * c.G + 0.11 * c.B);
        }

        public static int DiffGrayscale(this Rgba32 c1, Rgba32 c2)
        {
            var g1 = c1.ToGrayscale();
            var g2 = c2.ToGrayscale();
            return Math.Abs(g1 - g2);
        }

        public static Rgba32 Diff(this Rgba32 c1, Rgba32 c2)
        {
            var r = (byte)Math.Abs(c1.R - c2.R);
            var g = (byte)Math.Abs(c1.G - c2.G);
            var b = (byte)Math.Abs(c1.B - c2.B);
            return new Rgba32(r, g, b);
        }

        /// <summary>
        /// Returns the image, or `null` if the bytes are not a readable image.
        ///
        /// ⚠️ **The `null` is silent, and that is what cost fifteen months.** On Linux this method
        /// returned `null` for *every* image, and `cl2j.Medias.MediaService` ignored that `null`:
        /// no thumbnail was ever produced, without one line of log. A caller that can do nothing
        /// with a `null` must throw or log, never carry on.
        /// </summary>
        /// <summary>
        /// Returns true if the bytes carry an image in a recognised format, reading only the
        /// header — no full decode, so negligible next to a download.
        ///
        /// It answers a question the calling code must ask before storing anything: "is what the
        /// source just handed me really an image?". On September 3rd 2026, the Appartogo crawler
        /// was storing the LogisQuebec home page under a `.jpg` name — the source redirected its
        /// deleted photos to its home page, and `HttpClient` follows redirects on its own.
        /// </summary>
        public static bool IsImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return false;

            try
            {
                return ISImage.Identify(bytes) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static ImageRgba32? ReadImage(byte[] bytes) => ReadImage(bytes, out _);

        /// <summary>
        /// Reads an image, and says in `failure` why it could not. `null` image with a non-null
        /// `failure`; a read that works leaves `failure` null.
        ///
        /// **Why the reason is handed back rather than logged.** This assembly takes no logger,
        /// and a caller that knows the file name writes a better line than one that only has the
        /// bytes. The overload without the parameter keeps working for callers that do not care.
        ///
        /// **What it cost not to have it.** The parameterless read swallowed every exception to
        /// return `null`. The portal produced no thumbnail at all for fifteen months: callers knew
        /// they had failed, no one knew why, and the cause — a decoder that does not read HEIC —
        /// was only found by sniffing the bytes by hand. The reason therefore names the format
        /// when the signature is one this decoder is known not to read.
        /// </summary>
        public static ImageRgba32? ReadImage(byte[] bytes, out string? failure)
        {
            failure = null;

            if (bytes == null)
            {
                failure = "no bytes";
                return null;
            }

            if (bytes.Length == 0)
            {
                failure = "empty";
                return null;
            }

            try
            {
                return ISImage.Load<Rgba32>(bytes);
            }
            catch (Exception ex)
            {
                var format = NommerUnFormatNonSupporte(bytes);
                failure = format == null
                    ? $"{ex.GetType().Name}: {ex.Message}"
                    : $"{format}, which this decoder does not read ({ex.GetType().Name})";
                return null;
            }
        }

        /// <summary>
        /// Names the format of a file `ImageSharp` cannot read, by reading its signature. Returns
        /// `null` when the format is not recognised. It serves to **explain a refusal**, never to
        /// decide to accept: nothing here decodes anything.
        ///
        /// **Why this method exists.** Client decision, September 4th 2026: no native library will
        /// be added to decode HEIC. ImageMagick does it, but it recognises more than two hundred
        /// formats, some of them languages able to read and write files, and its vulnerability
        /// history — the ImageTragick family — has no fully reliable defence. For 95 photos, the
        /// game is not worth the candle. The conversion will happen **in the browser**, before the
        /// upload.
        ///
        /// But a client is still a client: an old browser, a direct API call or a failed
        /// conversion will send HEIC anyway. The server must therefore refuse, and the refusal must
        /// be **readable** — "HEIC format not accepted" rather than a generic error nobody can
        /// interpret. Fifteen months of missing thumbnails were born of a mute failure; we are not
        /// doing that again.
        ///
        /// Twelve bytes are enough: four of size, the `ftyp` tag, then the brand. It is managed
        /// code, with no dependency, and it covers the 124 files the portal refused.
        /// </summary>
        public static string? NommerUnFormatNonSupporte(byte[] bytes)
        {
            if (bytes is null || bytes.Length < 12)
                return null;

            if (bytes[4] != (byte)'f' || bytes[5] != (byte)'t' || bytes[6] != (byte)'y' || bytes[7] != (byte)'p')
                return null;

            var marque = System.Text.Encoding.ASCII.GetString(bytes, 8, 4);
            return marque switch
            {
                "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "hevm" or "hevs" or "mif1" or "msf1" => "HEIC",
                "avif" or "avis" => "AVIF",
                "qt  " => "video QuickTime",
                "mp42" or "mp41" or "isom" or "iso2" => "video MP4",
                _ => null,
            };
        }

        /// <summary>
        /// True if the format is rendered by browsers. A HEIC decodes correctly but displays in
        /// neither Chrome nor Firefox: storing it as-is produces a listing with no image, which is
        /// exactly the failure seen on 93 portal files. Anything not in this list must come back
        /// re-encoded.
        /// </summary>
        public static bool EstUnFormatDuWeb(byte[] bytes)
        {
            try
            {
                var format = ISImage.DetectFormat(bytes);
                return format is not null
                    && (format.Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
                        || format.Name.Equals("PNG", StringComparison.OrdinalIgnoreCase)
                        || format.Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase)
                        || format.Name.Equals("GIF", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
