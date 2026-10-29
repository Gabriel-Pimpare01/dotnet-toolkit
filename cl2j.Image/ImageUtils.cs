using cl2j.Tooling.Exceptions;
using SkiaSharp;

namespace cl2j.Image
{
    /// <summary>
    /// Image utilities, built on SkiaSharp since 6.0.0.
    ///
    /// **Ported from System.Drawing on September 3rd 2026, then from ImageSharp in October 2026.**
    ///
    /// The first port was not modernisation: `System.Drawing.Common` throws
    /// `PlatformNotSupportedException` on anything but Windows since .NET 6, and the consuming
    /// portal, which runs on Linux, had produced **no thumbnail at all** in fifteen months —
    /// 5,400 images, without one line of error, because the entry points swallowed the exception.
    ///
    /// The second port left ImageSharp when five vulnerabilities were published against 3.1.12 on
    /// October 7th 2026, fixed only in 4.x, which requires a licence key to build. SkiaSharp
    /// (MIT, Microsoft) was chosen after a measurement on 147 real listing photos: same output
    /// dimensions, a similar weight, no visible difference, and the same duplicate verdicts.
    ///
    /// **Ownership convention, unchanged:** the images returned belong to the caller, who must
    /// dispose them. Some methods return the instance they received when there is nothing to do —
    /// `Resize` at equal size, `Crop` with no border, `CropCenter` on an already smaller image.
    /// Do not dispose a result without knowing whether it is the original.
    /// </summary>
    public static class ImageUtils
    {
        public class OptimizeResult
        {
            public int Width { get; set; }
            public int Height { get; set; }

            public bool Modified { get; set; }
        }

        public static OptimizeResult? OptimizeImage(ref byte[] bytes, int max = 1280, long quality = 75L)
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

                    return new OptimizeResult
                    {
                        Modified = true,
                        Width = resized.Width,
                        Height = resized.Height
                    };
                }

                //Same reason as in CleanImage: a format browsers do not render must come back
                //re-encoded, even when nothing else calls for it.
                if (modified || !IsWebFormat(bytes))
                {
                    bytes = ImageSerialization.SaveJpegToBytes(image, quality);
                    modified = true;
                }

