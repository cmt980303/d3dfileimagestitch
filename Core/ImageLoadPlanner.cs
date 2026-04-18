using System;
using System.Collections.Generic;
using GPUStitch.Models;

namespace GPUStitch.Core
{
    /// <summary>
    /// 根据图片元数据为当前会话生成加载预算方案。
    ///
    /// 这里解决的问题是：
    /// “在不知道最终拼图画布多大之前，怎样先保证原始输入纹理不会把显存/内存吃爆？”
    ///
    /// 当前策略把预算拆成两层：
    /// 1. 本类只控制源图纹理常驻预算；
    /// 2. 预览画布预算由 MainWindow 中的 PreparePreviewLayout 单独控制。
    ///
    /// 这种拆分能让“输入资源规模”和“输出画布规模”分别收敛，调试时也更直观。
    /// </summary>
    public static class ImageLoadPlanner
    {
        private const int BytesPerSourcePixel = 4; // BGRA8

        /// <summary>
        /// 为一批图片生成统一加载比例。
        ///
        /// 算法要点：
        /// 1. 先统计所有图片的原始估算字节数；
        /// 2. 再根据单张图最大边长得到尺寸约束；
        /// 3. 如果总字节超预算，则对面积按平方根缩放；
        /// 4. 取所有约束中的最小比例作为最终 scale。
        ///
        /// 使用平方根的原因是：显存开销和像素面积近似成正比，
        /// 若希望总字节缩放到某个比例，边长应按其平方根缩放。
        /// </summary>
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

            // 面积预算换成边长预算时取平方根。
            double budgetScale = 1.0;
            if (rawBytes > sourceGpuBudgetBytes && rawBytes > 1)
            {
                budgetScale = Math.Sqrt(sourceGpuBudgetBytes / rawBytes);
            }

            double scale = Math.Min(1.0, Math.Min(dimensionScale, budgetScale));
            // 至少保留一个极小预览比例，保证系统仍能给出“能看见全局关系”的预览。
            scale = Math.Max(scale, 0.05);

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
