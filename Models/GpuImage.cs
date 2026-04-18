using System;
using Vortice.Direct3D11;

namespace GPUStitch.Models
{
    /// <summary>
    /// 一张已经上传到 GPU 的图像资源。
    ///
    /// 它既包含原始纹理对象，也包含供着色器读取的 SRV（Shader Resource View）。
    /// 在本项目里，图像一旦被上传到 GPU，就会以这个模型在各模块之间流转：
    /// - 配准器把它当作输入纹理做相对位移搜索；
    /// - 拼图器把它当作源纹理累计到输出画布；
    /// - 参数推荐器则通过它的尺寸和原始路径做估算。
    ///
    /// 由于内部持有原生 COM 资源，所以这个模型实现了 <see cref="IDisposable"/>，
    /// 调用方必须在不再需要时主动释放。
    /// </summary>
    public sealed class GpuImage : IDisposable
    {
        /// <summary>底层 D3D11 纹理对象。</summary>
        public ID3D11Texture2D Texture { get; set; } = null!;

        /// <summary>供 Compute Shader / 其他着色器读取的资源视图。</summary>
        public ID3D11ShaderResourceView ShaderResourceView { get; set; } = null!;

        /// <summary>纹理宽度（像素）。</summary>
        public int Width { get; set; }

        /// <summary>纹理高度（像素）。</summary>
        public int Height { get; set; }

        /// <summary>
        /// 源文件路径。
        /// 这个字段主要服务于：
        /// 1. 参数推荐阶段重新加载预览图；
        /// 2. 调试与状态显示；
        /// 3. 后续全分辨率导出重新回源读取。
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 释放这张图像持有的 GPU 资源。
        /// 释放顺序先 SRV 后 Texture，符合依赖关系，也更符合阅读直觉。
        /// </summary>
        public void Dispose()
        {
            ShaderResourceView?.Dispose();
            Texture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
