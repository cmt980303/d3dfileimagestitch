using GPUStitch.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
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
        private D3DDeviceManager? _deviceManager;
        private GpuImageLoader? _imageLoader;
        private GpuStitcher? _stitcher;
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

                // [暂时禁用] 拼图器初始化（包含 HLSL 编译），先验证单图显示
                // _stitcher = new GpuStitcher(_deviceManager);
                // _stitcher.Initialize();

                _gpuInitialized = true;

                // 设置 D3D11Image 回调
                var hwnd = new WindowInteropHelper(this).Handle;
                InteropImage.WindowOwner = hwnd;
                InteropImage.OnRender = RenderFrame;

                //// 设置初始尺寸并触发首次渲染
                //InteropImage.SetPixelSize(64, 64);
                //InteropImage.RequestRender();

                UpdateStatus("GPU 初始化成功（自有设备 + SharedHandle 模式），就绪");
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
                BtnShowSingle.IsEnabled = _loadedImages.Count >= 1;
                UpdateStatus($"已加载 {_loadedImages.Count} 张图片到 GPU");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"图片加载失败:\n{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>GPU 拼图（暂时禁用，待单图显示验证通过后启用）</summary>
        private void BtnStitch_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("GPU 拼图功能暂时禁用，请先验证单图显示是否正常", "提示");
            // [暂时禁用] 拼图功能
            // if (_loadedImages.Count < 2)
            // {
            //     MessageBox.Show("请至少加载 2 张图片", "提示");
            //     return;
            // }
            //
            // try
            // {
            //     int overlapPixels = (int)SliderOverlap.Value;
            //     float blendWidth = (float)SliderBlendWidth.Value;
            //
            //     var (canvasWidth, canvasHeight, placements) =
            //         GpuStitcher.ComputeHorizontalLayout(_loadedImages, overlapPixels);
            //
            //     if (canvasWidth <= 0 || canvasHeight <= 0)
            //     {
            //         MessageBox.Show("计算画布尺寸失败", "错误");
            //         return;
            //     }
            //
            //     _stitcher!.BlendWidth = blendWidth;
            //     _currentCanvasWidth = canvasWidth;
            //     _currentCanvasHeight = canvasHeight;
            //     _currentPlacements = placements;
            //     _renderMode = RenderMode.Stitch;
            //
            //     InteropImage.SetPixelSize(canvasWidth, canvasHeight);
            //     InteropImage.RequestRender();
            //
            //     UpdateStatus($"拼图完成: {canvasWidth}×{canvasHeight}, {_loadedImages.Count} 张图片");
            // }
            // catch (Exception ex)
            // {
            //     MessageBox.Show($"GPU 拼图失败:\n{ex.Message}", "错误",
            //         MessageBoxButton.OK, MessageBoxImage.Error);
            // }
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
                if (isNewSurface)
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

                    // [暂时禁用] GPU 拼图功能，待单图显示验证通过后启用
                    // case RenderMode.Stitch:
                    //     _stitcher!.Stitch(
                    //         _loadedImages,
                    //         _currentPlacements,
                    //         _currentCanvasWidth,
                    //         _currentCanvasHeight,
                    //         _sharedRenderTarget);
                    //     break;
                }

                // Flush 确保 GPU 命令执行完毕后 D3D11Image 才读取共享纹理
                _deviceManager!.Context.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RenderFrame 异常: {ex.Message}");
            }
        }

        private void UpdateStatus(string message)
        {
            TxtStatus.Text = message;
        }

        private enum RenderMode
        {
            None,
            SingleImage,
            Stitch,
        }
    }
}