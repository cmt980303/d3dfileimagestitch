# GPUStitch 项目说明

## 项目目标

这是一个基于 **WPF + D3D11 + Compute Shader** 的显微图像拼接原型工程。  
当前重点是让大量显微图片能够：

1. 先按预算加载成可交互的预览
2. 在导入过程中渐进式显示“逐张贴到大画布”的效果
3. 再基于 GPU 配准得到更准确的全局布局
4. 最终用 GPU 羽化混合得到拼接预览结果

## 当前代码结构

### `Models`

存放纯数据模型和跨模块共享的值对象：

- `GpuImage`：GPU 纹理与 SRV 的封装
- `ImageFileMetadata`：图片元数据
- `ImageLoadPlan`：导入预算结果
- `ImagePlacement`：单张图在大画布中的放置参数
- `GridImageCoordinate`：从文件名解析出的网格坐标
- `RegistrationAxis`
- `PairRegistrationResult`
- `RegistrationLayout`
- `RegistrationOptions`
- `StitchParameterRecommendation`

### `Core`

存放核心算法和 GPU / 资源管理逻辑：

- `D3DDeviceManager`：D3D11 设备与 WPF 共享表面互操作
- `GpuImageLoader`：图片解码并上传为 GPU 纹理
- `ImageLoadPlanner`：导入预算与缩放比例规划
- `GpuRegistration`：相邻图像配准和全局布局计算
- `GpuStitcher`：单张图增量累加 + 最终归一化拼图
- `StitchParameterAdvisor`：启发式推荐 overlap / blend 参数
- `RegistrationConstants`：RegistrationCS 对应常量缓冲区结构

### `Shaders`

当前着色器包括：

- `StitchCS.hlsl`：逐张图像累加到浮点画布
- `StitchClearCS.hlsl`：清空累积纹理
- `StitchFinalizeCS.hlsl`：归一化输出预览图
- `RegistrationCS.hlsl`：候选位移搜索评分

### `docs`

存放 HLSL 对应的详细讲解文档。  
HLSL 源码尽量保持简洁，详细中文解释写在 Markdown 中。

### `MainWindow.xaml(.cs)`

UI 层调度中心，负责：

- 文件选择
- 导入任务管理
- 渐进式预览
- 调用配准与拼图核心模块
- 管理 D3D11Image 显示状态

## 当前实现状态

当前代码已经具备：

1. **导入预算控制**：加载前先估算源图资源占用，必要时统一降采样
2. **渐进式导入预览**：图片不必等全部加载完成才开始显示
3. **网格命名识别**：支持从文件名解析三位行号 + 三位列号
4. **GPU 配准**：对相邻图做局部搜索，并传播成全局布局
5. **GPU 拼图**：使用“累积纹理 + 归一化输出”的两阶段拼图
6. **增量累加优化**：新图到来时只累加新增图像，避免每帧全量重拼
7. **缩小时条纹修复**：预览布局做整数像素对齐，着色器羽化改为更平滑的权重函数

## 关键设计约定

### 1. 预览链路和原始数据链路分开

- 预览可以降采样
- 配准和预览当前都基于“已加载到 GPU 的预览图”
- 不要默认把“当前预览尺寸”当作“原图真实尺寸”

### 2. 拼图采用两阶段模型

- `StitchCS`：只做单图累加
- `StitchFinalizeCS`：统一做归一化输出

这样更适合：

- 渐进式导入
- 大量图片
- 后续 tile-based 大图导出

### 3. 文件名网格坐标约定

默认按以下规则解析：

- 前三位数字：行号
- 后三位数字：列号

例如：

- `020016.jpg`
- `020 016.jpg`

都会被识别为 `Row=20, Column=16`。

### 4. 常量缓冲区必须和 HLSL 严格同布局

修改任何 HLSL `cbuffer` 时，必须同步检查对应 C# 结构：

- `StitchImageConstants`
- `StitchFinalizeConstants`
- `RegistrationConstants`

不要只改一侧。

### 5. WPF 预览稳定性优先

当前预览展示依赖 `D3D11Image`，因此要特别注意：

- 共享 surface 的生命周期
- 画布尺寸变化时的资源重建
- placement 的整数像素对齐
- 避免在 `RenderFrame` 中做重复的全量工作

## 继续开发时的建议

1. 纯数据模型优先放在 `Models`
2. GPU / 算法逻辑放在 `Core`
3. 复杂着色器说明写进 `docs`
4. 如果修改渲染链路，优先检查 `MainWindow.RenderFrame`、`GpuStitcher` 和对应 HLSL 是否同步
5. 如果修改配准逻辑，优先同步检查 `RegistrationOptions`、`RegistrationConstants`、`RegistrationCS.hlsl`
