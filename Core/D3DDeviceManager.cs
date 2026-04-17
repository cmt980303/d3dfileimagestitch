using SharpGen.Runtime;
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace GPUStitch.Core
{
    /// <summary>
    /// D3D 设备管理器
    /// 
    /// 核心设计（参考官方 WPFDXInterop C++ 示例）：
    /// 1. 创建自己的 D3D11 设备（与 D3D11Image 内部设备是不同实例）
    /// 2. D3D11Image OnRender 传入的 surface 是"共享纹理"（MiscFlags=Shared）
    /// 3. 通过 IDXGIResource.SharedHandle 获取共享句柄
    /// 4. 用 OpenSharedResource 在自己的设备上打开同一块 GPU 显存
    /// 5. 在自己的设备上渲染（CopyResource / Dispatch），然后 Flush
    /// 
    /// 这样所有 GPU 资源都在同一设备上，不会有跨设备错误。
    /// </summary>
    public class D3DDeviceManager : IDisposable
    {
        /// <summary>我们创建的 D3D11 设备</summary>
        public ID3D11Device Device { get; private set; } = null!;

        /// <summary>即时上下文，用于提交 GPU 命令</summary>
        public ID3D11DeviceContext Context { get; private set; } = null!;

        /// <summary>当前在我们设备上打开的共享渲染目标</summary>
        private ID3D11Texture2D? _sharedRenderTarget;

        private bool _disposed;

        /// <summary>
        /// 创建自己的 D3D11 设备。
        /// 使用 BgraSupport 标志，确保与 D3D11Image 的 B8G8R8A8_UNorm 格式兼容。
        /// 可在 Window_Loaded 中调用，不需要等 surface。
        /// </summary>
        public void Initialize()
        {
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
            };

            // 创建硬件加速的 D3D11 设备
            var result = D3D11CreateDevice(
                IntPtr.Zero,                         // 使用默认适配器
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,      // WPF 要求 BGRA 支持
                featureLevels,
                out var device,
                out _,
                out var context);

            result.CheckError();

            Device = device;
            Context = context;
        }

        /// <summary>
        /// 从 D3D11Image 的 surface 指针打开共享纹理到我们的设备上。
        /// 
        /// 流程（与官方 C++ 示例 InitRenderTarget 一致）：
        /// 1. Marshal.QueryInterface 获取 IDXGIResource 接口指针
        /// 2. 通过 vtable 调用 GetSharedHandle 获取 DXGI 共享句柄
        /// 3. OpenSharedResource 在我们的设备上打开同一块 GPU 显存
        /// 
        /// 注意：不使用 ComObject 包装 surfacePointer，避免引用计数冲突。
        /// surfacePointer 的生命周期由 D3D11Image 管理，我们只读不拥有。
        /// </summary>
        public ID3D11Texture2D OpenSharedSurface(IntPtr surfacePointer)
        {
            if (surfacePointer == IntPtr.Zero)
                throw new ArgumentException("surface 指针不能为空", nameof(surfacePointer));

            // 释放之前的共享纹理
            _sharedRenderTarget?.Dispose();
            _sharedRenderTarget = null;

            var surface = new ComObject(surfacePointer).QueryInterface<IDXGIResource>();
            var sharedHandle = surface.SharedHandle;
            // 获取共享句柄（纯 Marshal 调用，不涉及 ComObject 包装器）
            //IntPtr sharedHandle = GetDXGISharedHandle(surfacePointer);

            if (sharedHandle == IntPtr.Zero)
                throw new InvalidOperationException("无法获取 D3D11Image surface 的共享句柄");

            // 在我们的设备上打开共享资源
            _sharedRenderTarget = Device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);

            return _sharedRenderTarget;
        }

        /// <summary>
        /// IDXGIResource::GetSharedHandle 的委托定义（COM StdCall 约定）
        /// vtable slot 8: IUnknown(3) + IDXGIObject(4) + IDXGIDeviceSubObject(1) = 8
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSharedHandleDelegate(IntPtr pThis, out IntPtr pSharedHandle);

        /// <summary>
        /// 通过原生 COM 调用从 surface 指针获取 DXGI 共享句柄。
        /// 完全使用 Marshal API，不依赖任何 ComObject 包装器，
        /// 确保 D3D11Image 的 surface 引用计数不被意外修改。
        /// </summary>
        private static IntPtr GetDXGISharedHandle(IntPtr surfacePointer)
        {
            // 步骤 1：QI 获取 IDXGIResource 接口（AddRef 由 QI 内部完成）
            Guid iidDxgiResource = typeof(IDXGIResource).GUID;
            int hr = Marshal.QueryInterface(surfacePointer, ref iidDxgiResource, out IntPtr pDxgiResource);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                // 步骤 2：从 vtable 读取 GetSharedHandle 函数指针
                // COM 对象内存布局：[vtable_ptr, ...]
                // vtable 布局：[QI, AddRef, Release, ..., GetSharedHandle(slot 8), ...]
                IntPtr vtable = Marshal.ReadIntPtr(pDxgiResource);
                IntPtr fnGetSharedHandle = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);

                // 步骤 3：调用 GetSharedHandle(this, out handle)
                var getSharedHandle = Marshal.GetDelegateForFunctionPointer<GetSharedHandleDelegate>(fnGetSharedHandle);
                hr = getSharedHandle(pDxgiResource, out IntPtr sharedHandle);
                Marshal.ThrowExceptionForHR(hr);

                return sharedHandle;
            }
            finally
            {
                // 步骤 4：释放 QI 获取的引用（与 QI 的 AddRef 配对）
                Marshal.Release(pDxgiResource);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sharedRenderTarget?.Dispose();
            Context?.Dispose();
            Device?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
