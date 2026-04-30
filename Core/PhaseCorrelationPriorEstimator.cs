using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GPUStitch.Models;

namespace GPUStitch.Core
{
    internal static class PhaseCorrelationPriorEstimator
    {
        private const int MaxPreviewDimension = 768;
        private const int MaxPreviewCacheEntries = 128;

        internal readonly struct PhaseCorrelationResult
        {
            public PhaseCorrelationResult(
                float deltaX,
                float deltaY,
                float response,
                float secondBestResponse,
                float coverage)
            {
                DeltaX = deltaX;
                DeltaY = deltaY;
                Response = response;
                SecondBestResponse = secondBestResponse;
                Coverage = coverage;
            }

            public float DeltaX { get; }
            public float DeltaY { get; }
            public float Response { get; }
            public float SecondBestResponse { get; }
            public float Coverage { get; }
            public float PeakMargin => Response - SecondBestResponse;
        }

        private static readonly object PreviewCacheLock = new object();
        private static readonly Dictionary<string, PreviewImage> PreviewCache =
            new Dictionary<string, PreviewImage>(StringComparer.OrdinalIgnoreCase);

        public static bool TryEstimateDelta(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationAxis axis,
            out float deltaX,
            out float deltaY)
        {
            if (TryEstimateDeltaDetailed(first, second, overlapSize, axis, reverse: false, segmentIndex: -1, segmentCount: 1, out var result))
            {
                deltaX = result.DeltaX;
                deltaY = result.DeltaY;
                return true;
            }

            deltaX = 0.0f;
            deltaY = 0.0f;
            return false;
        }

        public static bool TryEstimateDeltaDetailed(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationAxis axis,
            bool reverse,
            int segmentIndex,
            int segmentCount,
            out PhaseCorrelationResult result)
        {
            result = default;

            if (string.IsNullOrWhiteSpace(first.FilePath) ||
                string.IsNullOrWhiteSpace(second.FilePath) ||
                !File.Exists(first.FilePath) ||
                !File.Exists(second.FilePath))
            {
                return false;
            }

            var firstPreview = GetOrLoadPreview(first.FilePath);
            var secondPreview = GetOrLoadPreview(second.FilePath);
            if (firstPreview.Width < 32 || firstPreview.Height < 32 ||
                secondPreview.Width < 32 || secondPreview.Height < 32)
            {
                return false;
            }

            return axis == RegistrationAxis.Horizontal
                ? TryEstimateHorizontalDeltaDetailed(first, second, overlapSize, firstPreview, secondPreview, reverse, segmentIndex, segmentCount, out result)
                : TryEstimateVerticalDeltaDetailed(first, second, overlapSize, firstPreview, secondPreview, reverse, segmentIndex, segmentCount, out result);
        }

        private static bool TryEstimateHorizontalDeltaDetailed(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            PreviewImage firstPreview,
            PreviewImage secondPreview,
            bool reverse,
            int segmentIndex,
            int segmentCount,
            out PhaseCorrelationResult result)
        {
            result = default;

            int firstOverlapWidth = Clamp((int)Math.Round(overlapSize * (firstPreview.Width / (double)Math.Max(first.Width, 1))), 16, firstPreview.Width);
            int secondOverlapWidth = Clamp((int)Math.Round(overlapSize * (secondPreview.Width / (double)Math.Max(second.Width, 1))), 16, secondPreview.Width);
            int overlapWidth = Math.Min(firstOverlapWidth, secondOverlapWidth);
            int fullOverlapHeight = Math.Min(firstPreview.Height, secondPreview.Height);
            if (overlapWidth < 16 || fullOverlapHeight < 32)
                return false;

            int firstStartX = reverse ? 0 : firstPreview.Width - overlapWidth;
            int secondStartX = reverse ? secondPreview.Width - overlapWidth : 0;
            if (!TryResolveSegmentBounds(fullOverlapHeight, segmentIndex, segmentCount, out int startY, out int segmentHeight))
                return false;

            var firstStrip = ExtractStrip(
                firstPreview,
                firstStartX,
                startY,
                overlapWidth,
                segmentHeight);
            var secondStrip = ExtractStrip(
                secondPreview,
                secondStartX,
                startY,
                overlapWidth,
                segmentHeight);

            if (!TryPhaseCorrelateDetailed(firstStrip, overlapWidth, segmentHeight, secondStrip, out double previewShiftX, out double previewShiftY, out float response, out float secondBestResponse))
                return false;

            float scaleX = (float)((first.Width / (double)Math.Max(firstPreview.Width, 1) +
                                    second.Width / (double)Math.Max(secondPreview.Width, 1)) * 0.5);
            float scaleY = (float)((first.Height / (double)Math.Max(firstPreview.Height, 1) +
                                    second.Height / (double)Math.Max(secondPreview.Height, 1)) * 0.5);

            result = new PhaseCorrelationResult(
                (float)(previewShiftX * scaleX),
                (float)(previewShiftY * scaleY),
                response,
                secondBestResponse,
                segmentHeight / (float)Math.Max(fullOverlapHeight, 1));
            return true;
        }

