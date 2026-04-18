using System;
using System.Collections.Generic;

namespace GPUStitch.Core
{
    /// <summary>
    /// 图片加载计划。
    /// 用于在真正解码/上传前，先根据图片尺寸估算资源占用，
    /// 决定是否需要统一降采样后再加载。
    /// </summary>
    public sealed class ImageLoadPlan
    {
        public float Scale { get; set; } = 1.0f;
        public long RawSourceBytes { get; set; }
        public long PlannedSourceBytes { get; set; }
        public int ImageCount { get; set; }
        public bool IsDownsampled => Scale < 0.999f;
    }

    /// <summary>
    /// 根据图片元数据为当前会话生成加载预算方案。
    /// 当前只控制“源图纹理”预算，真正的输出画布预算在 UI 预览阶段单独控制。
    /// </summary>
    public static class ImageLoadPlanner
    {
        private const int BytesPerSourcePixel = 4; // BGRA8

        public static ImageLoadPlan Build(
            IReadOnlyList<ImageFileMetadata> metadata,
            long sourceGpuBudgetBytes,
            int maxPerImageDimension)
        {
            if (metadata == null || metadata.Count == 0)
                return new ImageLoadPlan();

            double rawBytes = 0;
            double dimensionScale = 1.0;

            for (int i = 0; i < metadata.Count; i++)
            {
                rawBytes += metadata[i].EstimatedSourceBytes;

                int maxDimension = Math.Max(metadata[i].PixelWidth, metadata[i].PixelHeight);
                if (maxDimension > 0)
                {
                    dimensionScale = Math.Min(
                        dimensionScale,
                        maxPerImageDimension / (double)maxDimension);
                }
            }

            dimensionScale = Math.Min(1.0, dimensionScale);

            double budgetScale = 1.0;
            if (rawBytes > sourceGpuBudgetBytes && rawBytes > 1)
            {
                budgetScale = Math.Sqrt(sourceGpuBudgetBytes / rawBytes);
            }

            double scale = Math.Min(1.0, Math.Min(dimensionScale, budgetScale));
            scale = Math.Max(scale, 0.05); // 至少保留一个极小预览比例，避免退成 0

            long plannedBytes = (long)Math.Ceiling(rawBytes * scale * scale);

            return new ImageLoadPlan
            {
                Scale = (float)scale,
                RawSourceBytes = (long)Math.Ceiling(rawBytes),
                PlannedSourceBytes = plannedBytes,
                ImageCount = metadata.Count,
            };
        }
    }
}
