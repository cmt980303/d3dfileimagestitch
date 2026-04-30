using System;

namespace GPUStitch.Models
{
    /// <summary>
    /// 单对配准的可量化诊断信息。
    /// 这些指标的目标不是直接替代最终配准分数，而是帮助判断：
    /// 1. 最优峰值是否足够尖锐；
    /// 2. 正反向搜索是否互相一致；
    /// 3. 重叠区不同局部分段是否支持同一组位移。
    /// </summary>
    public sealed class PairRegistrationDiagnostics
    {
        private const float WeakPeakMarginThreshold = 0.050f;
        private const float StrongPeakMarginThreshold = 0.090f;
        private const float StableRoundTripThreshold = 0.25f;
        private const float InconsistentRoundTripThreshold = 0.75f;
        private const float StableLocalDriftThreshold = 0.75f;
        private const float LocalDriftThreshold = 2.00f;

        public PairRegistrationDiagnostics(
            float bestScore,
            float secondBestScore,
            float reverseBestScore,
            float gradientScore,
            float bestGradientCandidateScore,
            float lumaScore,
            float bestLumaCandidateScore,
            float gradientCoverage,
            float peakMargin,
            float roundTripErrorX,
            float roundTripErrorY,
            float segmentSpreadX,
            float segmentSpreadY,
            int validSegmentCount)
        {
            BestScore = bestScore;
            SecondBestScore = secondBestScore;
            ReverseBestScore = reverseBestScore;
            GradientScore = gradientScore;
            BestGradientCandidateScore = bestGradientCandidateScore;
            LumaScore = lumaScore;
            BestLumaCandidateScore = bestLumaCandidateScore;
            GradientCoverage = gradientCoverage;
            PeakMargin = peakMargin;
            RoundTripErrorX = roundTripErrorX;
            RoundTripErrorY = roundTripErrorY;
            SegmentSpreadX = segmentSpreadX;
            SegmentSpreadY = segmentSpreadY;
            ValidSegmentCount = validSegmentCount;
        }

        public float BestScore { get; }
        public float SecondBestScore { get; }
        public float ReverseBestScore { get; }
        public float GradientScore { get; }
        public float BestGradientCandidateScore { get; }
        public float LumaScore { get; }
        public float BestLumaCandidateScore { get; }
        public float GradientCoverage { get; }
        public float PeakMargin { get; }
        public float RoundTripErrorX { get; }
        public float RoundTripErrorY { get; }
        public float SegmentSpreadX { get; }
        public float SegmentSpreadY { get; }
        public int ValidSegmentCount { get; }

        public float RoundTripErrorMagnitude =>
            (float)Math.Sqrt((RoundTripErrorX * RoundTripErrorX) + (RoundTripErrorY * RoundTripErrorY));

        public float SegmentSpreadMagnitude =>
            (float)Math.Sqrt((SegmentSpreadX * SegmentSpreadX) + (SegmentSpreadY * SegmentSpreadY));

        public float PeakReliability => Normalize01(PeakMargin, WeakPeakMarginThreshold, StrongPeakMarginThreshold);

        public float RoundTripReliability =>
            1.0f - Normalize01(RoundTripErrorMagnitude, StableRoundTripThreshold, InconsistentRoundTripThreshold);

        public float LocalDriftReliability =>
            ValidSegmentCount < 2
                ? 1.0f
                : 1.0f - Normalize01(SegmentSpreadMagnitude, StableLocalDriftThreshold, LocalDriftThreshold);

        public float GeometryReliability => PeakReliability * RoundTripReliability * LocalDriftReliability;

        public bool HasWeakPeak => PeakMargin < WeakPeakMarginThreshold;
        public bool HasRoundTripMismatch => RoundTripErrorMagnitude > InconsistentRoundTripThreshold;
        public bool HasLocalDrift => ValidSegmentCount >= 2 && SegmentSpreadMagnitude > LocalDriftThreshold;

        public PairRegistrationDiagnostics Scale(float scale)
        {
            if (Math.Abs(scale - 1.0f) < 1e-6f)
                return this;

            return new PairRegistrationDiagnostics(
                BestScore,
                SecondBestScore,
                ReverseBestScore,
                GradientScore,
                BestGradientCandidateScore,
                LumaScore,
                BestLumaCandidateScore,
                GradientCoverage,
                PeakMargin,
                RoundTripErrorX * scale,
                RoundTripErrorY * scale,
                SegmentSpreadX * scale,
                SegmentSpreadY * scale,
                ValidSegmentCount);
        }

        private static float Normalize01(float value, float min, float max)
        {
            if (value <= min)
                return 0.0f;
            if (value >= max)
                return 1.0f;

            return (value - min) / (max - min);
        }
    }
}