        private static bool TryEstimateVerticalDeltaDetailed(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            PreviewImage firstPreview,
            PreviewImage secondPreview,
            bool reverse,
            int segmentIndex,
            int segmentCount,
            out PhaseCorrelationResult result)
        {
            result = default;

            int firstOverlapHeight = Clamp((int)Math.Round(overlapSize * (firstPreview.Height / (double)Math.Max(first.Height, 1))), 16, firstPreview.Height);
            int secondOverlapHeight = Clamp((int)Math.Round(overlapSize * (secondPreview.Height / (double)Math.Max(second.Height, 1))), 16, secondPreview.Height);
            int overlapHeight = Math.Min(firstOverlapHeight, secondOverlapHeight);
            int fullOverlapWidth = Math.Min(firstPreview.Width, secondPreview.Width);
            if (overlapHeight < 16 || fullOverlapWidth < 32)
                return false;

            int firstStartY = reverse ? 0 : firstPreview.Height - overlapHeight;
            int secondStartY = reverse ? secondPreview.Height - overlapHeight : 0;
            if (!TryResolveSegmentBounds(fullOverlapWidth, segmentIndex, segmentCount, out int startX, out int segmentWidth))
                return false;

            var firstStrip = ExtractStrip(
                firstPreview,
                startX,
                firstStartY,
                segmentWidth,
                overlapHeight);
            var secondStrip = ExtractStrip(
                secondPreview,
                startX,
                secondStartY,
                segmentWidth,
                overlapHeight);

            if (!TryPhaseCorrelateDetailed(firstStrip, segmentWidth, overlapHeight, secondStrip, out double previewShiftX, out double previewShiftY, out float response, out float secondBestResponse))
                return false;

            float scaleX = (float)((first.Width / (double)Math.Max(firstPreview.Width, 1) +
                                    second.Width / (double)Math.Max(secondPreview.Width, 1)) * 0.5);
            float scaleY = (float)((first.Height / (double)Math.Max(firstPreview.Height, 1) +
                                    second.Height / (double)Math.Max(secondPreview.Height, 1)) * 0.5);

            result = new PhaseCorrelationResult(
                (float)(previewShiftX * scaleX),
                (float)(previewShiftY * scaleY),
                response,
                secondBestResponse,
                segmentWidth / (float)Math.Max(fullOverlapWidth, 1));
            return true;
        }

        private static PreviewImage GetOrLoadPreview(string filePath)
        {
            lock (PreviewCacheLock)
            {
                if (PreviewCache.TryGetValue(filePath, out var cached))
                    return cached;
            }

            var loaded = LoadPreview(filePath);
            lock (PreviewCacheLock)
            {
                if (PreviewCache.TryGetValue(filePath, out var cached))
                    return cached;

                if (PreviewCache.Count >= MaxPreviewCacheEntries)
                    PreviewCache.Clear();

                PreviewCache[filePath] = loaded;
                return loaded;
            }
        }

        private static PreviewImage LoadPreview(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int originalWidth = decoder.Frames[0].PixelWidth;
            int originalHeight = decoder.Frames[0].PixelHeight;
            stream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;

            if (originalWidth > MaxPreviewDimension || originalHeight > MaxPreviewDimension)
            {
                if (originalWidth >= originalHeight)
                    bitmap.DecodePixelWidth = MaxPreviewDimension;
                else
                    bitmap.DecodePixelHeight = MaxPreviewDimension;
            }

            bitmap.EndInit();
            bitmap.Freeze();

            var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
            gray.Freeze();

            int width = gray.PixelWidth;
            int height = gray.PixelHeight;
            int stride = width;
            byte[] pixels = new byte[height * stride];
            gray.CopyPixels(pixels, stride, 0);

            return new PreviewImage(width, height, pixels);
        }

        private static float[] ExtractStrip(PreviewImage image, int startX, int startY, int width, int height)
        {
            var strip = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    strip[(y * width) + x] = image[startX + x, startY + y] / 255.0f;
                }
            }