                return new OptimizeResult
                {
                    Modified = modified,
                    Width = image.Width,
                    Height = image.Height
                };
            }

            return null;
        }

        public static RasterImage CreateThumbnailCropped(byte[] bytes, int w, int h)
        {
            using var image = ReadImage(bytes) ?? throw new ValidationException("Invalid image");
            return CreateThumbnailCropped(image, w, h);
        }

        public static RasterImage CreateThumbnailCropped(RasterImage image, int w, int h)
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

        /// <summary>The image scaled to fit `w` x `h`, centred on a background of the given colour.</summary>
        public static RasterImage CreateThumbnail(RasterImage image, int w, int h, RgbColor backgroundColor)
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

            var resized = ImageResizer.Resize(image, Math.Max(1, newW), Math.Max(1, newH));
            try
            {
                var target = new SKBitmap(RasterImage.Info(w, h));
                using (var canvas = new SKCanvas(target))
                using (var picture = SKImage.FromBitmap(resized.Bitmap))
                {
                    canvas.Clear(new SKColor(backgroundColor.R, backgroundColor.G, backgroundColor.B));
                    //Drawn at its own size: no resampling happens, the sampling options only satisfy the API.
                    canvas.DrawImage(picture, x, y, new SKSamplingOptions(SKFilterMode.Nearest));
                }
                return new RasterImage(target);
            }
            finally
            {
                if (!ReferenceEquals(resized, image))
                    resized.Dispose();
            }
        }

        public static RasterImage CreateThumbnailWithRatio(RasterImage image, int w)
        {
            var ratio = Math.Round((decimal)image.Width / image.Height, 2);
            int h = (int)Math.Round(w / ratio, 0);

            return ImageResizer.Resize(image, w, Math.Max(1, h));
        }

        /// <summary>The image without its near-white border rows and columns, or the same instance when there is none.</summary>
        public static RasterImage Crop(RasterImage bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            var grey = Pixels.Grey(bmp);

            int topmost = 0;
            for (int row = 0; row < h; ++row)
            {
                if (Pixels.IsWhiteRow(grey, w, row))
                    topmost = row + 1;
                else
                    break;
            }

            int bottommost = 0;
            for (int row = h - 1; row >= 0; --row)
            {
                if (Pixels.IsWhiteRow(grey, w, row))
                    bottommost = row;
                else
                    break;
            }

            int leftmost = 0;
            for (int col = 0; col < w; ++col)
            {
                if (Pixels.IsWhiteColumn(grey, w, h, col))
                    leftmost = col + 1;
                else
                    break;
            }

            int rightmost = 0;
            for (int col = w - 1; col >= 0; --col)
            {
                if (Pixels.IsWhiteColumn(grey, w, h, col))
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

            //An entirely white image gives crossed bounds: the System.Drawing version then threw a
            //BadRequestException from Graphics.DrawImage. We keep the same signal, but throw up
            //front rather than waiting for the library.
            if (croppedWidth <= 0 || croppedHeight <= 0 || leftmost + croppedWidth > w || topmost + croppedHeight > h)
                throw new BadRequestException($"Values are topmost={topmost} btm={bottommost} left={leftmost} right={rightmost} croppedWidth={croppedWidth} croppedHeight={croppedHeight}");

            return Extract(bmp, leftmost, topmost, croppedWidth, croppedHeight);
        }

        public static RasterImage CropCenter(RasterImage bmp, int w, int h, out bool modified)
        {
            modified = false;
            if (bmp.Width < w || bmp.Height < h)
                return bmp;
            if (bmp.Width == w && bmp.Height == h)
                return bmp;

            int x = (bmp.Width - w) / 2;
            int y = (bmp.Height - h) / 2;

            modified = true;

            return Extract(bmp, x, y, w, h);
        }

        private static RasterImage Extract(RasterImage source, int x, int y, int w, int h)
        {
            using var subset = new SKBitmap();
            if (!source.Bitmap.ExtractSubset(subset, SKRectI.Create(x, y, w, h)))
                throw new BadRequestException($"Cannot extract {w} x {h} at ({x}, {y}) from a {source.Width} x {source.Height} image");
            //A subset shares the pixels of its source: copy it so the result outlives the source.
            return new RasterImage(subset.Copy(), source.Origin, source.HasMetadata);
        }

        public class ImageCompareSettings
        {
            public int PixelDifferenceTolerance { get; set; } = 19;
            public int CompareDifferenceMax { get; set; } = 30;
            public double PercentEqualsMin { get; set; } = 0.6;
        }

        /// <summary>
        /// True when two images show the same photo, whatever their size: borders cropped, the
        /// larger scaled down to the smaller, then compared in grey levels. This is what decides
        /// whether two listings from different sources are the same flat.
        /// </summary>
        public static bool AreImagesIdentical(RasterImage image1, RasterImage image2, ImageCompareSettings settings)
        {
            if (image1 == null || image2 == null)
                return false;

            RasterImage? newImage = null;
            RasterImage? newImageCrawler = null;
            try
            {
                //Crop images (remove white lines/columns) surrounding
                newImage = Crop(image2);
                var imageRatio = (double)newImage.Width / newImage.Height;

                newImageCrawler = Crop(image1);
                var imageCrawlerRatio = (double)newImageCrawler.Width / newImageCrawler.Height;

                //Ratio is different --> Images are differents
                if (Math.Abs(imageRatio - imageCrawlerRatio) > 0.01)
                    return false;

                //Resize images if required to have the same size for the comparaison
                if (newImage.Width > newImageCrawler.Width)
                    newImage = Replace(newImage, image2, ImageResizer.Resize(newImage, newImageCrawler.Width, newImageCrawler.Height));
                else
                    newImageCrawler = Replace(newImageCrawler, image1, ImageResizer.Resize(newImageCrawler, newImage.Width, newImage.Height));

                //Compare
                var res = Compare(newImage, newImageCrawler, out var diff);
                if (res)
                {
                    var percentEquals = Equals(newImage, newImageCrawler, settings.PixelDifferenceTolerance);
                    if (diff <= settings.CompareDifferenceMax && percentEquals >= settings.PercentEqualsMin)
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
                Release(newImage, image1, image2);
                Release(newImageCrawler, image1, image2);
            }
        }

        private static RasterImage Replace(RasterImage previous, RasterImage original, RasterImage next)
        {
            if (!ReferenceEquals(previous, original) && !ReferenceEquals(previous, next))
                previous.Dispose();
            return next;
        }

        private static void Release(RasterImage? image, RasterImage original1, RasterImage original2)
        {
            if (image is not null && !ReferenceEquals(image, original1) && !ReferenceEquals(image, original2))
                image.Dispose();
        }

        /// <summary>The mean grey-level difference between two images of the same size, in `diff`. False when the sizes differ.</summary>
        public static bool Compare(RasterImage image1, RasterImage image2, out int diff)
        {
            diff = 0;

            if (image1.Width != image2.Width || image1.Height != image2.Height)
                return false;

            var a = Pixels.Grey(image1);
            var b = Pixels.Grey(image2);
            long total = 0;
            for (int i = 0; i < a.Length; ++i)
                total += Math.Abs(a[i] - b[i]);

            diff = (int)(total / a.Length);

            return true;
        }

        /// <summary>The share of pixels whose grey levels differ by `pixelDiffMax` at most. 0 when the sizes differ.</summary>
        public static double Equals(RasterImage image1, RasterImage image2, int pixelDiffMax)
        {
            if (image1.Width != image2.Width || image1.Height != image2.Height)
                return 0;

            var a = Pixels.Grey(image1);
            var b = Pixels.Grey(image2);
            int nbPixelEquals = 0;
            for (int i = 0; i < a.Length; ++i)
            {
                if (Math.Abs(a[i] - b[i]) <= pixelDiffMax)
                    ++nbPixelEquals;
            }

            return (double)nbPixelEquals / a.Length;
        }

        public static byte[]? CleanImage(byte[] bytes, int max = 1280)
        {
            if (bytes == null)
                return bytes;

            //No fallback, and that is the heart of the September 2026 fix. The System.Drawing
            //version chained two attempts that both ended in System.Drawing, then returned the
            //original bytes without saying anything: on Linux, every image came back as-is, not
            //resized — 2.4 MB measured on one portal photo.
            //
            //The exception is allowed to propagate. An unreadable image is an error the caller
            //must see, not a silence to store.
            using var image = ReadImage(bytes, out var failure)
                ?? throw new ValidationException($"Invalid image: {failure}.");

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
            if (modified || !IsWebFormat(bytes))
                return ImageSerialization.SaveJpegToBytes(image, 75L);

            return bytes;
        }

        public static RasterImage CleanImage(this RasterImage image, int max, out bool modified)
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

        /// <summary>An image of the per-channel differences between two images of the same size.</summary>
        public static RasterImage GenerateDiffImage(RasterImage image1, RasterImage image2)
        {
            if (image1.Width != image2.Width || image1.Height != image2.Height)
                throw new BadRequestException("Images sizes must match");

            var result = new RasterImage(image1.Width, image1.Height);
            var a = image1.Bitmap.GetPixelSpan();
            var b = image2.Bitmap.GetPixelSpan();
            var r = result.Bitmap.GetPixelSpan();
            for (int i = 0; i < r.Length; i += 4)
            {
                r[i] = (byte)Math.Abs(a[i] - b[i]);
                r[i + 1] = (byte)Math.Abs(a[i + 1] - b[i + 1]);
                r[i + 2] = (byte)Math.Abs(a[i + 2] - b[i + 2]);
                r[i + 3] = 255;
            }
            return result;
        }

        /// <summary>
        /// Returns true if the bytes carry an image in a recognised format, reading only the
        /// header — no full decode, so negligible next to a download.
        ///
        /// It answers a question the calling code must ask before storing anything: "is what the
        /// source just handed me really an image?". On September 3rd 2026, a consuming crawler
        /// was storing a source's home page under a `.jpg` name — the source redirected its
        /// deleted photos to its home page, and `HttpClient` follows redirects on its own.
        /// </summary>
        public static bool IsImage(byte[] bytes) => Identify(bytes) is not null;

        /// <summary>
        /// What the header of an image file declares — dimensions as stored, frame count, format —
        /// without decoding the pixels, so safe to call before deciding whether to decode at all.
        /// `null` when the bytes are not an image this library reads.
        /// </summary>
        public static ImageHeader? Identify(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(bytes));
                if (codec == null)
                    return null;
                return new ImageHeader(codec.Info.Width, codec.Info.Height, Math.Max(1, codec.FrameCount), FormatName(codec.EncodedFormat));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>How `ReadImage` turns the file into pixels. The defaults read it as every version before 6.4.0 did.</summary>
        public class ReadSettings
        {
            /// <summary>
            /// Converts the pixels to sRGB using the ICC profile the file carries, so they show the
            /// colours of the photo once encoded again — the encoders of this library write no
            /// profile. Without it, a Display P3 photo, what a phone shoots, comes out duller: its
            /// numbers are kept and then shown as sRGB ones. A file without a profile is sRGB
            /// already and reads the same either way.
            ///
            /// **Off by default, on purpose.** Turning it on changes the pixels of every wide-gamut
            /// photo, and with them the thumbnails and the grey levels `AreImagesIdentical` compares:
            /// an image stored before and the same image read again would no longer match. A
            /// consumer opts in when the colours matter more than that continuity.
            /// </summary>
            public bool ConvertToSrgb { get; set; }
        }

        private static readonly ReadSettings AsStored = new();

        public static RasterImage? ReadImage(byte[] bytes) => ReadImage(bytes, out _);

        public static RasterImage? ReadImage(byte[] bytes, out string? failure) => ReadImage(bytes, AsStored, out failure);

        /// <summary>
        /// Reads an image, and says in `failure` why it could not. `null` image with a non-null
        /// `failure`; a read that works leaves `failure` null.
        ///
        /// The EXIF orientation is not applied: it stays pending on the image until
        /// `ExifUtils.RotateFlipIfRequired`, so `Width` and `Height` are the stored ones.
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
        public static RasterImage? ReadImage(byte[] bytes, ReadSettings settings, out string? failure)
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

            var unsupported = NameUnsupportedFormat(bytes);
            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(bytes), out var result);
                if (codec == null)
                {
                    failure = unsupported == null
                        ? $"unrecognised image format ({result})"
                        : $"{unsupported}, which this decoder does not read";
                    return null;
                }

                var bitmap = new SKBitmap(RasterImage.Info(codec.Info.Width, codec.Info.Height));
                //Asking for sRGB makes the codec convert through the file's profile. The bitmap itself
                //stays untagged, like every other pixel buffer of this library: what it holds is now sRGB,
                //which is how an untagged buffer is read everywhere downstream.
                var target = settings.ConvertToSrgb ? bitmap.Info.WithColorSpace(SKColorSpace.CreateSrgb()) : bitmap.Info;
                var decoded = codec.GetPixels(target, bitmap.GetPixels());
                //A truncated file decodes what it has, the rest filled in — what browsers show.
                if (decoded != SKCodecResult.Success && decoded != SKCodecResult.IncompleteInput)
                {
                    bitmap.Dispose();
                    failure = $"decoding failed ({decoded})";
                    return null;
                }

                return new RasterImage(bitmap, codec.EncodedOrigin, MetadataSniffer.HasMetadata(bytes));
            }
            catch (Exception ex)
            {
                failure = unsupported == null
                    ? $"{ex.GetType().Name}: {ex.Message}"
                    : $"{unsupported}, which this decoder does not read ({ex.GetType().Name})";
                return null;
            }
        }

        /// <summary>
        /// Names the format of a file this library cannot read, by reading its signature. Returns
        /// `null` when the format is not recognised. It serves to **explain a refusal**, never to
        /// decide to accept: nothing here decodes anything.
        ///
        /// **Why this method exists.** Decision of September 4th 2026: no native library is added
        /// to decode HEIC. ImageMagick does it, but it recognises more than two hundred formats,
        /// some of them languages able to read and write files, and its vulnerability history —
        /// the ImageTragick family — has no fully reliable defence. The conversion happens **in the
        /// browser**, before the upload.
        ///
        /// But a client is still a client: an old browser, a direct API call or a failed
        /// conversion will send HEIC anyway. The server must therefore refuse, and the refusal must
        /// be **readable** — "HEIC format not accepted" rather than a generic error nobody can
        /// interpret.
        ///
        /// TIFF joined the list with 6.0.0: ImageSharp read it, SkiaSharp does not.
        ///
        /// Twelve bytes are enough: four of size, the `ftyp` tag, then the brand. It is managed
        /// code, with no dependency.
        /// </summary>
        public static string? NameUnsupportedFormat(byte[] bytes)
        {
            if (bytes is null || bytes.Length < 12)
                return null;

            var tiffLittleEndian = bytes[0] == (byte)'I' && bytes[1] == (byte)'I' && bytes[3] == 0 && (bytes[2] == 42 || bytes[2] == 43);
            var tiffBigEndian = bytes[0] == (byte)'M' && bytes[1] == (byte)'M' && bytes[2] == 0 && (bytes[3] == 42 || bytes[3] == 43);
            if (tiffLittleEndian || tiffBigEndian)
                return "TIFF";

            if (bytes[4] != (byte)'f' || bytes[5] != (byte)'t' || bytes[6] != (byte)'y' || bytes[7] != (byte)'p')
                return null;

            var brand = System.Text.Encoding.ASCII.GetString(bytes, 8, 4);
            return brand switch
            {
                "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" or "hevm" or "hevs" or "mif1" or "msf1" => "HEIC",
                "avif" or "avis" => "AVIF",
                "qt  " => "video QuickTime",
                "mp42" or "mp41" or "isom" or "iso2" => "video MP4",
                _ => null,
            };
        }

        /// <summary>
        /// True if the format is rendered by browsers. A HEIC decodes correctly on some systems but
        /// displays in neither Chrome nor Firefox: storing it as-is produces a listing with no
        /// image, which is exactly the failure seen on 93 portal files. Anything not in this list
        /// must come back re-encoded.
        /// </summary>
        public static bool IsWebFormat(byte[] bytes)
        {
            var format = Identify(bytes)?.Format;
            return format is "JPEG" or "PNG" or "WEBP" or "GIF";
        }

        private static string FormatName(SKEncodedImageFormat format) => format.ToString().ToUpperInvariant();
    }

    /// <summary>Grey-level helpers shared by the comparisons. Weights of the historical implementation, kept so verdicts do not move.</summary>
    internal static class Pixels
    {
        private const byte WhiteThreshold = 230;

        public static byte[] Grey(RasterImage image)
        {
            var span = image.Bitmap.GetPixelSpan();
            var grey = new byte[image.Width * image.Height];
            for (int i = 0, p = 0; i < grey.Length; ++i, p += 4)
                grey[i] = (byte)(0.3 * span[p] + 0.59 * span[p + 1] + 0.11 * span[p + 2]);
            return grey;
        }

        public static bool IsWhiteRow(byte[] grey, int width, int row)
        {
            var start = row * width;
            for (int x = 0; x < width; ++x)
            {
                if (grey[start + x] < WhiteThreshold)
                    return false;
            }
            return true;
        }

        public static bool IsWhiteColumn(byte[] grey, int width, int height, int column)
        {
            for (int y = 0; y < height; ++y)
            {
                if (grey[y * width + column] < WhiteThreshold)
                    return false;
            }
            return true;
        }
    }
}
