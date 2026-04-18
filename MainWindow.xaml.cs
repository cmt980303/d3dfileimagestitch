using GPUStitch.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using Vortice.Direct3D11;

namespace GPUStitch
{
    /// <summary>
    /// 主窗口：D3D11Image 渲染管线
    /// 
    /// 核心流程（参考官方 WPFDXInterop C++ 示例 D3DVisualization）：
    /// 1. Window_Loaded → 创建自己的 D3D11 设备 + 设置 D3D11Image 回调
    /// 2. D3D11Image OnRender → surface 是共享纹理 → 通过 SharedHandle 在我们设备上打开
    /// 3. 加载图片 → 在我们的设备上创建 GPU 纹理（与共享纹理同设备）
    /// 4. CopyResource 写入共享纹理 → Flush → D3D11Image 显示到 WPF
    /// 
    /// 关键设计：我们的设备与 D3D11Image 内部设备是两个不同实例，
    /// 但通过 DXGI SharedHandle 共享同一块 GPU 显存，避免跨设备错误。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int MaxPreviewTextureSize = 8192;

        private D3DDeviceManager? _deviceManager;
        private GpuImageLoader? _imageLoader;
        private GpuStitcher? _stitcher;
        private GpuRegistration? _registration;
        private readonly List<GpuImage> _loadedImages = new List<GpuImage>();

        /// <summary>GPU 是否已初始化</summary>
        private bool _gpuInitialized = false;

        /// <summary>在我们设备上打开的共享渲染目标（缓存，isNewSurface 时重建）</summary>
        private ID3D11Texture2D? _sharedRenderTarget;

        private RenderMode _renderMode = RenderMode.None;
        private List<ImagePlacement> _currentPlacements = new List<ImagePlacement>();
        private int _currentCanvasWidth;
        private int _currentCanvasHeight;

        public MainWindow()
        {
            InitializeComponent();

            SliderBlendWidth.ValueChanged += (s, e) =>
                TxtBlendValue.Text = ((int)SliderBlendWidth.Value).ToString();
            SliderOverlap.ValueChanged += (s, e) =>
                TxtOverlapValue.Text = ((int)SliderOverlap.Value).ToString();
        }

        /// <summary>
        /// 窗口加载：创建自己的 D3D11 设备，然后设置 D3D11Image 回调。
        /// 设备创建不依赖 surface，可以在此处立即完成。
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 创建自己的 D3D11 设备（与 D3D11Image 无关，独立的硬件设备）
                _deviceManager = new D3DDeviceManager();
                _deviceManager.Initialize();

                _imageLoader = new GpuImageLoader(_deviceManager);
                _stitcher = new GpuStitcher(_deviceManager);
                _stitcher.Initialize();
                _registration = new GpuRegistration(_deviceManager);
                _registration.Initialize();

                _gpuInitialized = true;

                // 设置 D3D11Image 回调
                var hwnd = new WindowInteropHelper(this).Handle;
                InteropImage.WindowOwner = hwnd;
                InteropImage.OnRender = RenderFrame;

                //// 设置初始尺寸并触发首次渲染
                //InteropImage.SetPixelSize(64, 64);
                //InteropImage.RequestRender();

                UpdateStatus("GPU 初始化成功，支持梯度配准 + GPU 拼图");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"GPU 初始化失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        // ===== 按钮事件 =====

        /// <summary>加载图片到 GPU</summary>
        private void BtnLoadImages_Click(object sender, RoutedEventArgs e)
        {
            if (!_gpuInitialized)
            {
                MessageBox.Show("GPU 设备尚未初始化，请稍候再试", "提示");
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = "选择要加载的图片（可多选）",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tiff|所有文件|*.*",
                Multiselect = true,
            };

            if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0)
                return;

