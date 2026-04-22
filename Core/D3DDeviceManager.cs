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
    /// D3D11 设备管理器。
    ///
    /// 整体协作方式如下：
    /// 1. 本类创建自己的 D3D11 设备和即时上下文；
    /// 2. WPF 在回调中给出一个共享 surface 指针；
    /// 3. 本类把该 surface 转成 IDXGIResource 并取出 SharedHandle；
    /// 4. 再用自己的设备打开同一块显存；
    /// 5. 拼图和配准模块都在“自己的设备”上工作；
    /// 6. 最终结果通过共享资源显示到 WPF 界面。
    ///
    /// 这样可以避免跨设备拷贝和资源不兼容问题，也把底层互操作复杂度收敛到一个类中。
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
            // 按从高到低的特性级别尝试，优先使用完整 D3D11 功能。
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
            };

            // 创建硬件加速设备，并启用 BGRA 支持以兼容 WPF 的像素格式要求。
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

            // 同一个窗口在尺寸变化或表面重建后可能产生新的共享表面，
            // 因此这里每次重新打开前都先释放旧引用。
            _sharedRenderTarget?.Dispose();
            _sharedRenderTarget = null;

            var surface = new ComObject(surfacePointer).QueryInterface<IDXGIResource>();
            var sharedHandle = surface.SharedHandle;

            if (sharedHandle == IntPtr.Zero)
                throw new InvalidOperationException("无法获取 D3D11Image surface 的共享句柄");

            // 在我们自己的设备上重新打开这块显存，后续所有渲染命令都针对这个对象执行。
            _sharedRenderTarget = Device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);

            return _sharedRenderTarget;
        }

        #region 先注释掉的内容

        
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
        #endregion
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
