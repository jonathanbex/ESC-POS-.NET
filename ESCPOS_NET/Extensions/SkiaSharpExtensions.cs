using System;
using SkiaSharp;

namespace ESCPOS_NET.Extensions
{
    public static class SkiaSharpExtensions
    {
        // Stucki error diffusion matrix (divisor 42), offsets relative to the current pixel.
        private static readonly (int Dx, int Dy, int Weight)[] StuckiMatrix =
        {
                                          (1, 0, 8), (2, 0, 4),
            (-2, 1, 2), (-1, 1, 4), (0, 1, 8), (1, 1, 4), (2, 1, 2),
            (-2, 2, 1), (-1, 2, 2), (0, 2, 4), (1, 2, 2), (2, 2, 1),
        };

        public static byte[] ToSingleBitPixelByteArray(this SKBitmap image, bool rasterFormat = true, int? maxWidth = null, int? maxHeight = null, float threshold = 0.5F)
        {
            return image.ToSingleBitPixelByteArray(out _, out _, rasterFormat, maxWidth, maxHeight, threshold);
        }

        /// <summary>
        /// Resizes (preserving aspect ratio) the image to fit within maxWidth/maxHeight, flattens it onto a white background,
        /// converts it to grayscale and dithers it to 1 bit per pixel. Returns the packed rows (MSB first, black = 1),
        /// along with the final width and height of the processed image.
        /// </summary>
        public static byte[] ToSingleBitPixelByteArray(this SKBitmap image, out int width, out int height, bool rasterFormat = true, int? maxWidth = null, int? maxHeight = null, float threshold = 0.5F)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            (width, height) = CalculateSize(image.Width, image.Height, maxWidth, maxHeight);
            var luminance = Flatten(image, width, height);
            var isBlack = Dither(luminance, width, height, threshold * 255F);

            if (!rasterFormat)
            {
                // Rotate 90 degrees clockwise followed by a horizontal flip is a transpose.
                var transposed = new bool[isBlack.Length];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        transposed[(x * height) + y] = isBlack[(y * width) + x];
                    }
                }

                isBlack = transposed;
                (width, height) = (height, width);
            }

            return Pack(isBlack, width, height);
        }

        private static (int Width, int Height) CalculateSize(int sourceWidth, int sourceHeight, int? maxWidth, int? maxHeight)
        {
            if (!maxWidth.HasValue && !maxHeight.HasValue)
            {
                return (sourceWidth, sourceHeight);
            }

            // Scale (up or down) so the image fits within the bounds while keeping its aspect ratio.
            float percentWidth = (maxWidth ?? int.MaxValue) / (float)sourceWidth;
            float percentHeight = (maxHeight ?? int.MaxValue) / (float)sourceHeight;
            if (percentHeight < percentWidth)
            {
                return (Math.Max(1, (int)Math.Round(sourceWidth * percentHeight)), maxHeight.Value);
            }

            return (maxWidth.Value, Math.Max(1, (int)Math.Round(sourceHeight * percentWidth)));
        }

        private static float[] Flatten(SKBitmap source, int width, int height)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var target = new SKBitmap(info))
            using (var canvas = new SKCanvas(target))
            using (var sourceImage = SKImage.FromBitmap(source))
            {
                canvas.Clear(SKColors.White);
                var sampling = width == source.Width && height == source.Height
                    ? SKSamplingOptions.Default
                    : new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(sourceImage, SKRect.Create(width, height), sampling);
                canvas.Flush();

                var pixels = target.GetPixelSpan();
                var rowBytes = target.RowBytes;
                var luminance = new float[width * height];
                for (int y = 0; y < height; y++)
                {
                    var rowStart = y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        var p = rowStart + (x * 4);

                        // BT.709 luma, matching the previous ImageSharp Grayscale() behaviour.
                        luminance[(y * width) + x] = (0.2126F * pixels[p]) + (0.7152F * pixels[p + 1]) + (0.0722F * pixels[p + 2]);
                    }
                }

                return luminance;
            }
        }

        private static bool[] Dither(float[] luminance, int width, int height, float threshold)
        {
            var isBlack = new bool[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var index = (y * width) + x;
                    var oldValue = luminance[index];
                    var black = oldValue < threshold;
                    isBlack[index] = black;

                    var error = oldValue - (black ? 0F : 255F);
                    foreach (var (dx, dy, weight) in StuckiMatrix)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        luminance[(ny * width) + nx] += error * weight / 42F;
                    }
                }
            }

            return isBlack;
        }

        private static byte[] Pack(bool[] isBlack, int width, int height)
        {
            var bytesPerRow = (width + 7 & -8) / 8;
            var result = new byte[bytesPerRow * height];
            for (int y = 0; y < height; y++)
            {
                var rowStartPosition = y * bytesPerRow;
                for (int x = 0; x < width; x++)
                {
                    if (isBlack[(y * width) + x])
                    {
                        result[rowStartPosition + (x / 8)] |= (byte)(0x01 << (7 - (x % 8)));
                    }
                }
            }

            return result;
        }
    }
}