            try
            {
                foreach (var filePath in dlg.FileNames)
                {
                    var gpuImage = _imageLoader!.LoadFromFile(filePath);
                    gpuImage.FilePath = filePath;
                    _loadedImages.Add(gpuImage);
                }

                BtnStitch.IsEnabled = _loadedImages.Count >= 2;
                BtnRecommend.IsEnabled = _loadedImages.Count >= 2;
                BtnShowSingle.IsEnabled = _loadedImages.Count >= 1;
                ApplyRecommendedParameters(showStatus: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"图片加载失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>GPU 配准后拼图</summary>
        private void BtnStitch_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedImages.Count < 2)
            {
                MessageBox.Show("请至少加载 2 张图片", "提示");
                return;
            }

            if (_loadedImages.Count > GpuStitcher.MaxInputImages)
            {
                MessageBox.Show(
                    $"当前演示版本最多一次拼接 {GpuStitcher.MaxInputImages} 张图片，请先减少图片数量。",
                    "提示");
                return;
            }

            try
            {
                int overlapPixels = (int)SliderOverlap.Value;
                float blendWidth = (float)SliderBlendWidth.Value;

                var registrationOptions =
                    RegistrationOptions.CreateForImages(_loadedImages, overlapPixels);

                var layout = _registration!.ComputeHorizontalLayout(
                    _loadedImages,
                    registrationOptions);

                if (layout.CanvasWidth <= 0 || layout.CanvasHeight <= 0)
                {
                    MessageBox.Show("计算画布尺寸失败", "错误");
                    return;
                }

                var preview = PreparePreviewLayout(layout, blendWidth);

                _stitcher!.BlendWidth = preview.BlendWidth;
                _currentCanvasWidth = preview.CanvasWidth;
                _currentCanvasHeight = preview.CanvasHeight;
                _currentPlacements = preview.Placements;
                _renderMode = RenderMode.Stitch;
                _sharedRenderTarget = null;

                InteropImage.SetPixelSize(preview.CanvasWidth, preview.CanvasHeight);
                InteropImage.RequestRender();

                int fallbackPairs = layout.PairResults.Count - layout.ConfidentPairCount;
                string previewSuffix = preview.Scale < 0.999f
                    ? $", 预览缩放 {preview.Scale:F3}"
                    : string.Empty;
                UpdateStatus(
                    $"配准并拼图完成: {layout.CanvasWidth}×{layout.CanvasHeight}, " +
                    $"平均分 {layout.AverageScore:F3}, 可信 {layout.ConfidentPairCount}/{layout.PairResults.Count}, " +
                    $"回退 {fallbackPairs} 对{previewSuffix}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"GPU 配准/拼图失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRecommend_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedImages.Count < 2)
            {
                MessageBox.Show("请至少加载 2 张图片", "提示");
                return;
            }

            ApplyRecommendedParameters(showStatus: true);
        }

        /// <summary>显示单张图片（验证渲染管线）</summary>
        private void BtnShowSingle_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedImages.Count == 0)
            {
                MessageBox.Show("请先加载图片", "提示");
                return;
            }

            try
            {
                var img = _loadedImages[0];
                _currentCanvasWidth = img.Width;
                _currentCanvasHeight = img.Height;
                _currentPlacements = new List<ImagePlacement>();
                _renderMode = RenderMode.SingleImage;
                _sharedRenderTarget = null;

                InteropImage.SetPixelSize(img.Width, img.Height);
                InteropImage.RequestRender();

                UpdateStatus($"显示单图: {img.Width}×{img.Height} - {System.IO.Path.GetFileName(img.FilePath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"显示失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>清除所有 GPU 图片</summary>
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var img in _loadedImages)
                img.Dispose();
            _loadedImages.Clear();

            _renderMode = RenderMode.None;
            BtnStitch.IsEnabled = false;
            BtnRecommend.IsEnabled = false;
            BtnShowSingle.IsEnabled = false;
            UpdateStatus("已清除所有图片");
        }

        /// <summary>窗口关闭：按依赖顺序释放 GPU 资源</summary>
        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            foreach (var img in _loadedImages)
                img.Dispose();
            _loadedImages.Clear();

            // 释放共享渲染目标（由 DeviceManager 管理的会在 Dispose 中释放）
            _sharedRenderTarget = null;

