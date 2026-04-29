namespace GPUStitch.Models
{
    /// <summary>
    /// 描述单张图像在输出大画布中的目标放置参数。
    ///
    /// 这个类型是拼接阶段最核心的几何模型之一：
    /// 1. OffsetX / OffsetY 表示图像左上角在目标画布中的位置；
    /// 2. Width / Height 表示这张图最终要被绘制成多大；
    /// 3. Feather* 表示四个边缘与相邻图的实际重叠范围，供拼缝权重计算使用。
    ///
    /// 之所以把“位置”和“羽化信息”放在同一个模型里，是因为 GPU 拼图着色器
    /// 在处理一张图时，需要同时知道它被放到哪里、以多大尺寸采样、以及哪些边缘
    /// 要做权重衰减；这些信息总是一起出现，适合作为一个独立模型统一传递。
    /// </summary>
    public struct ImagePlacement
    {
        /// <summary>目标画布中的左上角 X 偏移。</summary>
        public float OffsetX;

        /// <summary>目标画布中的左上角 Y 偏移。</summary>
        public float OffsetY;

        /// <summary>
        /// 输出时的目标宽度。
        /// 在全分辨率拼图时它通常等于原图宽度；在预览模式中则可能是缩小后的宽度。
        /// </summary>
        public float Width;

        /// <summary>
        /// 输出时的目标高度。
        /// 在全分辨率拼图时它通常等于原图高度；在预览模式中则可能是缩小后的高度。
        /// </summary>
        public float Height;

        /// <summary>左边缘与相邻图的重叠范围。</summary>
        public float FeatherLeft;

        /// <summary>右边缘与相邻图的重叠范围。</summary>
        public float FeatherRight;

        /// <summary>上边缘与相邻图的重叠范围。</summary>
        public float FeatherTop;

        /// <summary>下边缘与相邻图的重叠范围。</summary>
        public float FeatherBottom;
    }
}
