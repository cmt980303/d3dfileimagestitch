using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GPUStitch.Models;

namespace GPUStitch.Core
{
    /// <summary>
    /// 拼图参数推荐器。
    ///
    /// 它试图根据一批已加载图片，给出两个对用户最有帮助的初始参数：
    /// 1. 相邻图片的重叠像素；
    /// 2. 重叠区域的混合羽化宽度。
    ///
        /// 当前算法并不依赖完整精配准主链路，而是走一条更轻量的启发式路径：
    /// - 从磁盘重新读取小尺寸灰度预览；
    /// - 对相邻图做梯度相关性搜索；
    /// - 统计一批图像对的重叠估计中位数；
    /// - 再把它映射成用户可直接使用的 overlap / blend 值。
    /// </summary>
    public static class StitchParameterAdvisor
    {
        private const int MaxPreviewCacheEntries = 128;
        private static readonly object PreviewCacheLock = new object();
        private static readonly Dictionary<string, PreviewImage> PreviewCache =
            new Dictionary<string, PreviewImage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 对当前图片集合给出一组推荐参数。
        /// </summary>
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

            // 这里使用多个图像对的统计结果，而不是只看一对，
            // 这样对局部异常、内容单一或个别坏片更稳。
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

                var left = GetOrLoadPreview(images[i - 1].FilePath, 512);
                var right = GetOrLoadPreview(images[i].FilePath, 512);
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

        private static PreviewImage GetOrLoadPreview(string filePath, int maxDimension)
        {
            lock (PreviewCacheLock)
            {
                if (PreviewCache.TryGetValue(filePath, out var cached))
                    return cached;
            }

            var loaded = LoadPreview(filePath, maxDimension);

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

        /// <summary>
        /// 对一对小预览图估计重叠宽度。
        /// 这里把搜索问题简化为：
        /// - 枚举若干 overlap 候选；
        /// - 对每个 overlap 再枚举少量 Y 方向漂移；
        /// - 选取梯度相关性最高的一组参数。
        /// </summary>
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

        /// <summary>
        /// 计算两幅灰度预览在某个 overlap/shiftY 假设下的梯度相关性。
        ///
        /// 这里故意使用梯度而不是直接用亮度，是因为显微图像常出现照明不均、
        /// 暗角、整体亮度漂移等问题，梯度对这些低频亮度变化更不敏感。
        /// </summary>
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

            // 以 2 像素步长采样，牺牲一点精度换更高吞吐。
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

        /// <summary>
        /// 从磁盘读取一张用于参数推荐的小灰度预览。
        /// 这个路径独立于 GPU 纹理，不会干扰当前显示链路。
        /// </summary>
        private static PreviewImage LoadPreview(string filePath, int maxDimension)
        {
            int originalWidth, originalHeight;

            // 使用 FileStream 可以确保读取完毕后立即释放文件句柄
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // 1. 仅读取图像元数据，不解码像素（DelayCreation），几乎瞬间完成
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                originalWidth = decoder.Frames[0].PixelWidth;
                originalHeight = decoder.Frames[0].PixelHeight;

                // 重置流位置，供接下来的实际解码使用
                stream.Position = 0;

                // 2. 利用 WIC 底层机制，在解码加载时直接缩小图像
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // 确保加载后释放流缓存

                if (originalWidth > maxDimension || originalHeight > maxDimension)
                {
                    // 仅需设置较长的一边，WPF 会自动帮你保持图像的正确宽高比
                    if (originalWidth > originalHeight)
                        bitmap.DecodePixelWidth = maxDimension;
                    else
                        bitmap.DecodePixelHeight = maxDimension;
                }

                bitmap.EndInit();
                bitmap.Freeze();

                // 3. 此时的 bitmap 已经是缩小后的尺寸了，对其进行灰度转换的计算量呈指数级下降
                var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
                gray.Freeze();

                // 4. 提取像素数据
                int width = gray.PixelWidth;
                int height = gray.PixelHeight;
                int stride = width; // Gray8 格式每像素1字节，stride 即为 width
                byte[] pixels = new byte[height * stride];
                gray.CopyPixels(pixels, stride, 0);

                return new PreviewImage(width, height, pixels);
            }
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

        /// <summary>
        /// 单对图片的重叠估计结果。
        /// </summary>
        private sealed class PairEstimate
        {
            public int OverlapPixels { get; set; }
            public float Score { get; set; }
        }

        /// <summary>
        /// 只用于推荐算法的轻量灰度图封装。
        /// </summary>
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
