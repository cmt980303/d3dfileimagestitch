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
        private const float WeakPeakMarginThreshold = 0.015f;
        private const float InconsistentRoundTripThreshold = 1.25f;
        private const float LocalDriftThreshold = 1.50f;

        public PairRegistrationDiagnostics(
            float bestScore,
            float secondBestScore,
            float reverseBestScore,
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

        public bool HasWeakPeak => PeakMargin < WeakPeakMarginThreshold;
        public bool HasRoundTripMismatch => RoundTripErrorMagnitude > InconsistentRoundTripThreshold;
        public bool HasLocalDrift => ValidSegmentCount >= 2 && SegmentSpreadMagnitude > LocalDriftThreshold;
    }
}
