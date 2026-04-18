using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GPUStitch.Core
{
    public sealed class StitchParameterRecommendation
    {
        public int OverlapPixels { get; set; }
        public int BlendWidth { get; set; }
        public float Confidence { get; set; }
        public int EvaluatedPairCount { get; set; }
    }

    public static class StitchParameterAdvisor
    {
        public static StitchParameterRecommendation Recommend(IReadOnlyList<GpuImage> images)
        {
            if (images == null || images.Count == 0)
                return new StitchParameterRecommendation();

            int minWidth = images[0].Width;
            int minHeight = images[0].Height;

            for (int i = 1; i < images.Count; i++)
            {
                minWidth = Math.Min(minWidth, images[i].Width);
                minHeight = Math.Min(minHeight, images[i].Height);
            }

            int fallbackOverlap = Clamp(minWidth / 6, 48, Math.Max(64, minWidth / 2));
            int fallbackBlend = Clamp(fallbackOverlap / 3, 16, Math.Max(24, fallbackOverlap - 4));

            if (images.Count < 2)
            {
                return new StitchParameterRecommendation
                {
                    OverlapPixels = fallbackOverlap,
                    BlendWidth = fallbackBlend,
                    Confidence = 0,
                    EvaluatedPairCount = 0,
                };
            }

            var overlaps = new List<int>();
            float scoreSum = 0;
            int scoreCount = 0;

            for (int i = 1; i < images.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(images[i - 1].FilePath) ||
                    string.IsNullOrWhiteSpace(images[i].FilePath) ||
                    !File.Exists(images[i - 1].FilePath) ||
                    !File.Exists(images[i].FilePath))
                {
                    continue;
                }

                var left = LoadPreview(images[i - 1].FilePath, 512);
                var right = LoadPreview(images[i].FilePath, 512);
                if (left.Width < 32 || right.Width < 32 || left.Height < 32 || right.Height < 32)
                    continue;

                var estimate = EstimatePairOverlap(
                    left,
                    right,
                    Math.Min(images[i - 1].Width, images[i].Width));

                if (estimate.OverlapPixels > 0)
                {
                    overlaps.Add(estimate.OverlapPixels);
                    scoreSum += estimate.Score;
                    scoreCount++;
                }
            }

            if (overlaps.Count == 0)
            {
                return new StitchParameterRecommendation
                {
                    OverlapPixels = fallbackOverlap,
                    BlendWidth = fallbackBlend,
                    Confidence = 0,
                    EvaluatedPairCount = 0,
                };
            }

            overlaps.Sort();
            int medianOverlap = overlaps[overlaps.Count / 2];
            int recommendedOverlap = Clamp(medianOverlap, 24, Math.Max(64, minWidth / 2));
            int recommendedBlend = Clamp(
                recommendedOverlap / 3,
                12,
                Math.Max(20, recommendedOverlap - 4));

            return new StitchParameterRecommendation
            {
                OverlapPixels = recommendedOverlap,
                BlendWidth = recommendedBlend,
                Confidence = scoreCount > 0 ? scoreSum / scoreCount : 0,
                EvaluatedPairCount = scoreCount,
            };
        }

        private static PairEstimate EstimatePairOverlap(PreviewImage left, PreviewImage right, int sourceMinWidth)
        {
            int previewMinWidth = Math.Min(left.Width, right.Width);
            int minOverlap = Clamp(previewMinWidth / 12, 16, previewMinWidth / 3);
            int maxOverlap = Clamp(previewMinWidth / 3, minOverlap + 8, previewMinWidth / 2);
            int overlapStep = Math.Max(4, previewMinWidth / 48);
            int maxShiftY = Clamp(Math.Min(left.Height, right.Height) / 40, 2, 12);

            float bestScore = float.NegativeInfinity;
            int bestOverlap = minOverlap;

            for (int overlap = minOverlap; overlap <= maxOverlap; overlap += overlapStep)
            {
                for (int shiftY = -maxShiftY; shiftY <= maxShiftY; shiftY++)
                {
                    float score = ComputeGradientCorrelation(left, right, overlap, shiftY);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestOverlap = overlap;
                    }
                }
            }

            float scale = sourceMinWidth / (float)previewMinWidth;
            return new PairEstimate
            {
                OverlapPixels = (int)Math.Round(bestOverlap * scale),
                Score = bestScore,
            };
        }

        private static float ComputeGradientCorrelation(
            PreviewImage left,
            PreviewImage right,
            int overlapWidth,
            int shiftY)
        {
            int leftStartX = left.Width - overlapWidth;
            int yStart = Math.Max(1, -shiftY + 1);
            int yEnd = Math.Min(left.Height - 1, right.Height - shiftY - 1);

            float sumDot = 0;
            float sumLeft = 0;
            float sumRight = 0;
            int sampleCount = 0;

            for (int y = yStart; y < yEnd; y += 2)
            {
                for (int x = 1; x < overlapWidth - 1; x += 2)
                {
                    int leftX = leftStartX + x;
                    int rightX = x;
                    int rightY = y + shiftY;

                    float leftGx = left[leftX + 1, y] - left[leftX - 1, y];
                    float leftGy = left[leftX, y + 1] - left[leftX, y - 1];
                    float rightGx = right[rightX + 1, rightY] - right[rightX - 1, rightY];
                    float rightGy = right[rightX, rightY + 1] - right[rightX, rightY - 1];

                    float leftEnergy = (leftGx * leftGx) + (leftGy * leftGy);
                    float rightEnergy = (rightGx * rightGx) + (rightGy * rightGy);

                    if (leftEnergy < 25f || rightEnergy < 25f)
                        continue;

                    sumDot += (leftGx * rightGx) + (leftGy * rightGy);
                    sumLeft += leftEnergy;
                    sumRight += rightEnergy;
                    sampleCount++;
                }
            }

            if (sampleCount < 48 || sumLeft <= 1e-6f || sumRight <= 1e-6f)
                return -1f;

            return (float)(sumDot / Math.Sqrt(sumLeft * sumRight));
        }

        private static PreviewImage LoadPreview(string filePath, int maxDimension)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
            gray.Freeze();

            double scale = Math.Min(
                1.0,
                maxDimension / (double)Math.Max(gray.PixelWidth, gray.PixelHeight));

            BitmapSource source = gray;
            if (scale < 0.999)
            {
                var transform = new ScaleTransform(scale, scale);
                var scaled = new TransformedBitmap(gray, transform);
                scaled.Freeze();
                source = scaled;
            }

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = width;
            byte[] pixels = new byte[height * stride];
            source.CopyPixels(pixels, stride, 0);

            return new PreviewImage(width, height, pixels);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max < min)
                return min;
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private sealed class PairEstimate
        {
            public int OverlapPixels { get; set; }
            public float Score { get; set; }
        }

        private sealed class PreviewImage
        {
            private readonly byte[] _pixels;

            public PreviewImage(int width, int height, byte[] pixels)
            {
                Width = width;
                Height = height;
                _pixels = pixels;
            }

            public int Width { get; }
            public int Height { get; }

            public byte this[int x, int y] => _pixels[(y * Width) + x];
        }
    }
}
