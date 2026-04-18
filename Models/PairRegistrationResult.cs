namespace GPUStitch.Models
{
    /// <summary>
    /// 描述配准发生在水平方向还是垂直方向。
    /// - Horizontal：左图 -> 右图
    /// - Vertical：上图 -> 下图
    /// </summary>
    public enum RegistrationAxis
    {
        Horizontal = 0,
        Vertical = 1,
    }

    /// <summary>
    /// 单次相邻图像配准的结果。
    ///
    /// 该对象记录：
    /// - 参与配准的两张图在原始列表中的索引；
    /// - 配准方向；
    /// - 搜索到的最佳局部偏移；
    /// - 最终用于全局布局的相对位移；
    /// - 当前结果是否达到可接受置信度。
    /// </summary>
    public sealed class PairRegistrationResult
    {
        public PairRegistrationResult(
            int sourceIndex,
            int targetIndex,
            RegistrationAxis axis,
            int bestDeltaX,
            int bestDeltaY,
            float score,
            int overlapSize,
            int relativeOffsetX,
            int relativeOffsetY,
            bool isConfident)
        {
            SourceIndex = sourceIndex;
            TargetIndex = targetIndex;
            Axis = axis;
            BestDeltaX = bestDeltaX;
            BestDeltaY = bestDeltaY;
            Score = score;
            OverlapSize = overlapSize;
            RelativeOffsetX = relativeOffsetX;
            RelativeOffsetY = relativeOffsetY;
            IsConfident = isConfident;
        }

        /// <summary>源图索引。水平配准时为左图，垂直配准时为上图。</summary>
        public int SourceIndex { get; }

        /// <summary>目标图索引。水平配准时为右图，垂直配准时为下图。</summary>
        public int TargetIndex { get; }

        /// <summary>配准方向。</summary>
        public RegistrationAxis Axis { get; }

        /// <summary>
        /// 搜索窗口内得到的最佳 X 偏移修正量。
        /// 它是相对“预估重叠位置”的局部修正，而不是最终世界坐标。
        /// </summary>
        public int BestDeltaX { get; }

        /// <summary>
        /// 搜索窗口内得到的最佳 Y 偏移修正量。
        /// </summary>
        public int BestDeltaY { get; }

        /// <summary>
        /// 配准评分。当前使用“零均值亮度相关 + 梯度相关”的混合分数。
        /// 分数越高，说明两张图在该候选位移下越一致。
        /// </summary>
        public float Score { get; }

        /// <summary>
        /// 本次配准使用的预估重叠尺寸。
        /// 水平配准时表示重叠宽度，垂直配准时表示重叠高度。
        /// </summary>
        public int OverlapSize { get; }

        /// <summary>
        /// 从源图到目标图的最终 X 位移。
        /// 这是布局阶段真正会使用的偏移量。
        /// </summary>
        public int RelativeOffsetX { get; }

        /// <summary>
        /// 从源图到目标图的最终 Y 位移。
        /// </summary>
        public int RelativeOffsetY { get; }

        /// <summary>
        /// 当前结果是否达到置信阈值。
        /// 如果为 false，通常意味着本次结果已经回退到更保守的预估位移。
        /// </summary>
        public bool IsConfident { get; }
    }
}
