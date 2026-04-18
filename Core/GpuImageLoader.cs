using System;
using System.IO;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GPUStitch.Core
{
    /// <summary>
    /// GPU 图片加载器
    /// 将本地图片加载为 D3D11 Texture2D + SRV，供 Compute Shader 使用。
    /// 
    /// 流程：WPF BitmapImage 解码 → BGRA32 像素数组 → D3D11 Texture2D（Immutable） → SRV
    /// 
    /// 所有纹理在 D3DDeviceManager 持有的设备上创建（即 D3D11Image 的内部设备），
    /// 确保后续 CopyResource / Dispatch 不会跨设备。
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
        /// 从文件路径加载图片到 GPU 纹理
        /// </summary>
        public GpuImage LoadFromFile(string filePath)
        {
            return LoadFromFile(filePath, scale: 1.0f);
        }

        /// <summary>
        /// 按指定缩放比例加载图片到 GPU。
        /// 该方法用于“大批量预览模式”：
        /// - 先按预算统一算出一个缩放比例；
        /// - 再以同一比例加载所有图片，避免显存/内存被一次性打爆。
        /// </summary>
        public GpuImage LoadFromFile(string filePath, float scale)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"图片文件不存在: {filePath}");

            // 用 WPF 解码图片并转为 BGRA32
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

            var convertedBitmap = new FormatConvertedBitmap(
                bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            int width = convertedBitmap.PixelWidth;
            int height = convertedBitmap.PixelHeight;
            int stride = width * 4;

            byte[] pixels = new byte[height * stride];
            convertedBitmap.CopyPixels(pixels, stride, 0);

            return CreateTextureFromPixels(pixels, width, height, stride);
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
        /// 从 BGRA32 像素数据创建 GPU 纹理 + SRV
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

    /// <summary>
    /// GPU 图片数据：持有 D3D11 纹理和 SRV，使用后需 Dispose
    /// </summary>
    public class GpuImage : IDisposable
    {
        public ID3D11Texture2D Texture { get; set; } = null!;
        public ID3D11ShaderResourceView ShaderResourceView { get; set; } = null!;
        public int Width { get; set; }
        public int Height { get; set; }
        public string FilePath { get; set; } = string.Empty;

        public void Dispose()
        {
            ShaderResourceView?.Dispose();
            Texture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 图片文件元数据。
    /// 当前只保存预算所需的最小信息：路径、宽高和源图估算字节数。
    /// </summary>
    public sealed class ImageFileMetadata
    {
        public ImageFileMetadata(string filePath, int pixelWidth, int pixelHeight)
        {
            FilePath = filePath;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
        }

        public string FilePath { get; }
        public int PixelWidth { get; }
        public int PixelHeight { get; }
        public long EstimatedSourceBytes => (long)PixelWidth * PixelHeight * 4L;
    }
}
