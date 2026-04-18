using GPUStitch.Core;
using GPUStitch.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice.Direct3D11;

namespace GPUStitch
{
    /// <summary>
    /// 应用程序主窗口。
    ///
    /// 这个类承担了“用户操作编排层”的角色，本身并不直接实现底层 GPU 算法，
    /// 而是把若干核心模块组织成一条完整的工作流：
    /// 1. 读取文件并探测元数据；
    /// 2. 基于预算生成预览加载方案；
    /// 3. 逐张异步加载图片并触发渐进式预览；
    /// 4. 在用户需要时调用 GPU 配准器求全局布局；
    /// 5. 再把布局交给 GPU 拼图器输出最终预览。
    ///
    /// 之所以把这些流程放在窗口层统一调度，是因为它们和 UI 状态、
    /// 用户交互时机、D3D11Image 刷新节奏是强相关的。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 预览纹理单边最大尺寸。
        /// 这是对 WPF/D3D 预览链路的保护值，避免在超大画布下直接申请过大的共享纹理。
        /// </summary>
        private const int MaxPreviewTextureSize = 8192;

        /// <summary>
        /// 预览画布的估算字节预算。
        /// 这里按“累积纹理 + 输出纹理 + 额外余量”做保守估算，所以不是简单的 4 字节/像素。
        /// </summary>
        private const long MaxPreviewCanvasBytes = 384L * 1024L * 1024L;

        /// <summary>
        /// 源图纹理预算。
        /// 它控制的是“导入后每张图片作为 GPU 纹理常驻”这一部分开销，而不是输出画布开销。
        /// </summary>
        private const long SourceTextureBudgetBytes = 512L * 1024L * 1024L;

        /// <summary>
        /// 单张图片在预览模式下允许的最大边长。
        /// 这个限制主要用于避免个别超大原图在预算还没超标前就把单张纹理撑得太大。
        /// </summary>
        private const int MaxPerImagePreviewDimension = 3072;

        /// <summary>
        /// 同时并发解码/上传的图片数量。
        /// 这里故意保持较低并发，目的是降低磁盘、CPU 解码和显存上传同时峰值。
        /// </summary>
        private const int MaxParallelLoads = 2;

        // ===== GPU 核心模块 =====
        private D3DDeviceManager? _deviceManager;
        private GpuImageLoader? _imageLoader;
        private GpuStitcher? _stitcher;
        private GpuRegistration? _registration;

        // ===== 当前会话中的图片与索引状态 =====
        private readonly List<ImageFileMetadata> _loadedMetadata = new List<ImageFileMetadata>();
        private readonly List<GpuImage?> _imageSlots = new List<GpuImage?>();
        private readonly List<GridImageCoordinate?> _metadataCoordinates = new List<GridImageCoordinate?>();
        private readonly Dictionary<GridImageCoordinate, int> _indexByCoordinate =
            new Dictionary<GridImageCoordinate, int>();

        // ===== 导入状态 =====
        private ImageLoadPlan _currentLoadPlan = new ImageLoadPlan();
        private CancellationTokenSource? _loadCts;
        private bool _isLoading;

        // ===== D3D11Image 共享表面状态 =====
        private bool _gpuInitialized;
        private ID3D11Texture2D? _sharedRenderTarget;

        // ===== 渐进式累加状态 =====
        private readonly HashSet<int> _accumulatedIndices = new HashSet<int>();
        private bool _canvasPrepared;

        // ===== 当前显示内容的布局状态 =====
        private RenderMode _renderMode = RenderMode.None;
        private List<ImagePlacement> _currentPlacements = new List<ImagePlacement>();
        private int _currentCanvasWidth;
        private int _currentCanvasHeight;

