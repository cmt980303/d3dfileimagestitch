using GPUStitch.Core;
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
    /// 主窗口：负责图片导入、渐进预览、最终配准与 D3D11Image 显示。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int MaxPreviewTextureSize = 8192;
        private const long MaxPreviewCanvasBytes = 384L * 1024L * 1024L;
        private const long SourceTextureBudgetBytes = 512L * 1024L * 1024L;
        private const int MaxPerImagePreviewDimension = 3072;
        private const int MaxParallelLoads = 2;

        private D3DDeviceManager? _deviceManager;
        private GpuImageLoader? _imageLoader;
        private GpuStitcher? _stitcher;
        private GpuRegistration? _registration;

        private readonly List<ImageFileMetadata> _loadedMetadata = new List<ImageFileMetadata>();
        private readonly List<GpuImage?> _imageSlots = new List<GpuImage?>();
        private readonly List<GridImageCoordinate?> _metadataCoordinates = new List<GridImageCoordinate?>();
        private readonly Dictionary<GridImageCoordinate, int> _indexByCoordinate =
            new Dictionary<GridImageCoordinate, int>();

        private ImageLoadPlan _currentLoadPlan = new ImageLoadPlan();
        private CancellationTokenSource? _loadCts;
        private bool _isLoading;

        private bool _gpuInitialized;
        private ID3D11Texture2D? _sharedRenderTarget;

        private readonly HashSet<int> _accumulatedIndices = new HashSet<int>();
        private bool _canvasPrepared;

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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
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
                CancelCurrentLoad();

                SetLoadingUiState(isLoading: true);
                UpdateStatus("正在分析图片元数据...");

                var combinedMetadata = await Task.Run(() => BuildCombinedMetadata(dlg.FileNames));
                SortMetadataForStreaming(combinedMetadata);

                var loadPlan = ImageLoadPlanner.Build(
                    combinedMetadata,
                    SourceTextureBudgetBytes,
                    MaxPerImagePreviewDimension);

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
                BtnStitch.IsEnabled = false;
                BtnRecommend.IsEnabled = false;
                BtnLoadImages.IsEnabled = false;
                UpdateStatus("正在执行 GPU 配准...");

                int overlapPixels = (int)SliderOverlap.Value;
                float blendWidth = (float)SliderBlendWidth.Value;

                var registrationOptions =
                    RegistrationOptions.CreateForImages(images, overlapPixels);

                var layout = await Task.Run(() =>
                    _registration!.ComputeLayout(images, registrationOptions));

                if (layout.CanvasWidth <= 0 || layout.CanvasHeight <= 0)
                {
                    MessageBox.Show("计算画布尺寸失败", "错误");
                    return;
                }

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
            CancelCurrentLoad();
            ClearAllImages();

            _sharedRenderTarget = null;
            _stitcher?.Dispose();
            _registration?.Dispose();
            _imageLoader?.Dispose();
            _deviceManager?.Dispose();
        }

        private void RenderFrame(IntPtr surface, bool isNewSurface)
        {
            if (surface == IntPtr.Zero || !_gpuInitialized)
                return;

            if (_renderMode == RenderMode.None)
                return;

            try
            {
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
                            _deviceManager!.Context.CopyResource(_sharedRenderTarget, image.Texture);
                        }
                        break;
                    }
                    case RenderMode.Stitch:
                    {
                        // 增量累加：仅处理尚未累加的新图像
                        if (!_canvasPrepared)
                        {
                            _stitcher!.PrepareCanvas(_currentCanvasWidth, _currentCanvasHeight);
                            _accumulatedIndices.Clear();
                            _canvasPrepared = true;
                        }

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

        private async Task LoadImagesProgressivelyAsync(
            IReadOnlyList<ImageFileMetadata> metadata,
            ImageLoadPlan loadPlan,
            CancellationToken cancellationToken)
        {
            var pending = new List<Task<LoadedImageResult>>();
            int nextIndex = 0;

            while (nextIndex < metadata.Count || pending.Count > 0)
            {
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
                    result.Image.Dispose();
                    break;
                }

                _imageSlots[result.Index] = result.Image;
                int loadedCount = GetLoadedCount();

                if (loadedCount == 1)
                {
                    _renderMode = RenderMode.Stitch;
                    _sharedRenderTarget = null;
                }

                BtnShowSingle.IsEnabled = loadedCount >= 1;
                InteropImage.RequestRender();

                UpdateStatus(
                    $"正在导入并贴图: {loadedCount}/{metadata.Count}，" +
                    $"缩放 {loadPlan.Scale:F3}x");

                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

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

        private RegistrationLayout BuildProvisionalLayout(
            IReadOnlyList<ImageFileMetadata> metadata,
            float imageScale,
            int overlapPixels)
        {
            if (TryBuildGridProvisionalLayout(metadata, imageScale, overlapPixels, out var gridLayout))
                return gridLayout;

            return BuildSequentialProvisionalLayout(metadata, imageScale, overlapPixels);
        }

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

            float scaledOverlap = Math.Max(1.0f, overlapPixels * imageScale);

            var rowOffsets = new Dictionary<int, float>();
            var columnOffsets = new Dictionary<int, float>();

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

        private bool IsImageLoadedAt(int row, int column)
        {
            if (!_indexByCoordinate.TryGetValue(new GridImageCoordinate(row, column), out int index))
                return false;

            return index >= 0 && index < _imageSlots.Count && _imageSlots[index] != null;
        }

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

        private GpuImage? GetFirstLoadedImage()
        {
            for (int i = 0; i < _imageSlots.Count; i++)
            {
                if (_imageSlots[i] != null)
                    return _imageSlots[i];
            }

            return null;
        }

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

        private void SetLoadingUiState(bool isLoading)
        {
            _isLoading = isLoading;
            BtnLoadImages.IsEnabled = !isLoading;
            BtnStitch.IsEnabled = !isLoading && GetLoadedImagesInOrder().Count >= 2;
            BtnRecommend.IsEnabled = !isLoading && GetLoadedImagesInOrder().Count >= 2;
            BtnShowSingle.IsEnabled = GetLoadedImagesInOrder().Count >= 1;
        }

        private void UpdateStatus(string message)
        {
            TxtStatus.Text = message;
        }

        private static string FormatMiB(long bytes)
        {
            return $"{(bytes / 1024.0 / 1024.0):F1} MiB";
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
            float dimensionScale = Math.Min(
                1.0f,
                Math.Min(
                    MaxPreviewTextureSize / (float)layout.CanvasWidth,
                    MaxPreviewTextureSize / (float)layout.CanvasHeight));

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

            // 将偏移和尺寸四舍五入到整数像素，消除亚像素间隙导致的条纹
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

        private enum RenderMode
        {
            None,
            SingleImage,
            Stitch,
        }
    }
}
