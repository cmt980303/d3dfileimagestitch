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
    /// 拼图参数：描述一张图片在输出画布上的位置和尺寸
    /// </summary>
    public struct ImagePlacement
    {
        /// <summary>图片左上角在画布上的 X 偏移（像素）</summary>
        public float OffsetX;
        /// <summary>图片左上角在画布上的 Y 偏移（像素）</summary>
        public float OffsetY;
        /// <summary>图片宽度（像素）</summary>
        public float Width;
        /// <summary>图片高度（像素）</summary>
        public float Height;
    }

    /// <summary>
    /// GPU 拼图器
    /// 使用 D3D11 Compute Shader 在 GPU 上执行图像拼接：
    /// 1. 编译并加载 HLSL 计算着色器
    /// 2. 创建输出纹理（UAV）作为拼接画布
    /// 3. 设置常量缓冲区（每张图片的偏移和尺寸）
    /// 4. Dispatch 计算着色器执行拼接
    /// 5. 将结果复制到共享纹理供 D3DImage 显示
    /// 
    /// 拼接算法：
    /// - 每个 GPU 线程处理输出画布上的一个像素
    /// - 遍历所有源图片，判断像素是否在覆盖范围内
    /// - 重叠区域使用基于距离的线性混合权重，实现平滑过渡
    /// </summary>
    public class GpuStitcher : IDisposable
    {
        private readonly D3DDeviceManager _deviceManager;

        // ===== Compute Shader 相关 =====

        /// <summary>编译后的计算着色器</summary>
        private ID3D11ComputeShader? _computeShader;

        /// <summary>常量缓冲区：存放拼图参数（偏移量、尺寸、图片数量等）</summary>
        private ID3D11Buffer? _constantBuffer;

        /// <summary>线性采样器状态：用于纹理采样时的线性插值</summary>
        private ID3D11SamplerState? _samplerState;

        // ===== 输出纹理 =====

        /// <summary>拼接输出纹理（UAV），Compute Shader 的输出目标</summary>
        private ID3D11Texture2D? _outputTexture;

        /// <summary>输出纹理的无序访问视图（UAV），允许 Compute Shader 随机写入</summary>
        private ID3D11UnorderedAccessView? _outputUav;

        /// <summary>输出纹理的 SRV，用于后续处理或复制</summary>
        private ID3D11ShaderResourceView? _outputSrv;

        /// <summary>当前输出纹理的宽度</summary>
        private int _outputWidth;

        /// <summary>当前输出纹理的高度</summary>
        private int _outputHeight;

        /// <summary>重叠区域的混合宽度（像素），默认 50 像素</summary>
        public float BlendWidth { get; set; } = 50.0f;

        private bool _disposed;

        public GpuStitcher(D3DDeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        /// <summary>
        /// 初始化拼图器：编译着色器、创建采样器和常量缓冲区
        /// 必须在使用 Stitch 方法前调用
        /// </summary>
        public void Initialize()
        {
            CompileShader();
            CreateSamplerState();
            CreateConstantBuffer();
        }

        /// <summary>
        /// 编译 HLSL 计算着色器
        /// 从嵌入资源中读取 StitchCS.hlsl 并使用 D3DCompiler 编译
        /// </summary>
        private void CompileShader()
        {
            // 从程序集嵌入资源中读取 HLSL 源码
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "GPUStitch.Shaders.StitchCS.hlsl";

            string hlslSource;
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException($"找不到嵌入资源: {resourceName}");
                using var reader = new StreamReader(stream);
                hlslSource = reader.ReadToEnd();
            }

            // 编译 HLSL 为字节码
            // Target: cs_5_0（Compute Shader Model 5.0，对应 D3D 11.0）
            // EntryPoint: CSMain（着色器入口函数名）
            // Compile 在编译失败时会抛出 SharpGenException，包含详细错误信息
            ReadOnlyMemory<byte> bytecode;
            try
            {
                bytecode = Compiler.Compile(
                    hlslSource,
                    "CSMain",       // 入口点函数名
                    resourceName,   // 源文件名（用于调试信息）
                    "cs_5_0"        // 着色器模型
                );
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"HLSL 编译失败:\n{ex.Message}", ex);
            }

            // 从编译后的字节码创建 Compute Shader 对象
            _computeShader = _deviceManager.Device.CreateComputeShader(bytecode.Span);
        }

        /// <summary>
        /// 创建线性采样器状态
        /// Compute Shader 在采样源纹理时使用此采样器：
        /// - 线性插值：在纹理坐标之间做双线性插值，避免锯齿
        /// - Clamp 寻址：超出 0~1 范围的坐标取边缘像素值
        /// </summary>
        private void CreateSamplerState()
        {
            // 使用 SamplerDescription 构造函数创建线性+钳位采样器
            var samplerDesc = new SamplerDescription(
                Filter.MinMagMipLinear,       // 线性插值
                TextureAddressMode.Clamp      // 钳位寻址
            );

            _samplerState = _deviceManager.Device.CreateSamplerState(samplerDesc);
        }

        /// <summary>
        /// 创建常量缓冲区
        /// 用于向 Compute Shader 传递拼图参数（图片偏移、尺寸、数量等）
        /// 大小 = StitchConstants 结构体大小，使用 Dynamic + Write 允许 CPU 每帧更新
        /// </summary>
        private void CreateConstantBuffer()
        {
            var bufferDesc = new BufferDescription
            {
                ByteWidth = Marshal.SizeOf<StitchConstants>(),
                Usage = ResourceUsage.Dynamic,           // 动态：CPU 可频繁更新
                BindFlags = BindFlags.ConstantBuffer,    // 作为常量缓冲区绑定
                CPUAccessFlags = CpuAccessFlags.Write,   // CPU 可写入
            };

            _constantBuffer = _deviceManager.Device.CreateBuffer(bufferDesc);
        }

        /// <summary>
        /// 确保输出纹理的尺寸正确
        /// 当画布尺寸变化时重建输出纹理和对应的 UAV/SRV
        /// </summary>
        /// <param name="width">画布宽度</param>
        /// <param name="height">画布高度</param>
        private void EnsureOutputTexture(int width, int height)
        {
            if (_outputWidth == width && _outputHeight == height && _outputTexture != null)
                return;

            // 释放旧资源
            _outputUav?.Dispose();
            _outputSrv?.Dispose();
            _outputTexture?.Dispose();

            _outputWidth = width;
            _outputHeight = height;

            // 创建输出纹理：
            // - BindFlags 包含 UnorderedAccess：允许 Compute Shader 通过 UAV 写入
            // - BindFlags 包含 ShaderResource：允许后续作为 SRV 读取
            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            _outputTexture = _deviceManager.Device.CreateTexture2D(desc);
            _outputUav = _deviceManager.Device.CreateUnorderedAccessView(_outputTexture);
            _outputSrv = _deviceManager.Device.CreateShaderResourceView(_outputTexture);
        }

        /// <summary>
        /// 执行 GPU 拼图
        /// 将多张源图片根据指定的位置信息合成到一张输出纹理上
        /// </summary>
        /// <param name="images">源图片列表（GPU 纹理 + SRV）</param>
        /// <param name="placements">每张图片在画布上的位置和尺寸</param>
        /// <param name="canvasWidth">输出画布宽度</param>
        /// <param name="canvasHeight">输出画布高度</param>
        /// <param name="targetTexture">目标纹理，拼接结果将复制到此纹理（通常是共享纹理）</param>
        public void Stitch(
            List<GpuImage> images,
            List<ImagePlacement> placements,
            int canvasWidth,
            int canvasHeight,
            ID3D11Texture2D targetTexture)
        {
            if (images.Count == 0 || images.Count != placements.Count)
                throw new ArgumentException("图片数量和位置数量必须一致且不为空");

            var ctx = _deviceManager.Context;

            // ---------- 第一步：确保输出纹理尺寸正确 ----------
            EnsureOutputTexture(canvasWidth, canvasHeight);

            // ---------- 第二步：更新常量缓冲区 ----------
            UpdateConstants(placements, canvasWidth, canvasHeight);

            // ---------- 第三步：绑定 Compute Shader 管线状态 ----------

            // 设置计算着色器
            ctx.CSSetShader(_computeShader);

            // 绑定常量缓冲区到 slot 0（对应 HLSL 中的 register(b0)）
            ctx.CSSetConstantBuffer(0, _constantBuffer);

            // 绑定采样器到 slot 0（对应 HLSL 中的 register(s0)）
            ctx.CSSetSampler(0, _samplerState);

            // 绑定源图片的 SRV 到 slot 0~N（对应 HLSL 中的 register(t0)~register(tN)）
            var srvs = new ID3D11ShaderResourceView[8];
            for (int i = 0; i < Math.Min(images.Count, 8); i++)
            {
                srvs[i] = images[i].ShaderResourceView;
            }
            ctx.CSSetShaderResources(0, srvs);

            // 绑定输出纹理的 UAV 到 slot 0（对应 HLSL 中的 register(u0)）
            ctx.CSSetUnorderedAccessView(0, _outputUav);

            // ---------- 第四步：Dispatch 计算着色器 ----------
            // 线程组大小为 8×8（与 HLSL 中的 [numthreads(8,8,1)] 对应）
            // Dispatch 的组数 = ceil(纹理尺寸 / 线程组大小)
            int groupX = (int)Math.Ceiling(canvasWidth / 8.0);
            int groupY = (int)Math.Ceiling(canvasHeight / 8.0);
            ctx.Dispatch(groupX, groupY, 1);

            // ---------- 第五步：清理管线状态 ----------
            // 解绑 SRV 和 UAV，避免资源冲突
            ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[8]);
            ctx.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView?)null);

            // ---------- 第六步：将拼接结果复制到目标共享纹理 ----------
            // CopyResource 是 GPU 到 GPU 的零拷贝操作，速度极快
            ctx.CopyResource(targetTexture, _outputTexture!);
        }

        /// <summary>
        /// 更新常量缓冲区中的拼图参数
        /// 使用 Map/Unmap 将 CPU 数据写入 GPU 的常量缓冲区
        /// </summary>
        private void UpdateConstants(List<ImagePlacement> placements, int canvasWidth, int canvasHeight)
        {
            var constants = new StitchConstants
            {
                OutputWidth = canvasWidth,
                OutputHeight = canvasHeight,
                ImageCount = placements.Count,
                BlendWidth = BlendWidth,
            };

            // 填充每张图片的参数（最多 16 张）
            for (int i = 0; i < Math.Min(placements.Count, 16); i++)
            {
                var p = placements[i];
                constants.SetImageParam(i, p.OffsetX, p.OffsetY, p.Width, p.Height);
            }

            // Map：将常量缓冲区映射到 CPU 可写的内存
            var mapped = _deviceManager.Context.Map(_constantBuffer!, MapMode.WriteDiscard);

            // 将结构体数据复制到映射的内存
            Marshal.StructureToPtr(constants, mapped.DataPointer, false);

            // Unmap：解除映射，数据生效
            _deviceManager.Context.Unmap(_constantBuffer!, 0);
        }

        /// <summary>
        /// 计算将多张图片自动水平排列时所需的画布尺寸和位置
        /// 图片按行排列，相邻图片有指定的重叠宽度
        /// </summary>
        /// <param name="images">源图片列表</param>
        /// <param name="overlapPixels">相邻图片的水平重叠像素数</param>
        /// <returns>画布尺寸和每张图片的位置信息</returns>
        public static (int canvasWidth, int canvasHeight, List<ImagePlacement> placements)
            ComputeHorizontalLayout(List<GpuImage> images, int overlapPixels = 50)
        {
            if (images.Count == 0)
                return (0, 0, new List<ImagePlacement>());

            var placements = new List<ImagePlacement>();
            float currentX = 0;
            int maxHeight = 0;

            for (int i = 0; i < images.Count; i++)
            {
                var img = images[i];
                placements.Add(new ImagePlacement
                {
                    OffsetX = currentX,
                    OffsetY = 0,
                    Width = img.Width,
                    Height = img.Height,
                });

                // 下一张图片的起始位置 = 当前位置 + 图片宽度 - 重叠宽度
                currentX += img.Width - overlapPixels;
                maxHeight = Math.Max(maxHeight, img.Height);
            }

            // 画布总宽度 = 最后一张图片的右边界
            int canvasWidth = (int)Math.Ceiling(currentX + overlapPixels);
            return (canvasWidth, maxHeight, placements);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _outputUav?.Dispose();
            _outputSrv?.Dispose();
            _outputTexture?.Dispose();
            _constantBuffer?.Dispose();
            _samplerState?.Dispose();
            _computeShader?.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    // ===== 常量缓冲区数据结构 =====

    /// <summary>
    /// Compute Shader 的常量缓冲区数据结构
    /// 必须与 HLSL 中的 cbuffer StitchParams 布局完全一致
    /// 使用 LayoutKind.Sequential 确保内存布局与 HLSL 对齐
    /// 
    /// 注意：HLSL 常量缓冲区要求 16 字节对齐
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StitchConstants
    {
        // float4 ImageParams[16]：每张图片的 (offsetX, offsetY, width, height)
        // 展开为 16 × 4 = 64 个 float
        public Float4 Img0;
        public Float4 Img1;
        public Float4 Img2;
        public Float4 Img3;
        public Float4 Img4;
        public Float4 Img5;
        public Float4 Img6;
        public Float4 Img7;
        public Float4 Img8;
        public Float4 Img9;
        public Float4 Img10;
        public Float4 Img11;
        public Float4 Img12;
        public Float4 Img13;
        public Float4 Img14;
        public Float4 Img15;

        // float2 OutputSize
        public float OutputWidth;
        public float OutputHeight;

        // int ImageCount
        public int ImageCount;

        // float BlendWidth
        public float BlendWidth;

        /// <summary>
        /// 设置指定索引图片的参数
        /// </summary>
        public void SetImageParam(int index, float offsetX, float offsetY, float width, float height)
        {
            var value = new Float4(offsetX, offsetY, width, height);
            switch (index)
            {
                case 0: Img0 = value; break;
                case 1: Img1 = value; break;
                case 2: Img2 = value; break;
                case 3: Img3 = value; break;
                case 4: Img4 = value; break;
                case 5: Img5 = value; break;
                case 6: Img6 = value; break;
                case 7: Img7 = value; break;
                case 8: Img8 = value; break;
                case 9: Img9 = value; break;
                case 10: Img10 = value; break;
                case 11: Img11 = value; break;
                case 12: Img12 = value; break;
                case 13: Img13 = value; break;
                case 14: Img14 = value; break;
                case 15: Img15 = value; break;
            }
        }
    }

    /// <summary>
    /// 4 分量浮点向量，与 HLSL 的 float4 对应
    /// 用于常量缓冲区的数据传输
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Float4
    {
        public float X, Y, Z, W;
        public Float4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    }
}
