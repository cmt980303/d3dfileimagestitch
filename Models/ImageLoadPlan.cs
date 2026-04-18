namespace GPUStitch.Models
{
    /// <summary>
    /// 一次图片导入会话的加载计划。
    ///
    /// 该模型表达的是“这批图片应该按什么比例加载进 GPU 预览链路”：
    /// - <see cref="Scale"/> 决定真正解码时的统一缩放比例；
    /// - <see cref="RawSourceBytes"/> 表示所有原图若不缩放时的理论占用；
    /// - <see cref="PlannedSourceBytes"/> 表示按当前比例加载后的预计占用。
    ///
    /// 它本质上是预算模块和 UI / 加载模块之间的契约对象。
    /// </summary>
    public sealed class ImageLoadPlan
    {
        /// <summary>
        /// 实际加载比例。
        /// 1.0 表示原尺寸；小于 1 表示为了预算而降采样。
        /// </summary>
        public float Scale { get; set; } = 1.0f;

        /// <summary>所有源图按原尺寸加载时的估算总字节数。</summary>
        public long RawSourceBytes { get; set; }

        /// <summary>按 <see cref="Scale"/> 缩放后预计需要的总字节数。</summary>
        public long PlannedSourceBytes { get; set; }

        /// <summary>本次计划包含的图片数量。</summary>
        public int ImageCount { get; set; }

        /// <summary>
        /// 是否发生了降采样。
        /// 之所以使用 0.999 作为阈值，是为了避免浮点误差把 1.0 附近的值误判成缩放。
        /// </summary>
        public bool IsDownsampled => Scale < 0.999f;
    }
}
