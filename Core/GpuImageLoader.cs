using System;
using System.IO;
using GPUStitch.Models;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GPUStitch.Core
{
    /// <summary>
    /// GPU 图片加载器。
    ///
    /// 它的职责是把磁盘上的常见图片格式转换成项目内部统一使用的 <see cref="GpuImage"/>：
    /// 1. 使用 WPF 解码器读取图片；
    /// 2. 转成 BGRA32 像素排列；
    /// 3. 上传为 D3D11 纹理；
    /// 4. 创建供 Compute Shader 使用的 SRV。
    ///
    /// 这里选用 WPF 的解码链路，是因为它对常见桌面图片格式支持完整，
    /// 同时能方便地在导入时直接做 DecodePixelWidth 缩放，减少 CPU 和 GPU 压力。
    /// </summary>
    public class GpuImageLoader : IDisposable
    {
        private readonly D3DDeviceManager _deviceManager;
        private bool _disposed;

        public GpuImageLoader(D3DDeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }





        /// <summary>
        /// 按原始尺寸把图片加载到 GPU。
        /// 这是对 <see cref="LoadFromFile(string, float)"/> 的便捷封装。
        /// </summary>
        public GpuImage LoadFromFile(string filePath)
        {
            return LoadFromFile(filePath, scale: 1.0f);
        }

        /// <summary>
        /// 按指定缩放比例加载图片到 GPU。
        ///
        /// 该方法是“预算驱动导入”的真正执行点：
        /// - 外层先根据元数据算出统一 scale；
        /// - 这里再按同一 scale 解码每一张图片；
        /// - 最终所有预览纹理都处于同一几何尺度，便于配准和布局。
        /// </summary>
        public GpuImage LoadFromFile(string filePath, float scale)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"图片文件不存在: {filePath}");

            // 先让 WPF 完成格式解码。
            // 当 scale < 1 时，直接在解码阶段降采样，比解码完整图再缩放更省资源。
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (scale > 0.0f && scale < 0.999f)
            {
                var metadata = ProbeFile(filePath);
                int decodeWidth = Math.Max(1, (int)Math.Round(metadata.PixelWidth * scale));
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.EndInit();
            bitmap.Freeze();

            // 统一转成 BGRA32，保证后续上传到 GPU 时像素格式稳定可控。
            var convertedBitmap = new FormatConvertedBitmap(
                bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            int width = convertedBitmap.PixelWidth;
            int height = convertedBitmap.PixelHeight;
            int stride = width * 4;

            byte[] pixels = new byte[height * stride];
            convertedBitmap.CopyPixels(pixels, stride, 0);

            var image = CreateTextureFromPixels(pixels, width, height, stride);
            image.FilePath = filePath;
            return image;
        }

        /// <summary>
        /// 只读取图片元数据，不解码整图。
        /// 用于预算阶段快速估算资源占用。
        /// </summary>
        public static ImageFileMetadata ProbeFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"图片文件不存在: {filePath}");

            using var stream = File.OpenRead(filePath);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);

            var frame = decoder.Frames[0];
            return new ImageFileMetadata(filePath, frame.PixelWidth, frame.PixelHeight);
        }

        /// <summary>
        /// 从 BGRA32 像素数组创建不可变纹理和对应 SRV。
        ///
        /// 这里使用 Immutable 纹理，是因为源图上传后不会再被 CPU 改写，
        /// 这样驱动可以用更适合只读采样的资源布局去管理它。
        /// </summary>
        internal unsafe GpuImage CreateTextureFromPixels(byte[] pixels, int width, int height, int stride)
        {
            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            fixed (byte* pPixels = pixels)
            {
                var subresourceData = new SubresourceData
                {
                    DataPointer = (IntPtr)pPixels,
                    RowPitch = stride,
                };

                var texture = _deviceManager.Device.CreateTexture2D(desc, new[] { subresourceData });
                var srv = _deviceManager.Device.CreateShaderResourceView(texture);

                return new GpuImage
                {
                    Texture = texture,
                    ShaderResourceView = srv,
                    Width = width,
                    Height = height,
                    FilePath = string.Empty,
                };
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
