using System;
using System.Collections.Generic;

namespace GPUStitch.Models
{
    /// <summary>
    /// 配准参数集合。
    ///
    /// 这些参数控制：
    /// - 预估重叠区域大小；
    /// - 局部搜索窗口范围；
    /// - 采样步长；
    /// - 混合相关评分中，亮度项和梯度项的权重；
    /// - 判断“可信配准”的阈值。
    /// </summary>
    public sealed class RegistrationOptions
    {
        /// <summary>预估水平重叠宽度。</summary>
        public int ExpectedHorizontalOverlap { get; set; } = 100;

        /// <summary>预估垂直重叠高度。</summary>
        public int ExpectedVerticalOverlap { get; set; } = 100;

        /// <summary>主轴方向局部搜索半径（水平配准时为 X，垂直配准时为 Y）。</summary>
        public int SearchRangePrimary { get; set; } = 64;

        /// <summary>交叉轴方向局部搜索半径（水平配准时为 Y，垂直配准时为 X）。</summary>
        public int SearchRangeCross { get; set; } = 32;

        /// <summary>采样步长。步长越大，速度越快，但细节利用越少。</summary>
        public int SampleStep { get; set; } = 2;

        /// <summary>梯度样本最小能量阈值。低于该值的样本会被视为纹理过弱而跳过。</summary>
        public float MinGradientEnergy { get; set; } = 0.00015f;

        /// <summary>亮度零均值相关中允许的最小方差阈值。</summary>
        public float MinLumaVariance { get; set; } = 0.00005f;

        /// <summary>最低有效样本数量。</summary>
        public int MinSampleCount { get; set; } = 64;

        /// <summary>梯度相关分数在混合评分中的权重。</summary>
        public float GradientWeight { get; set; } = 0.45f;

        /// <summary>亮度相关分数在混合评分中的权重。</summary>
        public float LumaWeight { get; set; } = 0.55f;

        /// <summary>
        /// 判定“可信配准”的阈值。
        /// 当前版本偏向先跑通，因此阈值设置得相对保守宽松。
        /// </summary>
        public float ConfidenceThreshold { get; set; } = 0.03f;

        /// <summary>
        /// 由 peak / round-trip / local drift 组合得到的几何可靠度阈值。
        /// 低于该值时，即使总分不低，也视作“局部支持不一致”，回退到保守位移。
        /// </summary>
        public float GeometryConfidenceThreshold { get; set; } = 0.30f;

        /// <summary>
        /// 根据图片尺寸和预估重叠量自动生成一组相对稳妥的参数。
        /// 当前先把水平/垂直重叠都初始化为同一个值，后续可以再独立细化。
        /// </summary>
        public static RegistrationOptions CreateForImages(
            IReadOnlyList<GpuImage> images,
            int expectedOverlap,
            float knownOverlapRatio = 0.0f)
        {
            if (images == null || images.Count == 0)
            {
                return new RegistrationOptions
                {
                    ExpectedHorizontalOverlap = expectedOverlap,
                    ExpectedVerticalOverlap = expectedOverlap,
                };
            }

            int minWidth = images[0].Width;
            int minHeight = images[0].Height;

            for (int i = 1; i < images.Count; i++)
            {
                minWidth = Math.Min(minWidth, images[i].Width);
                minHeight = Math.Min(minHeight, images[i].Height);
            }

            int expectedHorizontalOverlap = expectedOverlap;
            int expectedVerticalOverlap = expectedOverlap;

            bool hasKnownOverlapRatio = knownOverlapRatio > 0.0f && knownOverlapRatio < 0.5f;
            if (hasKnownOverlapRatio)
            {
                expectedHorizontalOverlap = Clamp(
                    (int)Math.Round(minWidth * knownOverlapRatio),
                    8,
                    Math.Max(8, minWidth - 2));
                expectedVerticalOverlap = Clamp(
                    (int)Math.Round(minHeight * knownOverlapRatio),
                    8,
                    Math.Max(8, minHeight - 2));
            }

            int dominantOverlap = Math.Max(expectedHorizontalOverlap, expectedVerticalOverlap);
            int searchRangePrimary = hasKnownOverlapRatio
                ? Clamp(dominantOverlap / 6, 8, 64)
                : Clamp(expectedOverlap / 2, 16, 192);
            int searchRangeCross = hasKnownOverlapRatio
                ? Clamp(Math.Min(minWidth, minHeight) / 128, 4, 24)
                : Clamp(Math.Min(minWidth, minHeight) / 48, 6, 96);
            int sampleStep = minWidth >= 3500 || minHeight >= 3500 ? 3 : 2;
            int minSamples = sampleStep <= 2 ? 64 : 48;
            float minGradientEnergy = sampleStep <= 2 ? 0.00015f : 0.00010f;
            float minLumaVariance = sampleStep <= 2 ? 0.00005f : 0.00003f;

            return new RegistrationOptions
            {
                ExpectedHorizontalOverlap = expectedHorizontalOverlap,
                ExpectedVerticalOverlap = expectedVerticalOverlap,
                SearchRangePrimary = searchRangePrimary,
                SearchRangeCross = searchRangeCross,
                SampleStep = sampleStep,
                MinSampleCount = minSamples,
                MinGradientEnergy = minGradientEnergy,
                MinLumaVariance = minLumaVariance,
                GradientWeight = 0.20f,
                LumaWeight = 0.80f,
                ConfidenceThreshold = 0.03f,
                GeometryConfidenceThreshold = 0.30f,
            };
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}
