using System;
using System.Collections.Generic;

namespace GPUStitch.Models
{
    /// <summary>
    /// 全局拼图布局结果。
    ///
    /// 它同时包含：
    /// - 每张图在大画布中的最终位置；
    /// - 画布总尺寸；
    /// - 所有局部配准边的结果，便于调试与状态展示。
    /// </summary>
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

        /// <summary>
        /// 达到置信阈值的配准边数量。
        /// 该值越高，通常说明整体布局越依赖真实配准结果而非回退估计。
        /// </summary>
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

        /// <summary>
        /// 所有配准边的平均评分。
        /// 当图像内容充足且顺序正确时，这个值通常会比较稳定地为正。
        /// </summary>
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

        public int WeakPeakPairCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    if (PairResults[i].Diagnostics.HasWeakPeak)
                        count++;
                }
                return count;
            }
        }

        public int RoundTripMismatchPairCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    if (PairResults[i].Diagnostics.HasRoundTripMismatch)
                        count++;
                }
                return count;
            }
        }

        public int LocalDriftPairCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    if (PairResults[i].Diagnostics.HasLocalDrift)
                        count++;
                }
                return count;
            }
        }

        public float AveragePeakMargin
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.PeakMargin;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageGradientScore
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.GradientScore;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageLumaScore
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.LumaScore;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageBestGradientCandidateScore
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.BestGradientCandidateScore;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageBestLumaCandidateScore
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.BestLumaCandidateScore;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageGradientCoverage
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.GradientCoverage;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageRoundTripError
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.RoundTripErrorMagnitude;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageLocalDrift
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].Diagnostics.SegmentSpreadMagnitude;
                }
                return sum / PairResults.Count;
            }
        }

        public float AverageGeometryReliability
        {
            get
            {
                if (PairResults.Count == 0)
                    return 0;

                float sum = 0;
                for (int i = 0; i < PairResults.Count; i++)
                {
                    sum += PairResults[i].GeometryReliability;
                }
                return sum / PairResults.Count;
            }
        }

        public RegistrationLayout Scale(float scale)
        {
            if (Math.Abs(scale - 1.0f) < 1e-6f)
                return this;

            var placements = new List<ImagePlacement>(Placements.Count);
            for (int i = 0; i < Placements.Count; i++)
            {
                var placement = Placements[i];
                placements.Add(new ImagePlacement
                {
                    OffsetX = placement.OffsetX * scale,
                    OffsetY = placement.OffsetY * scale,
                    Width = placement.Width * scale,
                    Height = placement.Height * scale,
                    FeatherLeft = placement.FeatherLeft * scale,
                    FeatherRight = placement.FeatherRight * scale,
                    FeatherTop = placement.FeatherTop * scale,
                    FeatherBottom = placement.FeatherBottom * scale,
                });
            }

            var pairResults = new List<PairRegistrationResult>(PairResults.Count);
            for (int i = 0; i < PairResults.Count; i++)
            {
                pairResults.Add(PairResults[i].Scale(scale));
            }

            return new RegistrationLayout(
                placements,
                Math.Max(1, (int)Math.Ceiling(CanvasWidth * scale)),
                Math.Max(1, (int)Math.Ceiling(CanvasHeight * scale)),
                pairResults);
        }
    }
}
