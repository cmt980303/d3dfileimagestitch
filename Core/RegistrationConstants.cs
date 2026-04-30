using System.Runtime.InteropServices;

namespace GPUStitch.Core
{
    /// <summary>
    /// 传给 RegistrationCS.hlsl 的常量缓冲区结构。
    ///
    /// 该结构需要与 HLSL 端完全同布局：
    /// - 前两组是尺寸与搜索参数；
    /// - 后两组是方向、阈值与权重；
    /// - 整体保持 16 字节对齐。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RegistrationConstants
    {
        public int FirstWidth;
        public int FirstHeight;
        public int SecondWidth;
        public int SecondHeight;

        public int OverlapSize;
        public int SearchRangeX;
        public int SearchRangeY;
        public int SampleStep;

        public int Orientation;
        public int MinSampleCount;
        public int RegionStart;
        public int RegionEnd;

        public float MinGradientEnergy;
        public float MinLumaVariance;
        public float GradientWeight;
        public float LumaWeight;
    }
}
