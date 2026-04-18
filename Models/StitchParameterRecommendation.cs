namespace GPUStitch.Models
{
    /// <summary>
    /// 智能参数推荐模块输出的建议结果。
    ///
    /// 当前推荐器的目标不是直接给出“绝对正确”的物理参数，而是给出一组对当前批次图像
    /// 足够稳妥的初始化值，帮助用户更快进入可用状态。
    /// </summary>
    public sealed class StitchParameterRecommendation
    {
        /// <summary>推荐的重叠像素数。</summary>
        public int OverlapPixels { get; set; }

        /// <summary>推荐的混合羽化宽度。</summary>
        public int BlendWidth { get; set; }

        /// <summary>
        /// 推荐置信度。
        /// 这个值来自若干图像对估算得分的平均值，越高说明推荐越值得参考。
        /// </summary>
        public float Confidence { get; set; }

        /// <summary>参与估算的有效图像对数量。</summary>
        public int EvaluatedPairCount { get; set; }
    }
}