            _stitcher?.Dispose();
            _registration?.Dispose();
            _imageLoader?.Dispose();
            _deviceManager?.Dispose();
        }
        // ===== 渲染回调（核心） =====

        /// <summary>
        /// D3D11Image 的渲染回调（在 WPF 渲染线程调用）
        /// 
        /// 官方模式（OpenSharedResource）：
        /// 1. isNewSurface=true → 通过 SharedHandle 在我们设备上打开共享纹理
        /// 2. CopyResource / Dispatch 写入共享纹理（全部操作在我们的设备上）
        /// 3. Flush 确保 GPU 命令完成
        /// 
        /// 不直接操作 surface 指针（那是 D3D11Image 内部设备上的资源）。
        /// </summary>
        private void RenderFrame(IntPtr surface, bool isNewSurface)
        {
            if (surface == IntPtr.Zero || !_gpuInitialized)
                return;

            // 没有内容要渲染时直接返回，不打开共享纹理
            // （避免初始 64×64 空白渲染时触发不必要的 COM 调用）
            if (_renderMode == RenderMode.None)
                return;

            try
            {
                // 当 surface 变化时（首次 / 尺寸变化），重新打开共享纹理
                if (isNewSurface ||
                    _sharedRenderTarget == null ||
                    _sharedRenderTarget.Description.Width != _currentCanvasWidth ||
                    _sharedRenderTarget.Description.Height != _currentCanvasHeight)
                {
                    _sharedRenderTarget = _deviceManager!.OpenSharedSurface(surface);
                }

                if (_sharedRenderTarget == null)
                    return;

                switch (_renderMode)
                {
                    case RenderMode.SingleImage:
                        // 直接拷贝源纹理到共享渲染目标（尺寸必须匹配，通过 SetPixelSize 保证）
                        _deviceManager!.Context.CopyResource(_sharedRenderTarget, _loadedImages[0].Texture);
                        break;

                    case RenderMode.Stitch:
                        _stitcher!.Stitch(
                            _loadedImages,
                            _currentPlacements,
                            _currentCanvasWidth,
                            _currentCanvasHeight,
                            _sharedRenderTarget);
                        break;
                }

                // Flush 确保 GPU 命令执行完毕后 D3D11Image 才读取共享纹理
                _deviceManager!.Context.Flush();
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateStatus($"渲染失败: {ex.Message}");
                }));
                System.Diagnostics.Debug.WriteLine($"RenderFrame 异常: {ex.Message}");
            }
        }

        private void UpdateStatus(string message)
        {
            TxtStatus.Text = message;
        }

        private void ApplyRecommendedParameters(bool showStatus)
        {
            var recommendation = StitchParameterAdvisor.Recommend(_loadedImages);
            if (recommendation.OverlapPixels <= 0)
            {
                if (showStatus)
                    UpdateStatus($"已加载 {_loadedImages.Count} 张图片到 GPU，但未能得到有效推荐");
                return;
            }

            SliderOverlap.Value = ClampForSlider(
                recommendation.OverlapPixels,
                SliderOverlap.Minimum,
                SliderOverlap.Maximum);

            SliderBlendWidth.Value = ClampForSlider(
                recommendation.BlendWidth,
                SliderBlendWidth.Minimum,
                SliderBlendWidth.Maximum);

            if (showStatus)
            {
                string confidence = recommendation.Confidence.ToString("F3", CultureInfo.InvariantCulture);
                UpdateStatus(
                    $"已加载 {_loadedImages.Count} 张图片，推荐参数：重叠 {recommendation.OverlapPixels}px，" +
                    $"混合 {recommendation.BlendWidth}px，推荐置信 {confidence}，评估 {recommendation.EvaluatedPairCount} 对");
            }
        }

        private static double ClampForSlider(double value, double min, double max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private static PreviewLayout PreparePreviewLayout(
            RegistrationLayout layout,
            float blendWidth)
        {
            float scale = Math.Min(
                1.0f,
                Math.Min(
                    MaxPreviewTextureSize / (float)layout.CanvasWidth,
                    MaxPreviewTextureSize / (float)layout.CanvasHeight));

            if (scale >= 0.999f)
            {
                return new PreviewLayout(
                    layout.CanvasWidth,
                    layout.CanvasHeight,
                    layout.Placements,
                    blendWidth,
                    1.0f);
            }

            var scaledPlacements = new List<ImagePlacement>(layout.Placements.Count);
            for (int i = 0; i < layout.Placements.Count; i++)
            {
                var placement = layout.Placements[i];
                scaledPlacements.Add(new ImagePlacement
                {
                    OffsetX = placement.OffsetX * scale,
                    OffsetY = placement.OffsetY * scale,
                    Width = Math.Max(1.0f, placement.Width * scale),
                    Height = Math.Max(1.0f, placement.Height * scale),
                });
            }

            int scaledWidth = Math.Max(1, (int)Math.Ceiling(layout.CanvasWidth * scale));
            int scaledHeight = Math.Max(1, (int)Math.Ceiling(layout.CanvasHeight * scale));
            float scaledBlend = Math.Max(1.0f, blendWidth * scale);

            return new PreviewLayout(
                scaledWidth,
                scaledHeight,
                scaledPlacements,
                scaledBlend,
                scale);
        }

        private sealed class PreviewLayout
        {
            public PreviewLayout(
                int canvasWidth,
                int canvasHeight,
                List<ImagePlacement> placements,
                float blendWidth,
                float scale)
            {
                CanvasWidth = canvasWidth;
                CanvasHeight = canvasHeight;
                Placements = placements;
                BlendWidth = blendWidth;
                Scale = scale;
            }

            public int CanvasWidth { get; }
            public int CanvasHeight { get; }
            public List<ImagePlacement> Placements { get; }
            public float BlendWidth { get; }
            public float Scale { get; }
        }

        private enum RenderMode
        {
            None,
            SingleImage,
            Stitch,
        }
    }
}
