using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GPUStitch.Core
{
    /// <summary>
    /// GPU 配准器。
    ///
    /// 这个类专门负责“求相邻图片之间的相对位移”，不负责最终渲染。
    /// 当前版本支持两种使用方式：
    /// 1. 如果文件名能解析出行列号，则按二维网格布局做水平/垂直邻接配准；
    /// 2. 如果无法解析命名规则，则退回到按输入顺序的单行拼接。
    ///
    /// 配准评分本身在 GPU 上完成，使用“零均值亮度相关 + 梯度相关”的混合策略：
    /// - 亮度相关在严重模糊时更稳；
    /// - 梯度相关在亮度漂移时更稳；
    /// - 混合后对显微/工业图像会更耐受。
    /// </summary>
    public sealed class GpuRegistration : IDisposable
    {
        private readonly D3DDeviceManager _deviceManager;

        private ID3D11ComputeShader? _computeShader;
        private ID3D11Buffer? _constantBuffer;

        private ID3D11Texture2D? _scoreTexture;
        private ID3D11UnorderedAccessView? _scoreUav;
        private ID3D11Texture2D? _scoreReadbackTexture;
        private int _scoreWidth;
        private int _scoreHeight;

        private bool _disposed;

        public GpuRegistration(D3DDeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        /// <summary>
        /// 初始化配准器的 GPU 资源。
        /// 只需要在程序启动后执行一次。
        /// </summary>
        public void Initialize()
        {
            CompileShader();
            CreateConstantBuffer();
        }

        /// <summary>
        /// 根据输入图片生成全局布局。
        /// 若文件名符合“前三位行、后三位列”的约定，则优先构建二维网格布局；
        /// 否则回退到单行连续布局。
        /// </summary>
        public RegistrationLayout ComputeLayout(
            IReadOnlyList<GpuImage> images,
            RegistrationOptions options)
        {
            if (images == null)
                throw new ArgumentNullException(nameof(images));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (images.Count == 0)
                return RegistrationLayout.Empty;

            if (TryComputeGridLayout(images, options, out var gridLayout))
                return gridLayout;

            return ComputeSequentialHorizontalLayout(images, options);
        }

        /// <summary>
        /// 对一对相邻图片做 GPU 配准。
        /// axis=Horizontal 时表示“左 -> 右”；
        /// axis=Vertical 时表示“上 -> 下”。
        /// </summary>
        public PairRegistrationResult RegisterPair(
            GpuImage first,
            GpuImage second,
            RegistrationOptions options,
            RegistrationAxis axis,
            int sourceIndex,
            int targetIndex)
        {
            if (first == null)
                throw new ArgumentNullException(nameof(first));
            if (second == null)
                throw new ArgumentNullException(nameof(second));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            int overlapSize = axis == RegistrationAxis.Horizontal
                ? Clamp(options.ExpectedHorizontalOverlap, 8, Math.Min(first.Width, second.Width) - 2)
                : Clamp(options.ExpectedVerticalOverlap, 8, Math.Min(first.Height, second.Height) - 2);

            int candidateCountX = options.SearchRangeX * 2 + 1;
            int candidateCountY = options.SearchRangeY * 2 + 1;

            EnsureScoreTextures(candidateCountX, candidateCountY);
            UpdateConstants(first, second, overlapSize, options, axis);

            var ctx = _deviceManager.Context;
            ctx.CSSetShader(_computeShader);
            ctx.CSSetConstantBuffer(0, _constantBuffer);
            ctx.CSSetShaderResources(0, new[]
            {
                first.ShaderResourceView,
                second.ShaderResourceView,
            });
            ctx.CSSetUnorderedAccessView(0, _scoreUav);

            ctx.Dispatch(
                (candidateCountX + 7) / 8,
                (candidateCountY + 7) / 8,
                1);

            ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[2]);
            ctx.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView?)null);

            ctx.CopyResource(_scoreReadbackTexture!, _scoreTexture!);
            ctx.Flush();

            var best = ReadBestScore(candidateCountX, candidateCountY);

            int relativeOffsetX;
            int relativeOffsetY;

            if (axis == RegistrationAxis.Horizontal)
            {
                int baseShiftX = Math.Max(1, first.Width - overlapSize);
                relativeOffsetX = baseShiftX - best.BestDeltaX;
                relativeOffsetY = -best.BestDeltaY;
            }
            else
            {
                int baseShiftY = Math.Max(1, first.Height - overlapSize);
                relativeOffsetX = -best.BestDeltaX;
                relativeOffsetY = baseShiftY - best.BestDeltaY;
            }

            bool isConfident = best.Score >= options.ConfidenceThreshold;
            if (!isConfident)
            {
                if (axis == RegistrationAxis.Horizontal)
                {
                    relativeOffsetX = Math.Max(1, first.Width - overlapSize);
                    relativeOffsetY = 0;
                }
                else
                {
                    relativeOffsetX = 0;
                    relativeOffsetY = Math.Max(1, first.Height - overlapSize);
                }
            }

            return new PairRegistrationResult(
                sourceIndex,
                targetIndex,
                axis,
                best.BestDeltaX,
                best.BestDeltaY,
                best.Score,
                overlapSize,
                relativeOffsetX,
                relativeOffsetY,
                isConfident);
        }

        /// <summary>
        /// 退化场景：如果没有行列命名信息，则把输入图片按顺序视作单行。
        /// 这条路径兼容之前已有的使用方式。
        /// </summary>
        private RegistrationLayout ComputeSequentialHorizontalLayout(
            IReadOnlyList<GpuImage> images,
            RegistrationOptions options)
        {
            if (images.Count == 1)
            {
                var only = images[0];
                return new RegistrationLayout(
                    new List<ImagePlacement>
                    {
                        new ImagePlacement
                        {
                            OffsetX = 0,
                            OffsetY = 0,
                            Width = only.Width,
                            Height = only.Height,
                        }
                    },
                    only.Width,
                    only.Height,
                    new List<PairRegistrationResult>());
            }

            var positionsX = new float[images.Count];
            var positionsY = new float[images.Count];
            var pairResults = new List<PairRegistrationResult>(images.Count - 1);

            for (int i = 1; i < images.Count; i++)
            {
                var result = RegisterPair(
                    images[i - 1],
                    images[i],
                    options,
                    RegistrationAxis.Horizontal,
                    i - 1,
                    i);

                pairResults.Add(result);
                positionsX[i] = positionsX[i - 1] + result.RelativeOffsetX;
                positionsY[i] = positionsY[i - 1] + result.RelativeOffsetY;
            }

            return BuildLayoutFromPositions(images, positionsX, positionsY, pairResults);
        }

        /// <summary>
        /// 根据文件名中的行列号构建二维网格。
        ///
        /// 策略：
        /// 1. 为每张图解析出 (row, col)；
        /// 2. 只对“右邻居”和“下邻居”做局部配准；
        /// 3. 通过图遍历把局部位移传播成全局坐标；
        /// 4. 若图不完全连通，则使用网格步长做保守初始化。
        /// </summary>
        private bool TryComputeGridLayout(
            IReadOnlyList<GpuImage> images,
            RegistrationOptions options,
            out RegistrationLayout layout)
        {
            layout = RegistrationLayout.Empty;

            var coordinates = new GridImageCoordinate[images.Count];
            var indexByCoordinate = new Dictionary<GridImageCoordinate, int>();
            var sortedIndices = new List<int>(images.Count);

            for (int i = 0; i < images.Count; i++)
            {
                if (!GridImageCoordinate.TryParseFromFilePath(images[i].FilePath, out var coordinate))
                    return false;

                if (indexByCoordinate.ContainsKey(coordinate))
                    return false;

                coordinates[i] = coordinate;
                indexByCoordinate.Add(coordinate, i);
                sortedIndices.Add(i);
            }

            sortedIndices.Sort((left, right) =>
            {
                int rowCompare = coordinates[left].Row.CompareTo(coordinates[right].Row);
                return rowCompare != 0
                    ? rowCompare
                    : coordinates[left].Column.CompareTo(coordinates[right].Column);
            });

            var adjacency = new List<RegistrationEdge>[images.Count];
            for (int i = 0; i < adjacency.Length; i++)
            {
                adjacency[i] = new List<RegistrationEdge>();
            }

            var pairResults = new List<PairRegistrationResult>();

            for (int i = 0; i < sortedIndices.Count; i++)
            {
                int currentIndex = sortedIndices[i];
                var currentCoordinate = coordinates[currentIndex];

                var rightCoordinate = new GridImageCoordinate(currentCoordinate.Row, currentCoordinate.Column + 1);
                if (indexByCoordinate.TryGetValue(rightCoordinate, out int rightIndex))
                {
                    var result = RegisterPair(
                        images[currentIndex],
                        images[rightIndex],
                        options,
                        RegistrationAxis.Horizontal,
                        currentIndex,
                        rightIndex);

                    pairResults.Add(result);
                    AddBidirectionalEdge(adjacency, currentIndex, rightIndex, result.RelativeOffsetX, result.RelativeOffsetY);
                }

                var bottomCoordinate = new GridImageCoordinate(currentCoordinate.Row + 1, currentCoordinate.Column);
                if (indexByCoordinate.TryGetValue(bottomCoordinate, out int bottomIndex))
                {
                    var result = RegisterPair(
                        images[currentIndex],
                        images[bottomIndex],
                        options,
                        RegistrationAxis.Vertical,
                        currentIndex,
                        bottomIndex);

                    pairResults.Add(result);
                    AddBidirectionalEdge(adjacency, currentIndex, bottomIndex, result.RelativeOffsetX, result.RelativeOffsetY);
                }
            }

            if (pairResults.Count == 0)
                return false;

            float nominalStepX = EstimateNominalHorizontalStep(images, options);
            float nominalStepY = EstimateNominalVerticalStep(images, options);

            var positionsX = new float[images.Count];
            var positionsY = new float[images.Count];
            var visited = new bool[images.Count];
            var queue = new Queue<int>();
            var anchorCoordinate = coordinates[sortedIndices[0]];

            for (int i = 0; i < sortedIndices.Count; i++)
            {
                int seedIndex = sortedIndices[i];
                if (visited[seedIndex])
                    continue;

                if (seedIndex == sortedIndices[0])
                {
                    positionsX[seedIndex] = 0;
                    positionsY[seedIndex] = 0;
                }
                else
                {
                    var coordinate = coordinates[seedIndex];
                    positionsX[seedIndex] = (coordinate.Column - anchorCoordinate.Column) * nominalStepX;
                    positionsY[seedIndex] = (coordinate.Row - anchorCoordinate.Row) * nominalStepY;
                }

                visited[seedIndex] = true;
                queue.Enqueue(seedIndex);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    var edges = adjacency[current];
                    for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
                    {
                        var edge = edges[edgeIndex];
                        if (visited[edge.Target])
                            continue;

                        positionsX[edge.Target] = positionsX[current] + edge.OffsetX;
                        positionsY[edge.Target] = positionsY[current] + edge.OffsetY;
                        visited[edge.Target] = true;
                        queue.Enqueue(edge.Target);
                    }
                }
            }

            layout = BuildLayoutFromPositions(images, positionsX, positionsY, pairResults);
            return true;
        }

        private static void AddBidirectionalEdge(
            List<RegistrationEdge>[] adjacency,
            int source,
            int target,
            float offsetX,
            float offsetY)
        {
            adjacency[source].Add(new RegistrationEdge(target, offsetX, offsetY));
            adjacency[target].Add(new RegistrationEdge(source, -offsetX, -offsetY));
        }

        private static float EstimateNominalHorizontalStep(
            IReadOnlyList<GpuImage> images,
            RegistrationOptions options)
        {
            int minWidth = images[0].Width;
            for (int i = 1; i < images.Count; i++)
            {
                minWidth = Math.Min(minWidth, images[i].Width);
            }

            return Math.Max(1, minWidth - options.ExpectedHorizontalOverlap);
        }

        private static float EstimateNominalVerticalStep(
            IReadOnlyList<GpuImage> images,
            RegistrationOptions options)
        {
            int minHeight = images[0].Height;
            for (int i = 1; i < images.Count; i++)
            {
                minHeight = Math.Min(minHeight, images[i].Height);
            }

            return Math.Max(1, minHeight - options.ExpectedVerticalOverlap);
        }

        private static RegistrationLayout BuildLayoutFromPositions(
            IReadOnlyList<GpuImage> images,
            float[] positionsX,
            float[] positionsY,
            List<PairRegistrationResult> pairResults)
        {
            float minX = 0;
            float minY = 0;
            float maxX = images[0].Width;
            float maxY = images[0].Height;

            for (int i = 0; i < images.Count; i++)
            {
                minX = Math.Min(minX, positionsX[i]);
                minY = Math.Min(minY, positionsY[i]);
                maxX = Math.Max(maxX, positionsX[i] + images[i].Width);
                maxY = Math.Max(maxY, positionsY[i] + images[i].Height);
            }

            var placements = new List<ImagePlacement>(images.Count);
            for (int i = 0; i < images.Count; i++)
            {
                placements.Add(new ImagePlacement
                {
                    OffsetX = positionsX[i] - minX,
                    OffsetY = positionsY[i] - minY,
                    Width = images[i].Width,
                    Height = images[i].Height,
                });
            }

            return new RegistrationLayout(
                placements,
                (int)Math.Ceiling(maxX - minX),
                (int)Math.Ceiling(maxY - minY),
                pairResults);
        }

        private void CompileShader()
        {
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "GPUStitch.Shaders.RegistrationCS.hlsl";

            string source;
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException($"找不到嵌入资源: {resourceName}");

                using var reader = new StreamReader(stream);
                source = reader.ReadToEnd();
            }

            ReadOnlyMemory<byte> bytecode;
            try
            {
                bytecode = Compiler.Compile(source, "CSMain", resourceName, "cs_5_0");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"RegistrationCS 编译失败:\n{ex.Message}", ex);
            }

            _computeShader = _deviceManager.Device.CreateComputeShader(bytecode.Span);
        }

        private void CreateConstantBuffer()
        {
            var desc = new BufferDescription
            {
                ByteWidth = Marshal.SizeOf<RegistrationConstants>(),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            };

            _constantBuffer = _deviceManager.Device.CreateBuffer(desc);
        }

        private void EnsureScoreTextures(int width, int height)
        {
            if (_scoreTexture != null && _scoreWidth == width && _scoreHeight == height)
                return;

            _scoreUav?.Dispose();
            _scoreTexture?.Dispose();
            _scoreReadbackTexture?.Dispose();

            _scoreWidth = width;
            _scoreHeight = height;

            var gpuDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            _scoreTexture = _deviceManager.Device.CreateTexture2D(gpuDesc);
            _scoreUav = _deviceManager.Device.CreateUnorderedAccessView(_scoreTexture);

            var readbackDesc = gpuDesc;
            readbackDesc.Usage = ResourceUsage.Staging;
            readbackDesc.BindFlags = BindFlags.None;
            readbackDesc.CPUAccessFlags = CpuAccessFlags.Read;

            _scoreReadbackTexture = _deviceManager.Device.CreateTexture2D(readbackDesc);
        }

        private void UpdateConstants(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            RegistrationAxis axis)
        {
            var constants = new RegistrationConstants
            {
                FirstWidth = first.Width,
                FirstHeight = first.Height,
                SecondWidth = second.Width,
                SecondHeight = second.Height,
                OverlapSize = overlapSize,
                SearchRangeX = options.SearchRangeX,
                SearchRangeY = options.SearchRangeY,
                SampleStep = options.SampleStep,
                Orientation = (int)axis,
                MinSampleCount = options.MinSampleCount,
                MinGradientEnergy = options.MinGradientEnergy,
                MinLumaVariance = options.MinLumaVariance,
                GradientWeight = options.GradientWeight,
                LumaWeight = options.LumaWeight,
            };

            var mapped = _deviceManager.Context.Map(_constantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(constants, mapped.DataPointer, false);
            _deviceManager.Context.Unmap(_constantBuffer!, 0);
        }

        private BestScoreResult ReadBestScore(int width, int height)
        {
            int bestX = 0;
            int bestY = 0;
            float bestScore = float.NegativeInfinity;

            var mapped = _deviceManager.Context.Map(_scoreReadbackTexture!, 0, MapMode.Read);
            try
            {
                unsafe
                {
                    byte* basePtr = (byte*)mapped.DataPointer.ToPointer();
                    for (int y = 0; y < height; y++)
                    {
                        float* row = (float*)(basePtr + (y * mapped.RowPitch));
                        for (int x = 0; x < width; x++)
                        {
                            float score = row[x];
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestX = x;
                                bestY = y;
                            }
                        }
                    }
                }
            }
            finally
            {
                _deviceManager.Context.Unmap(_scoreReadbackTexture!, 0);
            }

            return new BestScoreResult(
                bestX - ((width - 1) / 2),
                bestY - ((height - 1) / 2),
                bestScore);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max < min)
                return min;
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _scoreUav?.Dispose();
            _scoreTexture?.Dispose();
            _scoreReadbackTexture?.Dispose();
            _constantBuffer?.Dispose();
            _computeShader?.Dispose();

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// GPU 搜索结果中得分最高的候选位移。
        /// 这里只保存局部搜索空间中的最佳点，后续再换算成世界坐标位移。
        /// </summary>
        private readonly struct BestScoreResult
        {
            public BestScoreResult(int bestDeltaX, int bestDeltaY, float score)
            {
                BestDeltaX = bestDeltaX;
                BestDeltaY = bestDeltaY;
                Score = score;
            }

            public int BestDeltaX { get; }
            public int BestDeltaY { get; }
            public float Score { get; }
        }

        /// <summary>
        /// 图遍历用的邻接边。
        /// 它只表达“从当前图走到另一张图，需要加上的位移”。
        /// </summary>
        private readonly struct RegistrationEdge
        {
            public RegistrationEdge(int target, float offsetX, float offsetY)
            {
                Target = target;
                OffsetX = offsetX;
                OffsetY = offsetY;
            }

            public int Target { get; }
            public float OffsetX { get; }
            public float OffsetY { get; }
        }
    }
}
