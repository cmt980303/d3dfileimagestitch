namespace GPUStitch.Models
{
    /// <summary>
    /// 图片文件的轻量级元数据。
    ///
    /// 这个模型只保留预算阶段真正需要的信息：
    /// - 文件路径；
    /// - 像素宽高；
    /// - 根据 BGRA8 估算得到的原始字节数。
    ///
    /// 之所以不在预算阶段直接解码整图，是为了让“导入前的资源评估”足够轻，
    /// 这样程序就可以先快速决定是否需要统一降采样，再进入真正的纹理解码和上传。
    /// </summary>
    public sealed class ImageFileMetadata
    {
        public ImageFileMetadata(string filePath, int pixelWidth, int pixelHeight)
        {
            FilePath = filePath;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
        }

        /// <summary>图片文件完整路径。</summary>
        public string FilePath { get; }

        /// <summary>图片像素宽度。</summary>
        public int PixelWidth { get; }

        /// <summary>图片像素高度。</summary>
        public int PixelHeight { get; }

        /// <summary>
        /// 按 BGRA8 粗略估算的源图显存/内存占用。
        /// 该值用于预估预算，而不是表示实际文件体积。
        /// </summary>
        public long EstimatedSourceBytes => (long)PixelWidth * PixelHeight * 4L;
    }
}