            return strip;
        }

        private static bool TryPhaseCorrelateDetailed(
            float[] first,
            int width,
            int height,
            float[] second,
            out double shiftX,
            out double shiftY,
            out float response,
            out float secondBestResponse)
        {
            shiftX = 0;
            shiftY = 0;
            response = 0.0f;
            secondBestResponse = 0.0f;

            if (width <= 0 || height <= 0 || first.Length != second.Length)
                return false;

            int fftWidth = NextPowerOfTwo(width);
            int fftHeight = NextPowerOfTwo(height);
            var firstSpectrum = BuildWindowedSpectrum(first, width, height, fftWidth, fftHeight);
            var secondSpectrum = BuildWindowedSpectrum(second, width, height, fftWidth, fftHeight);

            FFT2D(firstSpectrum, fftWidth, fftHeight, inverse: false);
            FFT2D(secondSpectrum, fftWidth, fftHeight, inverse: false);

            var crossPower = new Complex[fftWidth * fftHeight];
            for (int i = 0; i < crossPower.Length; i++)
            {
                Complex value = secondSpectrum[i] * Complex.Conjugate(firstSpectrum[i]);
                double magnitude = value.Magnitude;
                crossPower[i] = magnitude > 1e-9 ? value / magnitude : Complex.Zero;
            }

            FFT2D(crossPower, fftWidth, fftHeight, inverse: true);

            int bestX = 0;
            int bestY = 0;
            double bestMagnitude = double.NegativeInfinity;
            for (int y = 0; y < fftHeight; y++)
            {
                for (int x = 0; x < fftWidth; x++)
                {
                    double magnitude = crossPower[(y * fftWidth) + x].Magnitude;
                    if (magnitude > bestMagnitude)
                    {
                        bestMagnitude = magnitude;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            double secondMagnitude = double.NegativeInfinity;
            for (int y = 0; y < fftHeight; y++)
            {
                for (int x = 0; x < fftWidth; x++)
                {
                    if (IsWithinNeighborhood(x, y, bestX, bestY, fftWidth, fftHeight, radius: 1))
                        continue;

                    double magnitude = crossPower[(y * fftWidth) + x].Magnitude;
                    if (magnitude > secondMagnitude)
                        secondMagnitude = magnitude;
                }
            }

            double refinedX = bestX + ComputeWrappedParabolicOffset(crossPower, fftWidth, fftHeight, bestX, bestY, isXAxis: true);
            double refinedY = bestY + ComputeWrappedParabolicOffset(crossPower, fftWidth, fftHeight, bestX, bestY, isXAxis: false);

            if (refinedX > (fftWidth / 2.0))
                refinedX -= fftWidth;
            if (refinedY > (fftHeight / 2.0))
                refinedY -= fftHeight;

            shiftX = refinedX;
            shiftY = refinedY;
            response = (float)Math.Max(0.0, bestMagnitude);
            secondBestResponse = (float)Math.Max(0.0, secondMagnitude);
            return true;
        }

        private static bool TryResolveSegmentBounds(
            int fullLength,
            int segmentIndex,
            int segmentCount,
            out int start,
            out int length)
        {
            start = 0;
            length = fullLength;

            if (fullLength < 32)
                return false;

            if (segmentCount <= 1 || segmentIndex < 0)
                return true;

            if (segmentIndex >= segmentCount)
                return false;

            int baseStart = (int)Math.Floor((segmentIndex / (double)segmentCount) * fullLength);
            int baseEnd = (int)Math.Ceiling(((segmentIndex + 1) / (double)segmentCount) * fullLength);
            int segmentLength = Math.Max(1, baseEnd - baseStart);
            int padding = Math.Max(4, segmentLength / 6);

            start = Math.Max(0, baseStart - padding);
            int end = Math.Min(fullLength, baseEnd + padding);
            length = end - start;
            return length >= 24;
        }

        private static bool IsWithinNeighborhood(
            int x,
            int y,
            int centerX,
            int centerY,
            int width,
            int height,
            int radius)
        {
            int dx = WrappedDistance(x, centerX, width);
            int dy = WrappedDistance(y, centerY, height);
            return Math.Abs(dx) <= radius && Math.Abs(dy) <= radius;
        }

        private static int WrappedDistance(int value, int center, int size)
        {
            int diff = value - center;
            int half = size / 2;
            if (diff > half)
                diff -= size;
            else if (diff < -half)
                diff += size;
            return diff;
        }

        private static double ComputeWrappedParabolicOffset(
            Complex[] surface,
            int width,
            int height,
            int bestX,
            int bestY,
            bool isXAxis)
        {
            if (width <= 1 || height <= 1)
                return 0.0;

            if (isXAxis)
            {
                double left = surface[(bestY * width) + WrapIndex(bestX - 1, width)].Magnitude;
                double center = surface[(bestY * width) + bestX].Magnitude;
                double right = surface[(bestY * width) + WrapIndex(bestX + 1, width)].Magnitude;
                return ComputeParabolicOffset(left, center, right);
            }

            double top = surface[(WrapIndex(bestY - 1, height) * width) + bestX].Magnitude;
            double centerY = surface[(bestY * width) + bestX].Magnitude;
            double bottom = surface[(WrapIndex(bestY + 1, height) * width) + bestX].Magnitude;
            return ComputeParabolicOffset(top, centerY, bottom);
        }

        private static double ComputeParabolicOffset(double negative, double center, double positive)
        {
            double denominator = negative - (2.0 * center) + positive;
            if (Math.Abs(denominator) < 1e-9)
                return 0.0;

            double offset = 0.5 * (negative - positive) / denominator;
            if (double.IsNaN(offset) || double.IsInfinity(offset))
                return 0.0;

            if (offset < -0.5)
                return -0.5;
            if (offset > 0.5)
                return 0.5;
            return offset;
        }

        private static int WrapIndex(int value, int size)
        {
            int wrapped = value % size;
            return wrapped < 0 ? wrapped + size : wrapped;
        }

        private static Complex[] BuildWindowedSpectrum(
            float[] source,
            int width,
            int height,
            int fftWidth,
            int fftHeight)
        {
            double mean = 0.0;
            for (int i = 0; i < source.Length; i++)
            {
                mean += source[i];
            }

            mean /= Math.Max(source.Length, 1);
            var data = new Complex[fftWidth * fftHeight];

            for (int y = 0; y < height; y++)
            {
                double wy = height <= 1
                    ? 1.0
                    : 0.5 - (0.5 * Math.Cos((2.0 * Math.PI * y) / (height - 1)));
                for (int x = 0; x < width; x++)
                {
                    double wx = width <= 1
                        ? 1.0
                        : 0.5 - (0.5 * Math.Cos((2.0 * Math.PI * x) / (width - 1)));
                    double value = (source[(y * width) + x] - mean) * wx * wy;
                    data[(y * fftWidth) + x] = new Complex(value, 0.0);
                }
            }

            return data;
        }

        private static void FFT2D(Complex[] data, int width, int height, bool inverse)
        {
            var temp = new Complex[Math.Max(width, height)];

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    temp[x] = data[rowOffset + x];
                }

                FFT(temp, width, inverse);

                for (int x = 0; x < width; x++)
                {
                    data[rowOffset + x] = temp[x];
                }
            }

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    temp[y] = data[(y * width) + x];
                }

                FFT(temp, height, inverse);

                for (int y = 0; y < height; y++)
                {
                    data[(y * width) + x] = temp[y];
                }
            }
        }

        private static void FFT(Complex[] buffer, int length, bool inverse)
        {
            for (int i = 1, j = 0; i < length; i++)
            {
                int bit = length >> 1;
                while ((j & bit) != 0)
                {
                    j &= ~bit;
                    bit >>= 1;
                }

                j |= bit;
                if (i < j)
                {
                    Complex temp = buffer[i];
                    buffer[i] = buffer[j];
                    buffer[j] = temp;
                }
            }

            for (int len = 2; len <= length; len <<= 1)
            {
                double angle = (inverse ? 2.0 : -2.0) * Math.PI / len;
                Complex wLen = new Complex(Math.Cos(angle), Math.Sin(angle));

                for (int i = 0; i < length; i += len)
                {
                    Complex w = Complex.One;
                    int halfLen = len >> 1;
                    for (int j = 0; j < halfLen; j++)
                    {
                        Complex u = buffer[i + j];
                        Complex v = buffer[i + j + halfLen] * w;
                        buffer[i + j] = u + v;
                        buffer[i + j + halfLen] = u - v;
                        w *= wLen;
                    }
                }
            }

            if (inverse)
            {
                for (int i = 0; i < length; i++)
                {
                    buffer[i] /= length;
                }
            }
        }

        private static int NextPowerOfTwo(int value)
        {
            int power = 1;
            while (power < value)
                power <<= 1;
            return power;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
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
