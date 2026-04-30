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
    /// GPU 拼图器。
    ///
    /// 与旧版“一次绑定多张图统一计算”的方式不同，当前实现采用“两阶段”策略：
    /// 1. 每次只绑定 1 张源图，将其累加到浮点累积纹理；
    /// 2. 所有图处理完成后，再做一次归一化输出。
    ///
    /// 这样做的核心收益是：
    /// - 不再受 D3D11 shader resource slot 数量限制；
    /// - 理论上可以处理任意多张图，瓶颈转为时间与显存；
    /// - 更适合后续做分块、流式、大图预览。
    ///
    /// 这里的“累积纹理”本质上保存的是：
    /// - RGB：加权后的颜色和；
    /// - A：对应位置累计权重和。
    ///
    /// 最终只需要再做一次 rgb / a，就能得到经过羽化融合的结果。
    /// </summary>
    public sealed class GpuStitcher : IDisposable
    {
        private const int ThreadGroupSize = 8;

        private readonly D3DDeviceManager _deviceManager;

        private ID3D11ComputeShader? _accumulateShader;
        private ID3D11ComputeShader? _clearShader;
        private ID3D11ComputeShader? _finalizeShader;
        private ID3D11Buffer? _imageConstantBuffer;
        private ID3D11Buffer? _finalizeConstantBuffer;
        private ID3D11SamplerState? _samplerState;

        private ID3D11Texture2D? _accumTexture;
        private ID3D11ShaderResourceView? _accumSrv;
        private ID3D11UnorderedAccessView? _accumUav;

        private ID3D11Texture2D? _outputTexture;
        private ID3D11UnorderedAccessView? _outputUav;

        private int _outputWidth;
        private int _outputHeight;
        private bool _disposed;

        /// <summary>
        /// 重叠区域的羽化宽度，单位为像素。
        /// 数值越大，接缝越柔和；数值过大则会在轻微错位时显得发虚。
        /// </summary>
        public float BlendWidth { get; set; } = 50.0f;

        public GpuStitcher(D3DDeviceManager deviceManager)
        {
            _deviceManager = deviceManager;
        }

        /// <summary>
        /// 初始化 GPU 拼图所需的着色器、采样器、常量缓冲区。
        /// 该方法应在首次调用 Stitch 之前执行一次。
        /// </summary>
        public void Initialize()
        {
            // 三个着色器分别负责：单张图累加、清空累积纹理、最终归一化输出。
            _accumulateShader = CompileShader("GPUStitch.Shaders.StitchCS.hlsl");
            _clearShader = CompileShader("GPUStitch.Shaders.StitchClearCS.hlsl");
            _finalizeShader = CompileShader("GPUStitch.Shaders.StitchFinalizeCS.hlsl");
            _samplerState = CreateSamplerState();
            _imageConstantBuffer = CreateConstantBuffer<StitchImageConstants>();
            _finalizeConstantBuffer = CreateConstantBuffer<StitchFinalizeConstants>();
        }

        /// <summary>
        /// 执行拼图。
        /// 输入图片数量不再受 16 张限制，当前实现会逐张把图像累加到浮点画布。
        /// </summary>
        public void Stitch(
            List<GpuImage> images,
            List<ImagePlacement> placements,
            int canvasWidth,
            int canvasHeight,
            ID3D11Texture2D targetTexture)
        {
            if (images == null)
                throw new ArgumentNullException(nameof(images));
            if (placements == null)
                throw new ArgumentNullException(nameof(placements));
            if (targetTexture == null)
                throw new ArgumentNullException(nameof(targetTexture));
            if (images.Count == 0 || images.Count != placements.Count)
                throw new ArgumentException("图片数量和位置数量必须一致且不为空");

            // 经典一次性拼图路径：清空 -> 累加所有图 -> 归一化 -> 输出。
            EnsureOutputResources(canvasWidth, canvasHeight);
            ClearAccumulationTexture();

            for (int i = 0; i < images.Count; i++)
            {
                AccumulateSingleImage(images[i], placements[i]);
            }

            FinalizeOutput(canvasWidth, canvasHeight);
            _deviceManager.Context.CopyResource(targetTexture, _outputTexture!);
        }

        /// <summary>
        /// 为增量累加准备画布：创建/调整资源并清空累积纹理。
        /// 在渐进式导入场景中，先调用此方法，再逐张调用 AccumulateImage。
        /// </summary>
        public void PrepareCanvas(int canvasWidth, int canvasHeight)
        {
            Debug.WriteLine($"[Stitch] PrepareCanvas: {canvasWidth}x{canvasHeight}, BlendWidth={BlendWidth:F1}");
            EnsureOutputResources(canvasWidth, canvasHeight);
            ClearAccumulationTexture();
        }

        /// <summary>
        /// 把单张图像累加到已有的累积纹理上，不清空也不归一化。
        /// 用于渐进式导入场景：每加载一张图就调用一次。
        /// </summary>
        public void AccumulateImage(GpuImage image, ImagePlacement placement)
        {
            Debug.WriteLine(
                $"[Stitch] Accumulate: offset=({placement.OffsetX:F1},{placement.OffsetY:F1}), " +
                $"size=({placement.Width:F1},{placement.Height:F1}), " +
                $"feather=L{placement.FeatherLeft:F0}/R{placement.FeatherRight:F0}/T{placement.FeatherTop:F0}/B{placement.FeatherBottom:F0}");
            AccumulateSingleImage(image, placement);
        }

        /// <summary>
        /// 将当前累积结果归一化并拷贝到目标纹理。
        /// 可在每次 AccumulateImage 后调用以刷新显示。
        /// </summary>
        public void FinalizeAndCopy(
            int canvasWidth, int canvasHeight, ID3D11Texture2D targetTexture)
        {
            FinalizeOutput(canvasWidth, canvasHeight);
            _deviceManager.Context.CopyResource(targetTexture, _outputTexture!);
        }

        private ID3D11ComputeShader CompileShader(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();

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
                throw new InvalidOperationException($"编译着色器失败:\n{ex.Message}", ex);
            }

            return _deviceManager.Device.CreateComputeShader(bytecode.Span);
        }

        private ID3D11SamplerState CreateSamplerState()
        {
            // 线性采样 + Clamp 足以满足当前缩放预览场景，避免边缘采样到图外内容。
            var desc = new SamplerDescription(
                Filter.MinMagMipLinear,
                TextureAddressMode.Clamp);

            return _deviceManager.Device.CreateSamplerState(desc);
        }

        private ID3D11Buffer CreateConstantBuffer<T>() where T : struct
        {
            // 这些常量缓冲区会被频繁改写，因此使用 Dynamic + CPU Write。
            var desc = new BufferDescription
            {
                ByteWidth = Marshal.SizeOf<T>(),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write,
            };

            return _deviceManager.Device.CreateBuffer(desc);
        }

        private void EnsureOutputResources(int width, int height)
        {
            if (_outputWidth == width && _outputHeight == height &&
                _accumTexture != null && _outputTexture != null)
            {
                return;
            }

            // 尺寸变化时必须整套重建，因为 UAV/SRV 都绑定在具体纹理对象上。
            _accumSrv?.Dispose();
            _accumUav?.Dispose();
            _accumTexture?.Dispose();
            _outputUav?.Dispose();
            _outputTexture?.Dispose();

            _outputWidth = width;
            _outputHeight = height;

            // 累积纹理使用 128-bit float，是因为它既要存颜色和，也要存权重和，
            // 使用高精度格式可以降低多张图叠加后的量化误差。
            var accumDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            _accumTexture = _deviceManager.Device.CreateTexture2D(accumDesc);
            _accumSrv = _deviceManager.Device.CreateShaderResourceView(_accumTexture);
            _accumUav = _deviceManager.Device.CreateUnorderedAccessView(_accumTexture);

            // 最终输出纹理只用于显示，所以回到 BGRA8 即可，节省显存和带宽。
            var outputDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            _outputTexture = _deviceManager.Device.CreateTexture2D(outputDesc);
            _outputUav = _deviceManager.Device.CreateUnorderedAccessView(_outputTexture);
        }

        private void ClearAccumulationTexture()
        {
            UpdateFinalizeConstants(_outputWidth, _outputHeight);

            var ctx = _deviceManager.Context;
            ctx.CSSetShader(_clearShader);
            ctx.CSSetConstantBuffer(0, _finalizeConstantBuffer);
            ctx.CSSetUnorderedAccessView(0, _accumUav);
            ctx.Dispatch(
                (_outputWidth + ThreadGroupSize - 1) / ThreadGroupSize,
                (_outputHeight + ThreadGroupSize - 1) / ThreadGroupSize,
                1);
            ctx.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView?)null);
        }

        /// <summary>
        /// 把一张图累加到浮点累积画布。
        /// Dispatch 范围只覆盖这张图的显示区域，而不是整张输出画布，
        /// 因此图很多时的效率会明显好于“每张图都扫描整张大画布”。
        /// </summary>
        private void AccumulateSingleImage(GpuImage image, ImagePlacement placement)
        {
            // 这里按 placement 的包围盒而不是简单的 width/height dispatch，
            // 这样在 offset 带有小数时，边缘额外覆盖到的那 1 个像素也能被正确处理。
            int imageWidth = Math.Max(
                1,
                (int)Math.Ceiling(placement.OffsetX + placement.Width) - (int)Math.Floor(placement.OffsetX));
            int imageHeight = Math.Max(
                1,
                (int)Math.Ceiling(placement.OffsetY + placement.Height) - (int)Math.Floor(placement.OffsetY));

            UpdateImageConstants(placement);

            var ctx = _deviceManager.Context;
            ctx.CSSetShader(_accumulateShader);
            ctx.CSSetConstantBuffer(0, _imageConstantBuffer);
            ctx.CSSetSampler(0, _samplerState);
            ctx.CSSetShaderResource(0, image.ShaderResourceView);
            ctx.CSSetUnorderedAccessView(0, _accumUav);

            ctx.Dispatch(
                (imageWidth + ThreadGroupSize - 1) / ThreadGroupSize,
                (imageHeight + ThreadGroupSize - 1) / ThreadGroupSize,
                1);

            // 解绑 SRV/UAV，避免后续其他 Pass 误复用同一绑定状态。
            ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[1]);
            ctx.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView?)null);
        }

        /// <summary>
        /// 将浮点累积结果归一化为最终输出纹理。
        /// </summary>
        private void FinalizeOutput(int canvasWidth, int canvasHeight)
        {
            UpdateFinalizeConstants(canvasWidth, canvasHeight);

            var ctx = _deviceManager.Context;
            ctx.CSSetShader(_finalizeShader);
            ctx.CSSetConstantBuffer(0, _finalizeConstantBuffer);
            ctx.CSSetShaderResource(0, _accumSrv);
            ctx.CSSetUnorderedAccessView(0, _outputUav);

            ctx.Dispatch(
                (canvasWidth + ThreadGroupSize - 1) / ThreadGroupSize,
                (canvasHeight + ThreadGroupSize - 1) / ThreadGroupSize,
                1);

            ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView[1]);
            ctx.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView?)null);
        }

        private void UpdateImageConstants(ImagePlacement placement)
        {
            // 这里把托管侧布局模型转换成 HLSL 常量缓冲区。
            // 要特别注意字段顺序必须和着色器中的 cbuffer 严格一致。
            var constants = new StitchImageConstants
            {
                ImageParam = new Float4(placement.OffsetX, placement.OffsetY, placement.Width, placement.Height),
                FeatherParam = new Float4(
                    placement.FeatherLeft,
                    placement.FeatherRight,
                    placement.FeatherTop,
                    placement.FeatherBottom),
                OutputWidth = _outputWidth,
                OutputHeight = _outputHeight,
                BlendWidth = BlendWidth,
            };

            var mapped = _deviceManager.Context.Map(_imageConstantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(constants, mapped.DataPointer, false);
            _deviceManager.Context.Unmap(_imageConstantBuffer!, 0);
        }

        private void UpdateFinalizeConstants(int canvasWidth, int canvasHeight)
        {
            var constants = new StitchFinalizeConstants
            {
                OutputWidth = canvasWidth,
                OutputHeight = canvasHeight,
            };

            var mapped = _deviceManager.Context.Map(_finalizeConstantBuffer!, MapMode.WriteDiscard);
            Marshal.StructureToPtr(constants, mapped.DataPointer, false);
            _deviceManager.Context.Unmap(_finalizeConstantBuffer!, 0);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // 释放顺序按照“视图 -> 纹理 -> 缓冲区 -> 着色器”展开，便于阅读与排查。
            _accumSrv?.Dispose();
            _accumUav?.Dispose();
            _accumTexture?.Dispose();
            _outputUav?.Dispose();
            _outputTexture?.Dispose();
            _imageConstantBuffer?.Dispose();
            _finalizeConstantBuffer?.Dispose();
            _samplerState?.Dispose();
            _accumulateShader?.Dispose();
            _clearShader?.Dispose();
            _finalizeShader?.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 每次累加一张图时传给 HLSL 的常量缓冲区结构。
    /// 布局必须与 StitchCS.hlsl 中的 StitchImageParams 完全一致。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StitchImageConstants
    {
        public Float4 ImageParam;
        public Float4 FeatherParam;
        public float OutputWidth;
        public float OutputHeight;
        public float BlendWidth;
        public float Padding0;
    }

    /// <summary>
    /// 输出归一化阶段使用的常量缓冲区。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StitchFinalizeConstants
    {
        public float OutputWidth;
        public float OutputHeight;
        public float Padding0;
        public float Padding1;
    }

    /// <summary>
    /// 与 HLSL float4 对应的托管结构体。
    /// 只用于常量缓冲区数据传输。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Float4
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public Float4(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }
}
