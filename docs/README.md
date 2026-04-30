# 渲染与配准文档

这个目录用于解释当前项目中的渲染链路、配准链路和相关 HLSL 文件。

之所以把说明单独放在 Markdown 中，而不是把大量中文说明直接塞进源码里，主要有两个原因：

1. 源码和着色器文件本身需要尽量保持简洁，便于和 C# 结构、资源绑定、调度逻辑逐项对照。
2. 这里可以用更长篇幅讲清楚算法背景、参数语义、数据流和常见误区，而不把实现文件写得过于拥挤。

当前文档：

- `Registration.md`：当前主配准路线（overlap prior + phase correlation + diagnostics）
- `RegistrationCS.md`：旧 GPU score-map 搜索器的 fallback 说明
- `StitchCS.md`：单张图像累加到浮点画布的过程
- `StitchClearCS.md`：清空累积纹理
- `StitchFinalizeCS.md`：累积结果归一化输出