        public MainWindow()
        {
            InitializeComponent();

            // 滑块值展示单独放在这里绑定，避免在 XAML 中再引入额外转换器。
            SliderBlendWidth.ValueChanged += (s, e) =>
                TxtBlendValue.Text = ((int)SliderBlendWidth.Value).ToString();
            SliderOverlap.ValueChanged += (s, e) =>
                TxtOverlapValue.Text = ((int)SliderOverlap.Value).ToString();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 先初始化所有 GPU 服务对象，再把 D3D11Image 的回调接进来。
                // 这样一旦 WPF 触发首次渲染，底层资源已经全部就绪。
                _deviceManager = new D3DDeviceManager();
                _deviceManager.Initialize();

                _imageLoader = new GpuImageLoader(_deviceManager);
                _stitcher = new GpuStitcher(_deviceManager);
                _stitcher.Initialize();
                _registration = new GpuRegistration(_deviceManager);
                _registration.Initialize();

                _gpuInitialized = true;

                var hwnd = new WindowInteropHelper(this).Handle;
                InteropImage.WindowOwner = hwnd;
                InteropImage.OnRender = RenderFrame;

                UpdateStatus("GPU 初始化成功，支持渐进式导入预览");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"GPU 初始化失败:\n{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 导入流程：
        /// 1. 先探测元数据和预算；
        /// 2. 先建立一个预览画布；
        /// 3. 再异步把图像逐张贴进画布；
        /// 4. 全部导入完毕后，用户可以再点击“配准 + GPU 拼图”做精配准。
        /// </summary>
        private async void BtnLoadImages_Click(object sender, RoutedEventArgs e)
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
                // 新一轮导入前先中止旧任务，避免两批图片交叉写入同一套状态。
                CancelCurrentLoad();

                SetLoadingUiState(isLoading: true);
                UpdateStatus("正在分析图片元数据...");

                // 元数据探测在后台执行：这里只需要宽高和路径，不需要立刻解码整图。
                var combinedMetadata = await Task.Run(() => BuildCombinedMetadata(dlg.FileNames));
                SortMetadataForStreaming(combinedMetadata);

                // 先做预算，再决定真正的导入比例。
                var loadPlan = ImageLoadPlanner.Build(
                    combinedMetadata,
                    SourceTextureBudgetBytes,
                    MaxPerImagePreviewDimension);

                // 提前生成预览布局，用户会先看到一块待填充的画布。
                PrepareProgressivePreview(combinedMetadata, loadPlan);

                _loadCts = new CancellationTokenSource();
                await LoadImagesProgressivelyAsync(combinedMetadata, loadPlan, _loadCts.Token);

                if (_loadCts.IsCancellationRequested)
                    return;

                ApplyRecommendedParameters(showStatus: false);
                BtnStitch.IsEnabled = GetLoadedImagesInOrder().Count >= 2;
                BtnRecommend.IsEnabled = GetLoadedImagesInOrder().Count >= 2;
                BtnShowSingle.IsEnabled = GetLoadedImagesInOrder().Count >= 1;

                string sourceUsage = FormatMiB(loadPlan.PlannedSourceBytes);
                string sourceRaw = FormatMiB(loadPlan.RawSourceBytes);
                string scaleSuffix = loadPlan.IsDownsampled
                    ? $"，按 {loadPlan.Scale:F3}x 预算缩放"
                    : string.Empty;

                UpdateStatus(
                    $"导入完成：{GetLoadedCount()}/{_loadedMetadata.Count} 张{scaleSuffix}，源纹理预算 {sourceUsage}/{sourceRaw}");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("已取消导入");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"图片加载失败:\n{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetLoadingUiState(isLoading: false);
            }
        }

        /// <summary>
        /// 最终精配准。
        /// 当前会基于“已经完成导入”的缩放图像做 GPU 配准，再刷新显示布局。
        /// </summary>
        private async void BtnStitch_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
            {
                MessageBox.Show("当前仍在导入图片，请等待导入完成后再执行精配准。", "提示");
                return;
            }

            var images = GetLoadedImagesInOrder();
            if (images.Count < 2)
            {
                MessageBox.Show("请至少加载 2 张图片", "提示");
                return;
            }

            try
            {
                // 精配准期间锁住关键按钮，防止用户在布局更新中途再次触发导入或拼图。
                BtnStitch.IsEnabled = false;
                BtnRecommend.IsEnabled = false;
                BtnLoadImages.IsEnabled = false;
                UpdateStatus("正在执行 GPU 配准...");

                int overlapPixels = (int)SliderOverlap.Value;
                float blendWidth = (float)SliderBlendWidth.Value;

                var registrationOptions =
                    RegistrationOptions.CreateForImages(images, overlapPixels);

                // 配准是纯计算任务，放到后台线程执行，避免阻塞 UI 线程。
                var layout = await Task.Run(() =>
                    _registration!.ComputeLayout(images, registrationOptions));

                if (layout.CanvasWidth <= 0 || layout.CanvasHeight <= 0)
                {
                    MessageBox.Show("计算画布尺寸失败", "错误");
                    return;
                }

                // 注意：这里得到的是“真实配准布局”；
                // 但为了能稳定显示在 WPF 中，还需要经过一次预览预算缩放和整数像素对齐。
                var preview = PreparePreviewLayout(layout, blendWidth);
                ApplyFeatherHints(preview.Placements, preview.BlendWidth);

                _stitcher!.BlendWidth = preview.BlendWidth;
                _currentCanvasWidth = preview.CanvasWidth;
                _currentCanvasHeight = preview.CanvasHeight;
                _currentPlacements = preview.Placements;
                _renderMode = RenderMode.Stitch;
                _sharedRenderTarget = null;
                _canvasPrepared = false;
                _accumulatedIndices.Clear();

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
                MessageBox.Show(
                    $"GPU 配准/拼图失败:\n{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetLoadingUiState(isLoading: false);
                BtnStitch.IsEnabled = GetLoadedImagesInOrder().Count >= 2;
                BtnRecommend.IsEnabled = GetLoadedImagesInOrder().Count >= 2;
                BtnLoadImages.IsEnabled = true;
            }
        }

        private void BtnRecommend_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
            {
                MessageBox.Show("导入过程中暂不建议重新估计参数，请等待导入完成。", "提示");
                return;
            }

            if (GetLoadedImagesInOrder().Count < 2)
            {
                MessageBox.Show("请至少加载 2 张图片", "提示");
                return;
            }

            ApplyRecommendedParameters(showStatus: true);
        }

        /// <summary>
        /// 直接显示第一张已加载图片。
        /// 该模式主要用于快速核对原图内容、纹理清晰度和当前导入比例是否符合预期。
        /// </summary>
        private void BtnShowSingle_Click(object sender, RoutedEventArgs e)
        {
            var image = GetFirstLoadedImage();
            if (image == null)
            {
                MessageBox.Show("请先加载图片", "提示");
                return;
            }

            try
            {
                // 单图模式下不需要 placement，也不需要累积画布。
                _currentCanvasWidth = image.Width;
                _currentCanvasHeight = image.Height;
                _currentPlacements = new List<ImagePlacement>();
                _renderMode = RenderMode.SingleImage;
                _sharedRenderTarget = null;
                _canvasPrepared = false;
                _accumulatedIndices.Clear();

                InteropImage.SetPixelSize(image.Width, image.Height);
                InteropImage.RequestRender();

                UpdateStatus($"显示单图: {image.Width}×{image.Height} - {Path.GetFileName(image.FilePath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"显示失败:\n{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            // 清空操作既要终止后台导入，也要释放已上传的 GPU 纹理。
            CancelCurrentLoad();
            ClearAllImages();

            _renderMode = RenderMode.None;
            _sharedRenderTarget = null;
            BtnStitch.IsEnabled = false;
            BtnRecommend.IsEnabled = false;
            BtnShowSingle.IsEnabled = false;
            BtnLoadImages.IsEnabled = true;
            UpdateStatus("已清除所有图片");
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // 关闭窗口时按“高层状态 -> GPU 资源”顺序回收，避免回调仍访问已释放对象。
            CancelCurrentLoad();
            ClearAllImages();

            _sharedRenderTarget = null;
            _stitcher?.Dispose();
            _registration?.Dispose();
            _imageLoader?.Dispose();
            _deviceManager?.Dispose();
        }

        /// <summary>
        /// D3D11Image 每一帧都会回调这里。
        ///
        /// 这是整个预览链路的关键枢纽：
        /// - 单图模式下直接 CopyResource；
        /// - 拼图模式下走“增量累加 + 最终归一化”两阶段流程。
        ///
        /// 这个方法必须尽量短、尽量确定，因为它运行在 WPF 合成相关的渲染节奏中，
        /// 一旦这里做了过多重复工作，就会直接表现为界面卡顿。
        /// </summary>
        private void RenderFrame(IntPtr surface, bool isNewSurface)
        {
            if (surface == IntPtr.Zero || !_gpuInitialized)
                return;

            if (_renderMode == RenderMode.None)
                return;

            try
            {
                // D3D11Image 的底层共享表面可能在尺寸变化或表面重建时失效，
                // 因此每次都要校验它是否仍与当前画布尺寸匹配。
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
                    {
                        var image = GetFirstLoadedImage();
                        if (image != null)
                        {
                            // 单图显示是最便宜的路径：直接把源纹理复制到共享表面即可。
                            _deviceManager!.Context.CopyResource(_sharedRenderTarget, image.Texture);
                        }
                        break;
                    }
                    case RenderMode.Stitch:
                    {
                        // 第一次进入拼图模式时才真正创建并清空累积画布。
                        // 后续帧重用它，从而避免“每帧重新从第一张图拼到最后一张图”。
                        if (!_canvasPrepared)
                        {
                            _stitcher!.PrepareCanvas(_currentCanvasWidth, _currentCanvasHeight);
                            _accumulatedIndices.Clear();
                            _canvasPrepared = true;
                        }

                        // 只把“本轮新到达”的图像累加进去。
                        // 这正是渐进式导入预览不卡的核心原因。
                        int count = Math.Min(_imageSlots.Count, _currentPlacements.Count);
                        for (int i = 0; i < count; i++)
                        {
                            if (_imageSlots[i] != null && !_accumulatedIndices.Contains(i))
                            {
                                _stitcher!.AccumulateImage(_imageSlots[i]!, _currentPlacements[i]);
                                _accumulatedIndices.Add(i);
                            }
                        }

                        if (_accumulatedIndices.Count > 0)
                        {
                            // Finalize 的代价远低于重新 Accumulate 全部图像，
                            // 所以每次有新增内容时都做一次归一化输出是可接受的。
                            _stitcher!.FinalizeAndCopy(
                                _currentCanvasWidth, _currentCanvasHeight, _sharedRenderTarget);
                        }
                        break;
                    }
                }

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

        /// <summary>
        /// 按“有限并发 + 按完成顺序提交”的方式逐张加载图片。
        ///
        /// 这里刻意没有使用“全部任务一次性启动”的写法：
        /// 因为对于大量显微图片来说，解码、上传和纹理常驻都会带来较高瞬时压力，
        /// 用小并发窗口可以显著降低卡顿和资源峰值。
        /// </summary>
        private async Task LoadImagesProgressivelyAsync(
            IReadOnlyList<ImageFileMetadata> metadata,
            ImageLoadPlan loadPlan,
            CancellationToken cancellationToken)
        {
            var pending = new List<Task<LoadedImageResult>>();
            int nextIndex = 0;

            while (nextIndex < metadata.Count || pending.Count > 0)
            {
                // 维持一个固定大小的“在途任务池”。
                while (pending.Count < MaxParallelLoads && nextIndex < metadata.Count)
                {
                    int index = nextIndex++;
                    pending.Add(LoadSingleImageAsync(metadata[index], index, loadPlan.Scale, cancellationToken));
                }

                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);

                LoadedImageResult result = await finished;
                if (cancellationToken.IsCancellationRequested)
                {
                    // 如果取消发生在图片已经上传之后，需要显式释放这张中间产物。
                    result.Image.Dispose();
                    break;
                }

                _imageSlots[result.Index] = result.Image;
                int loadedCount = GetLoadedCount();

                if (loadedCount == 1)
                {
                    // 第一张图到达时就允许进入 Stitch 预览模式，
                    // 后面每来一张都会继续往同一个累积画布里贴。
                    _renderMode = RenderMode.Stitch;
                    _sharedRenderTarget = null;
                }

                BtnShowSingle.IsEnabled = loadedCount >= 1;
                InteropImage.RequestRender();

                UpdateStatus(
                    $"正在导入并贴图: {loadedCount}/{metadata.Count}，" +
                    $"缩放 {loadPlan.Scale:F3}x");

                // 主动把机会让回 Dispatcher，使状态文本和画面刷新能及时显示出来。
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// 在后台线程中解码图片并上传到 GPU。
        /// 返回结果同时带回原始槽位索引，这样完成顺序与输入顺序可以解耦。
        /// </summary>
        private Task<LoadedImageResult> LoadSingleImageAsync(
            ImageFileMetadata metadata,
            int index,
            float scale,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var image = _imageLoader!.LoadFromFile(metadata.FilePath, scale);
                image.FilePath = metadata.FilePath;

                cancellationToken.ThrowIfCancellationRequested();
                return new LoadedImageResult(index, image);
            }, cancellationToken);
        }

        private List<ImageFileMetadata> BuildCombinedMetadata(IReadOnlyList<string> newFilePaths)
        {
            // 允许“继续追加导入”，但要对路径去重，避免同一张图被重复加入会话。
            var combinedMetadata = new List<ImageFileMetadata>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _loadedMetadata.Count; i++)
            {
                var item = _loadedMetadata[i];
                if (seenPaths.Add(item.FilePath))
                {
                    combinedMetadata.Add(item);
                }
            }

            for (int i = 0; i < newFilePaths.Count; i++)
            {
                string filePath = newFilePaths[i];
                if (seenPaths.Add(filePath))
                {
                    combinedMetadata.Add(GpuImageLoader.ProbeFile(filePath));
                }
            }

            return combinedMetadata;
        }

        /// <summary>
        /// 若所有文件名都能解析成行列号，则按“行优先、列次之”排序。
        /// 这样渐进式导入时，用户看到的贴图顺序会更接近显微镜实际采集顺序。
        /// </summary>
        private void SortMetadataForStreaming(List<ImageFileMetadata> metadata)
        {
            bool allGridNamed = true;
            for (int i = 0; i < metadata.Count; i++)
            {
                if (!GridImageCoordinate.TryParseFromFilePath(metadata[i].FilePath, out _))
                {
                    allGridNamed = false;
                    break;
                }
            }

            if (!allGridNamed)
                return;

            metadata.Sort((left, right) =>
            {
                GridImageCoordinate.TryParseFromFilePath(left.FilePath, out var leftCoord);
                GridImageCoordinate.TryParseFromFilePath(right.FilePath, out var rightCoord);

                int rowCompare = leftCoord.Row.CompareTo(rightCoord.Row);
                return rowCompare != 0
                    ? rowCompare
                    : leftCoord.Column.CompareTo(rightCoord.Column);
            });
        }

        /// <summary>
        /// 为新一轮导入建立“空画布 + 空槽位 + 初始布局”。
        ///
        /// 这个阶段并不会真正解码图片，只会准备：
        /// - 会话级图片槽位；
        /// - 网格索引；
        /// - 预览布局；
        /// - 当前拼图器参数。
        /// </summary>
        private void PrepareProgressivePreview(
            IReadOnlyList<ImageFileMetadata> metadata,
            ImageLoadPlan loadPlan)
        {
            ClearAllImages();

            for (int i = 0; i < metadata.Count; i++)
            {
                _loadedMetadata.Add(metadata[i]);
                _imageSlots.Add(null);

                if (GridImageCoordinate.TryParseFromFilePath(metadata[i].FilePath, out var coordinate))
                {
                    _metadataCoordinates.Add(coordinate);
                    _indexByCoordinate[coordinate] = i;
                }
                else
                {
                    _metadataCoordinates.Add(null);
                }
            }

            _currentLoadPlan = loadPlan;

            int overlapPixels = Math.Max(16, (int)SliderOverlap.Value);
            float blendWidth = (float)SliderBlendWidth.Value;

            // 先用命名规则/默认重叠构建“粗布局”，
            // 后续用户点击配准时，再替换为真正由内容相关性求出的精布局。
            var provisionalLayout = BuildProvisionalLayout(_loadedMetadata, loadPlan.Scale, overlapPixels);
            var preview = PreparePreviewLayout(provisionalLayout, blendWidth);
            ApplyFeatherHints(preview.Placements, preview.BlendWidth);

            _stitcher!.BlendWidth = preview.BlendWidth;
            _currentCanvasWidth = preview.CanvasWidth;
            _currentCanvasHeight = preview.CanvasHeight;
            _currentPlacements = preview.Placements;
            _renderMode = RenderMode.None;
            _sharedRenderTarget = null;
            _canvasPrepared = false;
            _accumulatedIndices.Clear();

            InteropImage.SetPixelSize(preview.CanvasWidth, preview.CanvasHeight);
            InteropImage.RequestRender();
        }

        /// <summary>
        /// 根据当前元数据生成一个“足够可视化”的初始布局。
        /// 若能识别规则网格则按二维排布，否则退化为单行拼接。
        /// </summary>
        private RegistrationLayout BuildProvisionalLayout(
            IReadOnlyList<ImageFileMetadata> metadata,
            float imageScale,
            int overlapPixels)
        {
            if (TryBuildGridProvisionalLayout(metadata, imageScale, overlapPixels, out var gridLayout))
                return gridLayout;

            return BuildSequentialProvisionalLayout(metadata, imageScale, overlapPixels);
        }

        /// <summary>
        /// 通过文件名中的网格坐标快速构建一个二维预布局。
        ///
        /// 这一步不做图像内容配准，只使用：
        /// - 每一行的最大高度；
        /// - 每一列的最大宽度；
        /// - 用户给定的预估重叠像素。
        ///
        /// 它的意义是让渐进式预览从第一张图开始就有“落点”，
        /// 而不必等待全部图片都加载并完成精配准。
        /// </summary>
        private bool TryBuildGridProvisionalLayout(
            IReadOnlyList<ImageFileMetadata> metadata,
            float imageScale,
            int overlapPixels,
            out RegistrationLayout layout)
        {
            layout = RegistrationLayout.Empty;

            var rowHeights = new Dictionary<int, float>();
            var columnWidths = new Dictionary<int, float>();
            var rows = new SortedSet<int>();
            var columns = new SortedSet<int>();
            var coordinates = new GridImageCoordinate[metadata.Count];

            for (int i = 0; i < metadata.Count; i++)
            {
                if (!GridImageCoordinate.TryParseFromFilePath(metadata[i].FilePath, out var coordinate))
                    return false;

                coordinates[i] = coordinate;
                rows.Add(coordinate.Row);
                columns.Add(coordinate.Column);

                float width = Math.Max(1.0f, metadata[i].PixelWidth * imageScale);
                float height = Math.Max(1.0f, metadata[i].PixelHeight * imageScale);

                rowHeights[coordinate.Row] = rowHeights.TryGetValue(coordinate.Row, out float existingRow)
                    ? Math.Max(existingRow, height)
                    : height;

                columnWidths[coordinate.Column] = columnWidths.TryGetValue(coordinate.Column, out float existingColumn)
                    ? Math.Max(existingColumn, width)
                    : width;
            }

            // 重叠像素同样要按预览比例缩放，才能保持预布局的几何关系一致。
            float scaledOverlap = Math.Max(1.0f, overlapPixels * imageScale);

            var rowOffsets = new Dictionary<int, float>();
            var columnOffsets = new Dictionary<int, float>();

            // 行偏移和列偏移分别独立累加：
            // 每推进一格，都以前一行/列的尺寸减去重叠量作为步长。
            float currentY = 0;
            int? previousRow = null;
            foreach (int row in rows)
            {
                if (previousRow.HasValue)
                {
                    currentY += Math.Max(1.0f, rowHeights[previousRow.Value] - scaledOverlap);
                }

                rowOffsets[row] = currentY;
                previousRow = row;
            }

            float currentX = 0;
            int? previousColumn = null;
            foreach (int column in columns)
            {
                if (previousColumn.HasValue)
                {
                    currentX += Math.Max(1.0f, columnWidths[previousColumn.Value] - scaledOverlap);
                }

                columnOffsets[column] = currentX;
                previousColumn = column;
            }

            var placements = new List<ImagePlacement>(metadata.Count);
            float maxX = 0;
            float maxY = 0;

            for (int i = 0; i < metadata.Count; i++)
            {
                float width = Math.Max(1.0f, metadata[i].PixelWidth * imageScale);
                float height = Math.Max(1.0f, metadata[i].PixelHeight * imageScale);
                float offsetX = columnOffsets[coordinates[i].Column];
                float offsetY = rowOffsets[coordinates[i].Row];

                placements.Add(new ImagePlacement
                {
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                    Width = width,
                    Height = height,
                });

                maxX = Math.Max(maxX, offsetX + width);
                maxY = Math.Max(maxY, offsetY + height);
            }

            layout = new RegistrationLayout(
                placements,
                (int)Math.Ceiling(maxX),
                (int)Math.Ceiling(maxY),
                new List<PairRegistrationResult>());

            return true;
        }

        /// <summary>
        /// 退化布局：当文件名无法解析出二维网格时，按单行顺序排列。
        /// 这保证了即便没有显式行列信息，系统仍能给出一个可视化预览。
        /// </summary>
        private static RegistrationLayout BuildSequentialProvisionalLayout(
            IReadOnlyList<ImageFileMetadata> metadata,
            float imageScale,
            int overlapPixels)
        {
            var placements = new List<ImagePlacement>(metadata.Count);
            float currentX = 0;
            float maxHeight = 0;
            float scaledOverlap = Math.Max(1.0f, overlapPixels * imageScale);

            for (int i = 0; i < metadata.Count; i++)
            {
                float width = Math.Max(1.0f, metadata[i].PixelWidth * imageScale);
                float height = Math.Max(1.0f, metadata[i].PixelHeight * imageScale);

                placements.Add(new ImagePlacement
                {
                    OffsetX = currentX,
                    OffsetY = 0,
                    Width = width,
                    Height = height,
                });

                currentX += Math.Max(1.0f, width - scaledOverlap);
                maxHeight = Math.Max(maxHeight, height);
            }

            int canvasWidth = metadata.Count == 0
                ? 0
                : (int)Math.Ceiling(currentX + scaledOverlap);

            return new RegistrationLayout(
                placements,
                canvasWidth,
                (int)Math.Ceiling(maxHeight),
                new List<PairRegistrationResult>());
        }

        /// <summary>
        /// 根据当前已加载情况修正每张图实际参与羽化的边。
        /// 这个辅助结果主要用于“邻居尚未加载完全”的渐进式阶段。
        /// </summary>
        private RenderSnapshot BuildRenderSnapshot()
        {
            var images = new List<GpuImage>();
            var placements = new List<ImagePlacement>();

            int count = Math.Min(_imageSlots.Count, _currentPlacements.Count);
            bool hasGrid = _indexByCoordinate.Count == _loadedMetadata.Count && _loadedMetadata.Count > 0;

            for (int i = 0; i < count; i++)
            {
                var image = _imageSlots[i];
                if (image == null)
                    continue;

                var placement = _currentPlacements[i];

                if (hasGrid && _metadataCoordinates[i].HasValue)
                {
                    var coordinate = _metadataCoordinates[i]!.Value;
                    placement.FeatherLeft = IsImageLoadedAt(coordinate.Row, coordinate.Column - 1) ? placement.FeatherLeft : 0.0f;
                    placement.FeatherRight = IsImageLoadedAt(coordinate.Row, coordinate.Column + 1) ? placement.FeatherRight : 0.0f;
                    placement.FeatherTop = IsImageLoadedAt(coordinate.Row - 1, coordinate.Column) ? placement.FeatherTop : 0.0f;
                    placement.FeatherBottom = IsImageLoadedAt(coordinate.Row + 1, coordinate.Column) ? placement.FeatherBottom : 0.0f;
                }
                else
                {
                    placement.FeatherLeft = i > 0 && _imageSlots[i - 1] != null ? placement.FeatherLeft : 0.0f;
                    placement.FeatherRight = i < count - 1 && _imageSlots[i + 1] != null ? placement.FeatherRight : 0.0f;
                    placement.FeatherTop = 0.0f;
                    placement.FeatherBottom = 0.0f;
                }

                images.Add(image);
                placements.Add(placement);
            }

            return new RenderSnapshot(images, placements);
        }

        /// <summary>
        /// 判断指定网格坐标处的图片是否已经真正加载完成。
        /// </summary>
        private bool IsImageLoadedAt(int row, int column)
        {
            if (!_indexByCoordinate.TryGetValue(new GridImageCoordinate(row, column), out int index))
                return false;

            return index >= 0 && index < _imageSlots.Count && _imageSlots[index] != null;
        }

        /// <summary>
        /// 给每张图写入羽化提示。
        ///
        /// 这里区分“有无邻居”而不是“是否一定存在重叠像素”，
        /// 因为当前阶段的目标是先生成一个稳定、直观的默认混合策略。
        /// </summary>
        private void ApplyFeatherHints(List<ImagePlacement> placements, float blendWidth)
        {
            if (placements.Count == 0 || blendWidth <= 0.0f)
                return;

            bool hasGrid = _indexByCoordinate.Count == _loadedMetadata.Count && _loadedMetadata.Count > 0;

            for (int i = 0; i < placements.Count; i++)
            {
                var placement = placements[i];

                if (hasGrid && _metadataCoordinates[i].HasValue)
                {
                    var coordinate = _metadataCoordinates[i]!.Value;
                    placement.FeatherLeft = _indexByCoordinate.ContainsKey(new GridImageCoordinate(coordinate.Row, coordinate.Column - 1)) ? blendWidth : 0.0f;
                    placement.FeatherRight = _indexByCoordinate.ContainsKey(new GridImageCoordinate(coordinate.Row, coordinate.Column + 1)) ? blendWidth : 0.0f;
                    placement.FeatherTop = _indexByCoordinate.ContainsKey(new GridImageCoordinate(coordinate.Row - 1, coordinate.Column)) ? blendWidth : 0.0f;
                    placement.FeatherBottom = _indexByCoordinate.ContainsKey(new GridImageCoordinate(coordinate.Row + 1, coordinate.Column)) ? blendWidth : 0.0f;
                }
                else
                {
                    placement.FeatherLeft = i > 0 ? blendWidth : 0.0f;
                    placement.FeatherRight = i < placements.Count - 1 ? blendWidth : 0.0f;
                    placement.FeatherTop = 0.0f;
                    placement.FeatherBottom = 0.0f;
                }

                placements[i] = placement;
            }
        }

        /// <summary>
        /// 基于当前已加载图片自动推荐重叠宽度和混合宽度。
        /// 这一步不会改动布局本身，只更新 UI 滑块的建议值。
        /// </summary>
        private void ApplyRecommendedParameters(bool showStatus)
        {
            var loadedImages = GetLoadedImagesInOrder();
            var recommendation = StitchParameterAdvisor.Recommend(loadedImages);
            if (recommendation.OverlapPixels <= 0)
            {
                if (showStatus)
                {
                    UpdateStatus($"已加载 {loadedImages.Count} 张图片，但未能得到有效推荐");
                }
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
                    $"已加载 {loadedImages.Count} 张图片，推荐参数：重叠 {recommendation.OverlapPixels}px，" +
                    $"混合 {recommendation.BlendWidth}px，推荐置信 {confidence}，评估 {recommendation.EvaluatedPairCount} 对");
            }
        }

        /// <summary>
        /// 以原始导入顺序返回当前已加载完成的图片集合。
        /// 这个顺序对顺序拼接、参数推荐和状态显示都很重要。
        /// </summary>
        private List<GpuImage> GetLoadedImagesInOrder()
        {
            var images = new List<GpuImage>();
            for (int i = 0; i < _imageSlots.Count; i++)
            {
                if (_imageSlots[i] != null)
                {
                    images.Add(_imageSlots[i]!);
                }
            }
            return images;
        }

        /// <summary>
        /// 获取第一张已加载图片。
        /// 用于单图预览以及部分“至少有一张图即可”的 UI 逻辑。
        /// </summary>
        private GpuImage? GetFirstLoadedImage()
        {
            for (int i = 0; i < _imageSlots.Count; i++)
            {
                if (_imageSlots[i] != null)
                    return _imageSlots[i];
            }

            return null;
        }

        /// <summary>
        /// 统计当前已成功加载到 GPU 的图片数量。
        /// </summary>
        private int GetLoadedCount()
        {
            int count = 0;
            for (int i = 0; i < _imageSlots.Count; i++)
            {
                if (_imageSlots[i] != null)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// 清空当前会话的所有图片与布局状态。
        /// 这里会释放所有已上传的 GPU 纹理，是真正意义上的“重置会话”。
        /// </summary>
        private void ClearAllImages()
        {
            for (int i = 0; i < _imageSlots.Count; i++)
            {
                _imageSlots[i]?.Dispose();
            }

            _imageSlots.Clear();
            _loadedMetadata.Clear();
            _metadataCoordinates.Clear();
            _indexByCoordinate.Clear();
            _currentLoadPlan = new ImageLoadPlan();
            _currentPlacements = new List<ImagePlacement>();
            _currentCanvasWidth = 0;
            _currentCanvasHeight = 0;
            _canvasPrepared = false;
            _accumulatedIndices.Clear();
        }

        /// <summary>
        /// 取消正在进行的导入任务。
        /// 该方法既负责发出取消信号，也负责回收 CancellationTokenSource 本身。
        /// </summary>
        private void CancelCurrentLoad()
        {
            if (_loadCts == null)
                return;

            try
            {
                _loadCts.Cancel();
            }
            finally
            {
                _loadCts.Dispose();
                _loadCts = null;
                _isLoading = false;
            }
        }

        /// <summary>
        /// 根据“是否正在导入”统一切换工具栏按钮状态。
        /// </summary>
        private void SetLoadingUiState(bool isLoading)
        {
            _isLoading = isLoading;
            BtnLoadImages.IsEnabled = !isLoading;
            BtnStitch.IsEnabled = !isLoading && GetLoadedImagesInOrder().Count >= 2;
            BtnRecommend.IsEnabled = !isLoading && GetLoadedImagesInOrder().Count >= 2;
            BtnShowSingle.IsEnabled = GetLoadedImagesInOrder().Count >= 1;
        }

        /// <summary>
        /// 更新状态栏文本。
        /// </summary>
        private void UpdateStatus(string message)
        {
            TxtStatus.Text = message;
        }

        /// <summary>
        /// 以 MiB 形式格式化字节数，便于在状态栏中展示预算信息。
        /// </summary>
        private static string FormatMiB(long bytes)
        {
            return $"{(bytes / 1024.0 / 1024.0):F1} MiB";
        }

        /// <summary>
        /// 把推荐值约束到滑块可选范围内。
        /// </summary>
        private static double ClampForSlider(double value, double min, double max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        /// <summary>
        /// 把“原始布局”压缩成“适合 WPF 预览”的布局。
        ///
        /// 这里同时处理三件事：
        /// 1. 受纹理尺寸限制的缩放；
        /// 2. 受总预算限制的缩放；
        /// 3. 为避免缩小时条纹而做的整数像素对齐。
        /// </summary>
        private static PreviewLayout PreparePreviewLayout(
            RegistrationLayout layout,
            float blendWidth)
        {
            float dimensionScale = Math.Min(
                1.0f,
                Math.Min(
                    MaxPreviewTextureSize / (float)layout.CanvasWidth,
                    MaxPreviewTextureSize / (float)layout.CanvasHeight));

            // 20 字节/像素是一个经验性的保守估算：
            // 既覆盖 R32G32B32A32_Float 累积纹理，也留出输出纹理和驱动层额外开销。
            double rawCanvasBytes = (double)layout.CanvasWidth * layout.CanvasHeight * 20.0;
            float byteBudgetScale = 1.0f;
            if (rawCanvasBytes > MaxPreviewCanvasBytes && rawCanvasBytes > 1.0)
            {
                byteBudgetScale = (float)Math.Sqrt(MaxPreviewCanvasBytes / rawCanvasBytes);
            }

            float scale = Math.Min(1.0f, Math.Min(dimensionScale, byteBudgetScale));

            List<ImagePlacement> placements;
            float scaledBlend;

            if (scale >= 0.999f)
            {
                scale = 1.0f;
                placements = new List<ImagePlacement>(layout.Placements);
                scaledBlend = blendWidth;
            }
            else
            {
                placements = new List<ImagePlacement>(layout.Placements.Count);
                for (int i = 0; i < layout.Placements.Count; i++)
                {
                    var p = layout.Placements[i];
                    placements.Add(new ImagePlacement
                    {
                        OffsetX = p.OffsetX * scale,
                        OffsetY = p.OffsetY * scale,
                        Width = p.Width * scale,
                        Height = p.Height * scale,
                        FeatherLeft = p.FeatherLeft * scale,
                        FeatherRight = p.FeatherRight * scale,
                        FeatherTop = p.FeatherTop * scale,
                        FeatherBottom = p.FeatherBottom * scale,
                    });
                }
                scaledBlend = Math.Max(1.0f, blendWidth * scale);
            }

            // 将偏移和尺寸四舍五入到整数像素，消除亚像素间隙导致的条纹。
            // 这是当前版本里非常关键的稳定性处理：如果 placement 落在半像素位置，
            // WPF 再次缩放显示时就更容易看到暗纹和摩尔纹。
            int canvasWidth = 0, canvasHeight = 0;
            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                p.OffsetX = (float)Math.Round(p.OffsetX);
                p.OffsetY = (float)Math.Round(p.OffsetY);
                p.Width = Math.Max(1.0f, (float)Math.Round(p.Width));
                p.Height = Math.Max(1.0f, (float)Math.Round(p.Height));
                placements[i] = p;

                canvasWidth = Math.Max(canvasWidth, (int)(p.OffsetX + p.Width));
                canvasHeight = Math.Max(canvasHeight, (int)(p.OffsetY + p.Height));
            }

            canvasWidth = Math.Max(1, canvasWidth);
            canvasHeight = Math.Max(1, canvasHeight);

            return new PreviewLayout(
                canvasWidth,
                canvasHeight,
                placements,
                scaledBlend,
                scale);
        }

        /// <summary>
        /// 预览布局打包对象。
        /// 用于一次性返回缩放后的画布尺寸、placements、混合宽度和最终缩放因子。
        /// </summary>
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

        /// <summary>
        /// 单张图片异步加载完成后的回传结果。
        /// </summary>
        private sealed class LoadedImageResult
        {
            public LoadedImageResult(int index, GpuImage image)
            {
                Index = index;
                Image = image;
            }

            public int Index { get; }
            public GpuImage Image { get; }
        }

        /// <summary>
        /// 渲染快照。
        /// 当前主要作为“带羽化修正后的已加载图片集合”容器保留，便于后续扩展。
        /// </summary>
        private sealed class RenderSnapshot
        {
            public RenderSnapshot(List<GpuImage> images, List<ImagePlacement> placements)
            {
                Images = images;
                Placements = placements;
            }

            public List<GpuImage> Images { get; }
            public List<ImagePlacement> Placements { get; }
        }

        /// <summary>
        /// 当前显示模式。
        /// </summary>
        private enum RenderMode
        {
            None,
            SingleImage,
            Stitch,
        }
    }
}
