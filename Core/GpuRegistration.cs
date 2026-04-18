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
    /// 针对定拍模式，利用已知重叠区域，只在局部窗口内搜索相邻图像的精确偏移。
    /// 为了兼顾轻度模糊与亮度漂移，评分使用梯度归一化相关，而不是直接比灰度值。
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

        public void Initialize()
        {
            CompileShader();
            CreateConstantBuffer();
        }

        public RegistrationLayout ComputeHorizontalLayout(
            IReadOnlyList<GpuImage> images,
            RegistrationOptions options)
        {
            if (images == null)
                throw new ArgumentNullException(nameof(images));
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (images.Count == 0)
                return RegistrationLayout.Empty;

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

            var placements = new List<ImagePlacement>(images.Count)
            {
                new ImagePlacement
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    Width = images[0].Width,
                    Height = images[0].Height,
                }
            };

            var pairResults = new List<PairRegistrationResult>(images.Count - 1);

            float currentX = 0;
            float currentY = 0;
            float minY = 0;
            float maxX = images[0].Width;
            float maxY = images[0].Height;

            for (int i = 1; i < images.Count; i++)
            {
                var result = RegisterPair(images[i - 1], images[i], options);
                pairResults.Add(result);

                currentX += result.RelativeOffsetX;
                currentY += result.RelativeOffsetY;

                placements.Add(new ImagePlacement
                {
                    OffsetX = currentX,
                    OffsetY = currentY,
                    Width = images[i].Width,
                    Height = images[i].Height,
                });

                minY = Math.Min(minY, currentY);
                maxX = Math.Max(maxX, currentX + images[i].Width);
                maxY = Math.Max(maxY, currentY + images[i].Height);
            }

            if (minY < 0)
            {
                for (int i = 0; i < placements.Count; i++)
                {
                    var placement = placements[i];
                    placement.OffsetY -= minY;
                    placements[i] = placement;
                }
            }

            return new RegistrationLayout(
                placements,
                (int)Math.Ceiling(maxX),
                (int)Math.Ceiling(maxY - minY),
                pairResults);
        }

        public PairRegistrationResult RegisterPair(
            GpuImage left,
            GpuImage right,
            RegistrationOptions options)
        {
            if (left == null)
                throw new ArgumentNullException(nameof(left));
            if (right == null)
                throw new ArgumentNullException(nameof(right));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            int overlapWidth = Clamp(
                options.ExpectedOverlap,
                8,
                Math.Min(left.Width, right.Width) - 2);

            int candidateCountX = options.SearchRangeX * 2 + 1;
            int candidateCountY = options.SearchRangeY * 2 + 1;
            EnsureScoreTextures(candidateCountX, candidateCountY);
            UpdateConstants(left, right, overlapWidth, options);

            var ctx = _deviceManager.Context;
            ctx.CSSetShader(_computeShader);
            ctx.CSSetConstantBuffer(0, _constantBuffer);
            ctx.CSSetShaderResources(0, new[]
            {
                left.ShaderResourceView,
                right.ShaderResourceView,
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
            int baseShiftX = Math.Max(1, left.Width - overlapWidth);
            int relativeOffsetX = baseShiftX - best.BestDeltaX;
            int relativeOffsetY = -best.BestDeltaY;

            bool isConfident = best.Score >= options.ConfidenceThreshold;
            if (!isConfident)
            {
                relativeOffsetX = baseShiftX;
                relativeOffsetY = 0;
            }

            return new PairRegistrationResult(
                best.BestDeltaX,
                best.BestDeltaY,
                best.Score,
                overlapWidth,
                relativeOffsetX,
                relativeOffsetY,
                isConfident);
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
            GpuImage left,
            GpuImage right,
            int overlapWidth,
            RegistrationOptions options)
        {
            var constants = new RegistrationConstants
            {
                LeftWidth = left.Width,
                LeftHeight = left.Height,
                RightWidth = right.Width,
                RightHeight = right.Height,
                OverlapWidth = overlapWidth,
                SearchRangeX = options.SearchRangeX,
                SearchRangeY = options.SearchRangeY,
                SampleStep = options.SampleStep,
                MinGradientEnergy = options.MinGradientEnergy,
                MinSampleCount = options.MinSampleCount,
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
    }

    public sealed class RegistrationOptions
    {
        public int ExpectedOverlap { get; set; } = 100;
        public int SearchRangeX { get; set; } = 64;
        public int SearchRangeY { get; set; } = 32;
        public int SampleStep { get; set; } = 2;
        public float MinGradientEnergy { get; set; } = 0.00015f;
        public int MinSampleCount { get; set; } = 64;
        public float MinLumaVariance { get; set; } = 0.00005f;
        public float GradientWeight { get; set; } = 0.45f;
        public float LumaWeight { get; set; } = 0.55f;
        public float ConfidenceThreshold { get; set; } = 0.06f;

        public static RegistrationOptions CreateForImages(
            IReadOnlyList<GpuImage> images,
            int expectedOverlap)
        {
            if (images == null || images.Count == 0)
                return new RegistrationOptions { ExpectedOverlap = expectedOverlap };

            int minWidth = images[0].Width;
            int minHeight = images[0].Height;

            for (int i = 1; i < images.Count; i++)
            {
                minWidth = Math.Min(minWidth, images[i].Width);
                minHeight = Math.Min(minHeight, images[i].Height);
            }

            int searchRangeX = ClampStatic(expectedOverlap / 2, 16, 192);
            int searchRangeY = ClampStatic(minHeight / 48, 6, 64);
            int sampleStep = minWidth >= 3500 || minHeight >= 3500 ? 3 : 2;
            int minSamples = sampleStep <= 2 ? 64 : 48;
            float minGradientEnergy = sampleStep <= 2 ? 0.00015f : 0.00010f;
            float minLumaVariance = sampleStep <= 2 ? 0.00005f : 0.00003f;

            return new RegistrationOptions
            {
                ExpectedOverlap = expectedOverlap,
                SearchRangeX = searchRangeX,
                SearchRangeY = searchRangeY,
                SampleStep = sampleStep,
                MinSampleCount = minSamples,
                MinGradientEnergy = minGradientEnergy,
                MinLumaVariance = minLumaVariance,
                GradientWeight = 0.40f,
                LumaWeight = 0.60f,
                ConfidenceThreshold = 0.03f,
            };
        }

        private static int ClampStatic(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }

    public sealed class RegistrationLayout
    {
        public static readonly RegistrationLayout Empty =
            new RegistrationLayout(new List<ImagePlacement>(), 0, 0, new List<PairRegistrationResult>());

        public RegistrationLayout(
            List<ImagePlacement> placements,
            int canvasWidth,
            int canvasHeight,
            List<PairRegistrationResult> pairResults)
        {
            Placements = placements;
            CanvasWidth = canvasWidth;
            CanvasHeight = canvasHeight;
            PairResults = pairResults;
        }

        public List<ImagePlacement> Placements { get; }
        public int CanvasWidth { get; }
        public int CanvasHeight { get; }
        public List<PairRegistrationResult> PairResults { get; }

        public int ConfidentPairCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    if (PairResults[i].IsConfident)
                        count++;
                }
                return count;
            }
        }

        public float AverageScore
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Score;
                }
                return sum / PairResults.Count;
            }
        }
    }

    public sealed class PairRegistrationResult
    {
        public PairRegistrationResult(
            int bestDeltaX,
            int bestDeltaY,
            float score,
            int overlapWidth,
            int relativeOffsetX,
            int relativeOffsetY,
            bool isConfident)
        {
            BestDeltaX = bestDeltaX;
            BestDeltaY = bestDeltaY;
            Score = score;
            OverlapWidth = overlapWidth;
            RelativeOffsetX = relativeOffsetX;
            RelativeOffsetY = relativeOffsetY;
            IsConfident = isConfident;
        }

        public int BestDeltaX { get; }
        public int BestDeltaY { get; }
        public float Score { get; }
        public int OverlapWidth { get; }
        public int RelativeOffsetX { get; }
        public int RelativeOffsetY { get; }
        public bool IsConfident { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RegistrationConstants
    {
        public int LeftWidth;
        public int LeftHeight;
        public int RightWidth;
        public int RightHeight;

        public int OverlapWidth;
        public int SearchRangeX;
        public int SearchRangeY;
        public int SampleStep;

        public float MinGradientEnergy;
        public int MinSampleCount;
        public float MinLumaVariance;
        public float GradientWeight;

        public float LumaWeight;
        public float Padding0;
        public float Padding1;
        public float Padding2;
    }
}
