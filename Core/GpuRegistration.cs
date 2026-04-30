using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using GPUStitch.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GPUStitch.Core
{
        /// <summary>
        /// 配准器。
        ///
        /// 这个类专门负责“求相邻图片之间的相对位移”，不负责最终渲染。
        /// 当前主路径使用“重叠先验 + phase correlation 亚像素精修”；
        /// 原有 GPU score-map 搜索保留为回退路径。
        ///
        /// 布局模式仍支持两种使用方式：
        /// 1. 如果文件名能解析出行列号，则按二维网格布局做水平/垂直邻接配准；
        /// 2. 如果无法解析命名规则，则退回到按输入顺序的单行拼接。
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
        /// 预热 fallback 所需的 GPU 资源。
        /// 当前主路径不依赖它；只有当 phase correlation 失败并回退到旧 score-map 搜索时才需要。
        /// </summary>
        public void Initialize()
        {
            EnsureFallbackGpuResources();
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

            Debug.WriteLine(
                $"[Registration] === 开始配准 {images.Count} 张图片 ===\n" +
                $"  水平重叠={options.ExpectedHorizontalOverlap}, 垂直重叠={options.ExpectedVerticalOverlap}\n" +
                $"  搜索范围: primary={options.SearchRangePrimary}, cross={options.SearchRangeCross}\n" +
                $"  置信阈值={options.ConfidenceThreshold}, 几何阈值={options.GeometryConfidenceThreshold}");

            if (TryComputeGridLayout(images, options, out var gridLayout))
            {
                Debug.WriteLine(
                    $"[Registration] === 网格布局完成: {gridLayout.CanvasWidth}x{gridLayout.CanvasHeight}, " +
                    $"可信 {gridLayout.ConfidentPairCount}/{gridLayout.PairResults.Count}, " +
                    $"平均响应 {gridLayout.AverageScore:F4} ===");
                return gridLayout;
            }

            Debug.WriteLine("[Registration] 网格布局失败，回退到顺序水平布局");
            var seqLayout = ComputeSequentialHorizontalLayout(images, options);
            Debug.WriteLine(
                $"[Registration] === 顺序布局完成: {seqLayout.CanvasWidth}x{seqLayout.CanvasHeight}, " +
                $"可信 {seqLayout.ConfidentPairCount}/{seqLayout.PairResults.Count}, " +
                $"平均响应 {seqLayout.AverageScore:F4} ===");
            return seqLayout;
        }

        /// <summary>
        /// 对一对相邻图片做配准。
        /// axis=Horizontal 时表示“左 -> 右”；
        /// axis=Vertical 时表示“上 -> 下”。
        ///
        /// 当前优先走：
        /// 1. 根据已知 overlap 裁出重叠带；
        /// 2. 在 CPU 上做 phase correlation 亚像素精修；
        /// 3. 用 reverse / segment 诊断评估几何可靠度；
        /// 4. 必要时才回退到旧的 GPU score-map 搜索。
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

            // overlapSize 不是任意值，而是被限制在两张图尺寸可容纳的范围内。
            int overlapSize = axis == RegistrationAxis.Horizontal
                ? Clamp(options.ExpectedHorizontalOverlap, 8, Math.Min(first.Width, second.Width) - 2)
                : Clamp(options.ExpectedVerticalOverlap, 8, Math.Min(first.Height, second.Height) - 2);

            if (TryRegisterPairWithPhaseCorrelation(
                    first,
                    second,
                    overlapSize,
                    options,
                    axis,
                    sourceIndex,
                    targetIndex,
                    out var phaseResult))
            {
                return phaseResult;
            }

            return RegisterPairWithGpuSearch(first, second, overlapSize, options, axis, sourceIndex, targetIndex);
        }

        private bool TryRegisterPairWithPhaseCorrelation(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            RegistrationAxis axis,
            int sourceIndex,
            int targetIndex,
            out PairRegistrationResult result)
        {
            result = default!;

            if (!TryRunPhaseEstimate(first, second, overlapSize, axis, reverse: false, segmentIndex: -1, segmentCount: 1, out var best))
                return false;

            SearchOrientation forwardOrientation = axis == RegistrationAxis.Horizontal
                ? SearchOrientation.HorizontalForward
                : SearchOrientation.VerticalForward;

            (float relativeOffsetX, float relativeOffsetY) = ComputeRelativeOffset(
                best.BestDeltaX,
                best.BestDeltaY,
                first,
                second,
                overlapSize,
                forwardOrientation);

            var diagnostics = BuildPhaseDiagnostics(
                first,
                second,
                overlapSize,
                options,
                axis,
                best,
                forwardOffsetX: relativeOffsetX,
                forwardOffsetY: relativeOffsetY);

            bool isConfident =
                best.Score >= options.ConfidenceThreshold &&
                diagnostics.GeometryReliability >= options.GeometryConfidenceThreshold;

            if (!isConfident)
            {
                (relativeOffsetX, relativeOffsetY) = CreateFallbackOffset(first, overlapSize, axis);
            }

            result = new PairRegistrationResult(
                sourceIndex,
                targetIndex,
                axis,
                best.BestDeltaX,
                best.BestDeltaY,
                best.Score,
                overlapSize,
                relativeOffsetX,
                relativeOffsetY,
                isConfident,
                diagnostics);

            Debug.WriteLine(
                $"[Registration][Phase] 配准 [{sourceIndex}]->[{targetIndex}] {axis}: " +
                $"response={best.Score:F4}, delta=({best.BestDeltaX:F3},{best.BestDeltaY:F3}), " +
                $"offset=({relativeOffsetX:F3},{relativeOffsetY:F3}), overlap={overlapSize}, " +
                $"peakMargin={diagnostics.PeakMargin:F4}, roundTrip={diagnostics.RoundTripErrorMagnitude:F2}px, " +
                $"localDrift={diagnostics.SegmentSpreadMagnitude:F2}px, geomRel={diagnostics.GeometryReliability:F3}, " +
                $"confident={isConfident}{(isConfident ? "" : " [FALLBACK]")}");

            return true;
        }

        private PairRegistrationResult RegisterPairWithGpuSearch(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            RegistrationAxis axis,
            int sourceIndex,
            int targetIndex)
        {
            EnsureFallbackGpuResources();

            SearchOrientation forwardOrientation = axis == RegistrationAxis.Horizontal
                ? SearchOrientation.HorizontalForward
                : SearchOrientation.VerticalForward;

            var best = RunSearch(
                first,
                second,
                overlapSize,
                options,
                forwardOrientation,
                SearchRegion.Full);

            (float relativeOffsetX, float relativeOffsetY) = ComputeRelativeOffset(
                best.BestDeltaX,
                best.BestDeltaY,
                first,
                second,
                overlapSize,
                forwardOrientation);

            var diagnostics = BuildDiagnostics(
                first,
                second,
                overlapSize,
                options,
                axis,
                best,
                relativeOffsetX,
                relativeOffsetY);

            bool isConfident =
                best.Score >= options.ConfidenceThreshold &&
                diagnostics.GeometryReliability >= options.GeometryConfidenceThreshold;

            if (!isConfident)
            {
                (relativeOffsetX, relativeOffsetY) = CreateFallbackOffset(first, overlapSize, axis);
            }

            var result = new PairRegistrationResult(
                sourceIndex,
                targetIndex,
                axis,
                best.BestDeltaX,
                best.BestDeltaY,
                best.Score,
                overlapSize,
                relativeOffsetX,
                relativeOffsetY,
                isConfident,
                diagnostics);

            Debug.WriteLine(
                $"[Registration][GPU-Fallback] 配准 [{sourceIndex}]->[{targetIndex}] {axis}: " +
                $"score={best.Score:F4}, delta=({best.BestDeltaX:F3},{best.BestDeltaY:F3}), " +
                $"offset=({relativeOffsetX:F3},{relativeOffsetY:F3}), overlap={overlapSize}, " +
                $"peakMargin={diagnostics.PeakMargin:F4}, roundTrip={diagnostics.RoundTripErrorMagnitude:F2}px, " +
                $"localDrift={diagnostics.SegmentSpreadMagnitude:F2}px, geomRel={diagnostics.GeometryReliability:F3}, " +
                $"confident={isConfident}{(isConfident ? "" : " [FALLBACK]")}");

            return result;
        }

        private void EnsureFallbackGpuResources()
        {
            if (_computeShader != null && _constantBuffer != null)
                return;

            CompileShader();
            CreateConstantBuffer();
        }

        private BestScoreResult RunSearch(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            SearchOrientation orientation,
            SearchRegion region)
        {
            GetSearchRanges(orientation, options, out int searchRangeX, out int searchRangeY);
            int coarseCenterX = 0;
            int coarseCenterY = 0;
            if (region.IsFull &&
                TryGetPhaseCorrelationSearchCenter(
                    first,
                    second,
                    overlapSize,
                    orientation,
                    searchRangeX,
                    searchRangeY,
                    out int phaseCenterX,
                    out int phaseCenterY))
            {
                coarseCenterX = phaseCenterX;
                coarseCenterY = phaseCenterY;
            }

            int coarseSampleStep = GetEffectiveSampleStep(first, second, overlapSize, orientation, options.SampleStep);
            var coarse = ExecuteSearch(
                first,
                second,
                overlapSize,
                options,
                orientation,
                region,
                searchRangeX,
                searchRangeY,
                coarseCenterX,
                coarseCenterY,
                coarseSampleStep);

            GetRefineSearchRanges(orientation, searchRangeX, searchRangeY, out int refineRangeX, out int refineRangeY);
            if (refineRangeX <= 0 || refineRangeY <= 0)
                return coarse;

            int refineCenterX = Clamp((int)Math.Round(coarse.BestDeltaX), -searchRangeX, searchRangeX);
            int refineCenterY = Clamp((int)Math.Round(coarse.BestDeltaY), -searchRangeY, searchRangeY);

            return ExecuteSearch(
                first,
                second,
                overlapSize,
                options,
                orientation,
                region,
                refineRangeX,
                refineRangeY,
                refineCenterX,
                refineCenterY,
                1);
        }

        private static bool TryGetPhaseCorrelationSearchCenter(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            SearchOrientation orientation,
            int searchRangeX,
            int searchRangeY,
            out int searchCenterX,
            out int searchCenterY)
        {
            searchCenterX = 0;
            searchCenterY = 0;

            RegistrationAxis axis =
                orientation == SearchOrientation.HorizontalForward || orientation == SearchOrientation.HorizontalReverse
                    ? RegistrationAxis.Horizontal
                    : RegistrationAxis.Vertical;

            if (!PhaseCorrelationPriorEstimator.TryEstimateDelta(
                    first,
                    second,
                    overlapSize,
                    axis,
                    out float deltaX,
                    out float deltaY))
            {
                return false;
            }

            searchCenterX = Clamp((int)Math.Round(deltaX), -searchRangeX, searchRangeX);
            searchCenterY = Clamp((int)Math.Round(deltaY), -searchRangeY, searchRangeY);
            return true;
        }

        private BestScoreResult ExecuteSearch(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            SearchOrientation orientation,
            SearchRegion region,
            int searchRangeX,
            int searchRangeY,
            int searchCenterX,
            int searchCenterY,
            int sampleStep)
        {
            int candidateCountX = searchRangeX * 2 + 1;
            int candidateCountY = searchRangeY * 2 + 1;

            EnsureScoreTextures(candidateCountX, candidateCountY);
            UpdateConstants(
                first,
                second,
                overlapSize,
                searchRangeX,
                searchRangeY,
                sampleStep,
                options,
                orientation,
                region,
                searchCenterX,
                searchCenterY);

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

            return ReadBestScore(candidateCountX, candidateCountY, searchCenterX, searchCenterY);
        }

        private static void GetSearchRanges(
            SearchOrientation orientation,
            RegistrationOptions options,
            out int searchRangeX,
            out int searchRangeY)
        {
            if (orientation == SearchOrientation.HorizontalForward ||
                orientation == SearchOrientation.HorizontalReverse)
            {
                searchRangeX = options.SearchRangePrimary;
                searchRangeY = options.SearchRangeCross;
            }
            else
            {
                searchRangeX = options.SearchRangeCross;
                searchRangeY = options.SearchRangePrimary;
            }
        }

        private static void GetRefineSearchRanges(
            SearchOrientation orientation,
            int searchRangeX,
            int searchRangeY,
            out int refineRangeX,
            out int refineRangeY)
        {
            const int primaryRadius = 8;
            const int crossRadius = 4;

            if (orientation == SearchOrientation.HorizontalForward ||
                orientation == SearchOrientation.HorizontalReverse)
            {
                refineRangeX = Math.Min(searchRangeX, primaryRadius);
                refineRangeY = Math.Min(searchRangeY, crossRadius);
            }
            else
            {
                refineRangeX = Math.Min(searchRangeX, crossRadius);
                refineRangeY = Math.Min(searchRangeY, primaryRadius);
            }
        }

        private static void GetVerificationSearchRanges(
            SearchOrientation orientation,
            out int verifyRangeX,
            out int verifyRangeY)
        {
            const int primaryRadius = 6;
            const int crossRadius = 3;

            if (orientation == SearchOrientation.HorizontalForward ||
                orientation == SearchOrientation.HorizontalReverse)
            {
                verifyRangeX = primaryRadius;
                verifyRangeY = crossRadius;
            }
            else
            {
                verifyRangeX = crossRadius;
                verifyRangeY = primaryRadius;
            }
        }

        private BestScoreResult RunVerificationSearch(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            SearchOrientation orientation,
            SearchRegion region,
            float expectedDeltaX,
            float expectedDeltaY)
        {
            GetVerificationSearchRanges(orientation, out int verifyRangeX, out int verifyRangeY);
            GetSearchRanges(orientation, options, out int maxRangeX, out int maxRangeY);
            int searchCenterX = Clamp((int)Math.Round(expectedDeltaX), -maxRangeX, maxRangeX);
            int searchCenterY = Clamp((int)Math.Round(expectedDeltaY), -maxRangeY, maxRangeY);
            searchCenterX = Clamp(searchCenterX, -maxRangeX, maxRangeX);
            searchCenterY = Clamp(searchCenterY, -maxRangeY, maxRangeY);
            verifyRangeX = Math.Min(verifyRangeX, maxRangeX);
            verifyRangeY = Math.Min(verifyRangeY, maxRangeY);

            return ExecuteSearch(
                first,
                second,
                overlapSize,
                options,
                orientation,
                region,
                verifyRangeX,
                verifyRangeY,
                searchCenterX,
                searchCenterY,
                1);
        }

        private static (float OffsetX, float OffsetY) ComputeRelativeOffset(
            float bestDeltaX,
            float bestDeltaY,
            GpuImage first,
            GpuImage second,
            int overlapSize,
            SearchOrientation orientation)
        {
            switch (orientation)
            {
                case SearchOrientation.HorizontalForward:
                {
                    float baseShiftX = Math.Max(1, first.Width - overlapSize);
                    return (baseShiftX - bestDeltaX, -bestDeltaY);
                }
                case SearchOrientation.VerticalForward:
                {
                    float baseShiftY = Math.Max(1, first.Height - overlapSize);
                    return (-bestDeltaX, baseShiftY - bestDeltaY);
                }
                case SearchOrientation.HorizontalReverse:
                {
                    float baseShiftX = Math.Max(1, second.Width - overlapSize);
                    return (-(baseShiftX + bestDeltaX), -bestDeltaY);
                }
                case SearchOrientation.VerticalReverse:
                {
                    float baseShiftY = Math.Max(1, second.Height - overlapSize);
                    return (-bestDeltaX, -(baseShiftY + bestDeltaY));
                }
                default:
                    throw new InvalidOperationException($"未知搜索方向: {orientation}");
            }
        }

        private static (float DeltaX, float DeltaY) ComputeExpectedDeltaForOffset(
            float offsetX,
            float offsetY,
            GpuImage first,
            GpuImage second,
            int overlapSize,
            SearchOrientation orientation)
        {
            switch (orientation)
            {
                case SearchOrientation.HorizontalForward:
                {
                    float baseShiftX = Math.Max(1, first.Width - overlapSize);
                    return (baseShiftX - offsetX, -offsetY);
                }
                case SearchOrientation.VerticalForward:
                {
                    float baseShiftY = Math.Max(1, first.Height - overlapSize);
                    return (-offsetX, baseShiftY - offsetY);
                }
                case SearchOrientation.HorizontalReverse:
                {
                    float baseShiftX = Math.Max(1, second.Width - overlapSize);
                    return (-offsetX - baseShiftX, -offsetY);
                }
                case SearchOrientation.VerticalReverse:
                {
                    float baseShiftY = Math.Max(1, second.Height - overlapSize);
                    return (-offsetX, -offsetY - baseShiftY);
                }
                default:
                    throw new InvalidOperationException($"未知搜索方向: {orientation}");
            }
        }

        private static (float OffsetX, float OffsetY) CreateFallbackOffset(
            GpuImage first,
            int overlapSize,
            RegistrationAxis axis)
        {
            if (axis == RegistrationAxis.Horizontal)
                return (Math.Max(1, first.Width - overlapSize), 0);

            return (0, Math.Max(1, first.Height - overlapSize));
        }

        private static bool TryRunPhaseEstimate(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationAxis axis,
            bool reverse,
            int segmentIndex,
            int segmentCount,
            out BestScoreResult best)
        {
            best = default;

            if (!PhaseCorrelationPriorEstimator.TryEstimateDeltaDetailed(
                    first,
                    second,
                    overlapSize,
                    axis,
                    reverse,
                    segmentIndex,
                    segmentCount,
                    out var phaseResult))
            {
                return false;
            }

            best = new BestScoreResult(
                phaseResult.DeltaX,
                phaseResult.DeltaY,
                phaseResult.Response,
                phaseResult.SecondBestResponse,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                phaseResult.Coverage);
            return true;
        }

        private PairRegistrationDiagnostics BuildPhaseDiagnostics(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            RegistrationAxis axis,
            BestScoreResult best,
            float forwardOffsetX,
            float forwardOffsetY)
        {
            SearchOrientation reverseOrientation = axis == RegistrationAxis.Horizontal
                ? SearchOrientation.HorizontalReverse
                : SearchOrientation.VerticalReverse;

            float reverseScore = 0.0f;
            float roundTripErrorX = float.MaxValue;
            float roundTripErrorY = float.MaxValue;

            if (TryRunPhaseEstimate(second, first, overlapSize, axis, reverse: true, segmentIndex: -1, segmentCount: 1, out var reverse))
            {
                reverseScore = reverse.Score;
                (float reverseOffsetX, float reverseOffsetY) = ComputeRelativeOffset(
                    reverse.BestDeltaX,
                    reverse.BestDeltaY,
                    second,
                    first,
                    overlapSize,
                    reverseOrientation);
                roundTripErrorX = forwardOffsetX + reverseOffsetX;
                roundTripErrorY = forwardOffsetY + reverseOffsetY;
            }

            int validSegments = 0;
            float minSegmentOffsetX = 0.0f;
            float maxSegmentOffsetX = 0.0f;
            float minSegmentOffsetY = 0.0f;
            float maxSegmentOffsetY = 0.0f;

            const int phaseSegmentCount = 3;
            SearchOrientation forwardOrientation = axis == RegistrationAxis.Horizontal
                ? SearchOrientation.HorizontalForward
                : SearchOrientation.VerticalForward;

            for (int segmentIndex = 0; segmentIndex < phaseSegmentCount; segmentIndex++)
            {
                if (!TryRunPhaseEstimate(first, second, overlapSize, axis, reverse: false, segmentIndex, phaseSegmentCount, out var segment))
                    continue;

                if (segment.Score < options.ConfidenceThreshold)
                    continue;

                (float segmentOffsetX, float segmentOffsetY) = ComputeRelativeOffset(
                    segment.BestDeltaX,
                    segment.BestDeltaY,
                    first,
                    second,
                    overlapSize,
                    forwardOrientation);

                if (validSegments == 0)
                {
                    minSegmentOffsetX = maxSegmentOffsetX = segmentOffsetX;
                    minSegmentOffsetY = maxSegmentOffsetY = segmentOffsetY;
                }
                else
                {
                    minSegmentOffsetX = Math.Min(minSegmentOffsetX, segmentOffsetX);
                    maxSegmentOffsetX = Math.Max(maxSegmentOffsetX, segmentOffsetX);
                    minSegmentOffsetY = Math.Min(minSegmentOffsetY, segmentOffsetY);
                    maxSegmentOffsetY = Math.Max(maxSegmentOffsetY, segmentOffsetY);
                }

                validSegments++;
            }

            float segmentSpreadX = validSegments >= 2 ? maxSegmentOffsetX - minSegmentOffsetX : 0.0f;
            float segmentSpreadY = validSegments >= 2 ? maxSegmentOffsetY - minSegmentOffsetY : 0.0f;

            return new PairRegistrationDiagnostics(
                best.Score,
                best.SecondBestScore,
                reverseScore,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                best.GradientCoverage,
                best.Score - best.SecondBestScore,
                roundTripErrorX,
                roundTripErrorY,
                segmentSpreadX,
                segmentSpreadY,
                validSegments);
        }

        private PairRegistrationDiagnostics BuildDiagnostics(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            RegistrationOptions options,
            RegistrationAxis axis,
            BestScoreResult best,
            float forwardOffsetX,
            float forwardOffsetY)
        {
            SearchOrientation forwardOrientation = axis == RegistrationAxis.Horizontal
                ? SearchOrientation.HorizontalForward
                : SearchOrientation.VerticalForward;

            SearchOrientation reverseOrientation = axis == RegistrationAxis.Horizontal
                ? SearchOrientation.HorizontalReverse
                : SearchOrientation.VerticalReverse;

            (float expectedReverseDeltaX, float expectedReverseDeltaY) = ComputeExpectedDeltaForOffset(
                -forwardOffsetX,
                -forwardOffsetY,
                second,
                first,
                overlapSize,
                reverseOrientation);

            var reverse = RunVerificationSearch(
                second,
                first,
                overlapSize,
                options,
                reverseOrientation,
                SearchRegion.Full,
                expectedReverseDeltaX,
                expectedReverseDeltaY);

            (float reverseOffsetX, float reverseOffsetY) = ComputeRelativeOffset(
                reverse.BestDeltaX,
                reverse.BestDeltaY,
                second,
                first,
                overlapSize,
                reverseOrientation);

            float roundTripErrorX = forwardOffsetX + reverseOffsetX;
            float roundTripErrorY = forwardOffsetY + reverseOffsetY;

            int validSegments = 0;
            float minSegmentOffsetX = 0;
            float maxSegmentOffsetX = 0;
            float minSegmentOffsetY = 0;
            float maxSegmentOffsetY = 0;

            var segmentRegions = BuildSegmentRegions(first, second, axis);
            for (int i = 0; i < segmentRegions.Length; i++)
            {
                if (!segmentRegions[i].HasValue)
                    continue;

                var segment = RunVerificationSearch(
                    first,
                    second,
                    overlapSize,
                    options,
                    forwardOrientation,
                    segmentRegions[i]!.Value,
                    best.BestDeltaX,
                    best.BestDeltaY);

                if (segment.Score < options.ConfidenceThreshold)
                    continue;

                (float segmentOffsetX, float segmentOffsetY) = ComputeRelativeOffset(
                    segment.BestDeltaX,
                    segment.BestDeltaY,
                    first,
                    second,
                    overlapSize,
                    forwardOrientation);

                if (validSegments == 0)
                {
                    minSegmentOffsetX = maxSegmentOffsetX = segmentOffsetX;
                    minSegmentOffsetY = maxSegmentOffsetY = segmentOffsetY;
                }
                else
                {
                    minSegmentOffsetX = Math.Min(minSegmentOffsetX, segmentOffsetX);
                    maxSegmentOffsetX = Math.Max(maxSegmentOffsetX, segmentOffsetX);
                    minSegmentOffsetY = Math.Min(minSegmentOffsetY, segmentOffsetY);
                    maxSegmentOffsetY = Math.Max(maxSegmentOffsetY, segmentOffsetY);
                }

                validSegments++;
            }

            float segmentSpreadX = validSegments >= 2 ? maxSegmentOffsetX - minSegmentOffsetX : 0.0f;
            float segmentSpreadY = validSegments >= 2 ? maxSegmentOffsetY - minSegmentOffsetY : 0.0f;

            return new PairRegistrationDiagnostics(
                best.Score,
                best.SecondBestScore,
                reverse.Score,
                best.GradientScore,
                best.BestGradientCandidateScore,
                best.LumaScore,
                best.BestLumaCandidateScore,
                best.GradientCoverage,
                best.Score - best.SecondBestScore,
                roundTripErrorX,
                roundTripErrorY,
                segmentSpreadX,
                segmentSpreadY,
                validSegments);
        }

        private static SearchRegion?[] BuildSegmentRegions(
            GpuImage first,
            GpuImage second,
            RegistrationAxis axis)
        {
            int crossAxisLength = axis == RegistrationAxis.Horizontal
                ? Math.Min(first.Height, second.Height)
                : Math.Min(first.Width, second.Width);

            int usableStart = 1;
            int usableEnd = Math.Max(usableStart + 1, crossAxisLength - 1);
            int usableLength = usableEnd - usableStart;
            if (usableLength < 24)
            {
                return new SearchRegion?[] { null, null, null };
            }

            int segmentLength = usableLength / 3;
            if (segmentLength < 8)
            {
                return new SearchRegion?[] { null, null, null };
            }

            return new SearchRegion?[]
            {
                new SearchRegion(usableStart, usableStart + segmentLength),
                new SearchRegion(usableStart + segmentLength, usableStart + (segmentLength * 2)),
                new SearchRegion(usableStart + (segmentLength * 2), usableEnd),
            };
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

            // 顺序布局就是把每一对相邻图的相对位移累积起来。
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

            // 第一步：把每张图映射到唯一的网格坐标。
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

            // 第二步：构建邻接图，边上保存“从一张图走到另一张图的相对位移”。
            var adjacency = new List<RegistrationEdge>[images.Count];
            for (int i = 0; i < adjacency.Length; i++)
            {
                adjacency[i] = new List<RegistrationEdge>();
            }

            var pairResults = new List<PairRegistrationResult>();

            // 第三步：只计算右邻居和下邻居，避免重复配准。
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

            // 第四步：先用 BFS 生成初始全局坐标，再用加权最小二乘全局优化精炼。
            // BFS 的问题是每个节点只从一条路径获取位置，冗余约束被丢弃。
            // 全局优化会同时考虑所有配准边，让多条路径的误差互相抵消。
            var positionsX = new float[images.Count];
            var positionsY = new float[images.Count];
            var visited = new bool[images.Count];
            var queue = new Queue<int>();
            var anchorCoordinate = coordinates[sortedIndices[0]];

            // BFS 初始化（和之前逻辑一致）
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

            // 第五步：加权最小二乘全局优化
            // 以 BFS 结果为初始值，迭代优化使所有边约束的加权残差平方和最小。
            // 锚点（sortedIndices[0]）固定在 (0,0) 不参与优化。
            Debug.WriteLine("[Registration] BFS 初始坐标:");
            for (int i = 0; i < images.Count; i++)
            {
                Debug.WriteLine($"  图[{i}] ({coordinates[i]}): ({positionsX[i]:F1}, {positionsY[i]:F1})");
            }

            RefinePositionsGlobalLeastSquares(
                positionsX, positionsY, pairResults, images.Count, sortedIndices[0]);

            Debug.WriteLine("[Registration] 全局优化后坐标:");
            for (int i = 0; i < images.Count; i++)
            {
                Debug.WriteLine($"  图[{i}] ({coordinates[i]}): ({positionsX[i]:F1}, {positionsY[i]:F1})");
            }

            layout = BuildLayoutFromPositions(images, positionsX, positionsY, pairResults);
            return true;
        }

        /// <summary>
        /// 加权最小二乘全局位置优化。
        ///
        /// 原理：每条配准边提供一个约束 "pos[target] - pos[source] ≈ (offsetX, offsetY)"。
        /// 我们要找到一组全局坐标使所有约束的加权残差平方和最小：
        ///   min Σ w_e * ||(pos[t] - pos[s]) - (dx_e, dy_e)||²
        ///
        /// 这等价于求解一个稀疏线性方程组 L * pos = b，其中 L 是图的加权拉普拉斯矩阵。
        /// 由于锚点固定，我们用迭代法（加权 Jacobi）求解，简单且对这个规模足够快。
        ///
        /// 权重策略：置信配准边权重 = score²（分数越高越可靠），
        /// 不置信边权重大幅降低，避免错误配准污染全局布局。
        /// </summary>
        private static void RefinePositionsGlobalLeastSquares(
            float[] positionsX,
            float[] positionsY,
            List<PairRegistrationResult> pairResults,
            int nodeCount,
            int anchorIndex)
        {
            if (pairResults.Count == 0 || nodeCount <= 1)
                return;

            const int maxIterations = 200;
            const float convergenceThreshold = 0.01f;
            const float lowConfidenceWeight = 0.02f;

            // 为每条边计算权重
            var edgeWeights = new float[pairResults.Count];
            for (int e = 0; e < pairResults.Count; e++)
            {
                var pr = pairResults[e];
                if (pr.IsConfident && pr.Score > 0)
                {
                    // 高置信边：在 score² 的基础上，再乘一个几何可靠度因子。
                    // 这样“总分高但局部不一致”的边会被自动降权，而不是和理想纯平移边等权。
                    float geometricWeight = 0.25f + (0.75f * pr.GeometryReliability);
                    edgeWeights[e] = pr.Score * pr.Score * geometricWeight;
                }
                else
                {
                    // 低置信边：保留微弱约束防止孤立节点漂移，但不主导布局。
                    // 这类边的位移通常已经回退到 nominal step，因此只作为连续性约束使用。
                    edgeWeights[e] = lowConfidenceWeight;
                }
            }

            var newX = new float[nodeCount];
            var newY = new float[nodeCount];

            for (int iter = 0; iter < maxIterations; iter++)
            {
                // 每个节点收集所有相邻边的加权约束，计算新位置
                Array.Copy(positionsX, newX, nodeCount);
                Array.Copy(positionsY, newY, nodeCount);

                float maxShift = 0;

                for (int node = 0; node < nodeCount; node++)
                {
                    if (node == anchorIndex)
                        continue;

                    float sumWeightedX = 0;
                    float sumWeightedY = 0;
                    float sumWeight = 0;

                    for (int e = 0; e < pairResults.Count; e++)
                    {
                        var pr = pairResults[e];
                        float w = edgeWeights[e];

                        if (pr.SourceIndex == node)
                        {
                            // 这条边说: pos[node] = pos[target] - (offsetX, offsetY)
                            sumWeightedX += w * (positionsX[pr.TargetIndex] - pr.RelativeOffsetX);
                            sumWeightedY += w * (positionsY[pr.TargetIndex] - pr.RelativeOffsetY);
                            sumWeight += w;
                        }
                        else if (pr.TargetIndex == node)
                        {
                            // 这条边说: pos[node] = pos[source] + (offsetX, offsetY)
                            sumWeightedX += w * (positionsX[pr.SourceIndex] + pr.RelativeOffsetX);
                            sumWeightedY += w * (positionsY[pr.SourceIndex] + pr.RelativeOffsetY);
                            sumWeight += w;
                        }
                    }

                    if (sumWeight > 1e-8f)
                    {
                        newX[node] = sumWeightedX / sumWeight;
                        newY[node] = sumWeightedY / sumWeight;

                        float dx = newX[node] - positionsX[node];
                        float dy = newY[node] - positionsY[node];
                        float shift = dx * dx + dy * dy;
                        if (shift > maxShift)
                            maxShift = shift;
                    }
                }

                Array.Copy(newX, positionsX, nodeCount);
                Array.Copy(newY, positionsY, nodeCount);

                if (maxShift < convergenceThreshold * convergenceThreshold)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Registration] 全局优化在第 {iter + 1} 轮收敛 (maxShift={Math.Sqrt(maxShift):F4}px)");
                    break;
                }
            }
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
            // 先找整体包围盒，再把最小坐标平移到 (0, 0)，
            // 这样最终 placement 就能直接作为画布内坐标使用。
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

            // 分数贴图的尺寸由搜索窗口决定，而不是由输入图尺寸决定。
            // 每个像素都表示一个候选位移的得分。
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
                Format = Format.R32G32B32A32_Float,
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
            int searchRangeX,
            int searchRangeY,
            int sampleStep,
            RegistrationOptions options,
            SearchOrientation orientation,
            SearchRegion region,
            int searchCenterX,
            int searchCenterY)
        {
            Debug.WriteLine(
                $"[Reg] UpdateConstants: overlap={overlapSize}, rangeX={searchRangeX}, rangeY={searchRangeY}, center=({searchCenterX},{searchCenterY}), orientation={orientation}, sampleStep={sampleStep}, region=({region.Start},{region.End})");
            var constants = new RegistrationConstants
            {
                FirstWidth = first.Width,
                FirstHeight = first.Height,
                SecondWidth = second.Width,
                SecondHeight = second.Height,
                OverlapSize = overlapSize,
                SearchRangeX = searchRangeX,
                SearchRangeY = searchRangeY,
                SampleStep = sampleStep,
                Orientation = (int)orientation,
                MinSampleCount = options.MinSampleCount,
                RegionStart = region.Start,
                RegionEnd = region.End,
                SearchCenterX = searchCenterX,
                SearchCenterY = searchCenterY,
                MinGradientEnergy = options.MinGradientEnergy,
                MinLumaVariance = options.MinLumaVariance,
                GradientWeight = options.GradientWeight,
                LumaWeight = options.LumaWeight,
            };

            var mapped = _deviceManager.Context.Map(_constantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(constants, mapped.DataPointer, false);
            _deviceManager.Context.Unmap(_constantBuffer!, 0);
        }

        /// <summary>
        /// 当重叠区域很大时，自动提高实际采样步长，减少单精度累加误差并降低 shader 压力。
        /// 这里不会覆盖用户显式给出的更大步长，只会把过小的步长抬到一个更稳妥的下限。
        /// </summary>
        private static int GetEffectiveSampleStep(
            GpuImage first,
            GpuImage second,
            int overlapSize,
            SearchOrientation orientation,
            int requestedSampleStep)
        {
            int effectiveSampleStep = Math.Max(1, requestedSampleStep);
            bool isHorizontal =
                orientation == SearchOrientation.HorizontalForward ||
                orientation == SearchOrientation.HorizontalReverse;

            int crossAxisLength = isHorizontal
                ? Math.Min(first.Height, second.Height)
                : Math.Min(first.Width, second.Width);

            long overlapArea = (long)Math.Max(overlapSize, 1) * Math.Max(crossAxisLength, 1);
            if (overlapArea >= 160000)
            {
                return Math.Max(effectiveSampleStep, 3);
            }

            if (overlapArea >= 40000)
            {
                return Math.Max(effectiveSampleStep, 2);
            }

            return effectiveSampleStep;
        }

        private BestScoreResult ReadBestScore(int width, int height, int searchCenterX, int searchCenterY)
        {
            int bestX = 0;
            int bestY = 0;
            float bestScore = float.NegativeInfinity;
            float bestGradientScore = -2.0f;
            float bestLumaScore = -2.0f;
            float bestGradientCoverage = 0.0f;
            float bestGradientCandidateScore = float.NegativeInfinity;
            float bestLumaCandidateScore = float.NegativeInfinity;
            var scores = new float[width * height];

            // ScoreTexture 是二维搜索图：
            // x 轴对应 deltaX 候选，y 轴对应 deltaY 候选。
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
                            int pixelBase = x * 4;
                            float score = row[pixelBase];
                            float gradientScore = row[pixelBase + 1];
                            float lumaScore = row[pixelBase + 2];
                            scores[(y * width) + x] = score;

                            if (gradientScore > bestGradientCandidateScore)
                                bestGradientCandidateScore = gradientScore;

                            if (lumaScore > bestLumaCandidateScore)
                                bestLumaCandidateScore = lumaScore;

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestGradientScore = gradientScore;
                                bestLumaScore = lumaScore;
                                bestGradientCoverage = row[pixelBase + 3];
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

            float secondBestScore = ComputeSecondBestScore(scores, width, height, bestX, bestY, exclusionRadius: 1);
            float refinedX = bestX + ComputeParabolicSubpixelOffset(scores, width, height, bestX, bestY, isXAxis: true);
            float refinedY = bestY + ComputeParabolicSubpixelOffset(scores, width, height, bestX, bestY, isXAxis: false);

            return new BestScoreResult(
                searchCenterX + refinedX - ((width - 1) / 2.0f),
                searchCenterY + refinedY - ((height - 1) / 2.0f),
                bestScore,
                secondBestScore,
                bestGradientScore,
                bestGradientCandidateScore,
                bestLumaScore,
                bestLumaCandidateScore,
                bestGradientCoverage);
        }

        private static float ComputeSecondBestScore(
            float[] scores,
            int width,
            int height,
            int bestX,
            int bestY,
            int exclusionRadius)
        {
            float secondBestScore = float.NegativeInfinity;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (Math.Abs(x - bestX) <= exclusionRadius &&
                        Math.Abs(y - bestY) <= exclusionRadius)
                    {
                        continue;
                    }

                    float score = scores[(y * width) + x];
                    if (score > secondBestScore)
                    {
                        secondBestScore = score;
                    }
                }
            }

            return float.IsNegativeInfinity(secondBestScore)
                ? scores[(bestY * width) + bestX]
                : secondBestScore;
        }

        private static float ComputeParabolicSubpixelOffset(
            float[] scores,
            int width,
            int height,
            int bestX,
            int bestY,
            bool isXAxis)
        {
            if (isXAxis)
            {
                if (bestX <= 0 || bestX >= width - 1)
                    return 0.0f;

                float left = scores[(bestY * width) + bestX - 1];
                float center = scores[(bestY * width) + bestX];
                float right = scores[(bestY * width) + bestX + 1];
                return ComputeParabolicOffset(left, center, right);
            }

            if (bestY <= 0 || bestY >= height - 1)
                return 0.0f;

            float top = scores[((bestY - 1) * width) + bestX];
            float centerY = scores[(bestY * width) + bestX];
            float bottom = scores[((bestY + 1) * width) + bestX];
            return ComputeParabolicOffset(top, centerY, bottom);
        }

        private static float ComputeParabolicOffset(float negative, float center, float positive)
        {
            float denominator = negative - (2.0f * center) + positive;
            if (Math.Abs(denominator) < 1e-6f)
                return 0.0f;

            float offset = 0.5f * (negative - positive) / denominator;
            if (float.IsNaN(offset) || float.IsInfinity(offset))
                return 0.0f;

            if (offset < -0.5f)
                return -0.5f;
            if (offset > 0.5f)
                return 0.5f;
            return offset;
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
            public BestScoreResult(
                float bestDeltaX,
                float bestDeltaY,
                float score,
                float secondBestScore,
                float gradientScore,
                float bestGradientCandidateScore,
                float lumaScore,
                float bestLumaCandidateScore,
                float gradientCoverage)
            {
                BestDeltaX = bestDeltaX;
                BestDeltaY = bestDeltaY;
                Score = score;
                SecondBestScore = secondBestScore;
                GradientScore = gradientScore;
                BestGradientCandidateScore = bestGradientCandidateScore;
                LumaScore = lumaScore;
                BestLumaCandidateScore = bestLumaCandidateScore;
                GradientCoverage = gradientCoverage;
            }

            public float BestDeltaX { get; }
            public float BestDeltaY { get; }
            public float Score { get; }
            public float SecondBestScore { get; }
            public float GradientScore { get; }
            public float BestGradientCandidateScore { get; }
            public float LumaScore { get; }
            public float BestLumaCandidateScore { get; }
            public float GradientCoverage { get; }
        }

        private enum SearchOrientation
        {
            HorizontalForward = 0,
            VerticalForward = 1,
            HorizontalReverse = 2,
            VerticalReverse = 3,
        }

        private readonly struct SearchRegion
        {
            public static readonly SearchRegion Full = new SearchRegion(1, int.MaxValue);

            public SearchRegion(int start, int end)
            {
                Start = start;
                End = end;
            }

            public int Start { get; }
            public int End { get; }
            public bool IsFull => Start == 1 && End == int.MaxValue;
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
