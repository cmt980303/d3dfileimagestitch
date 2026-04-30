# RegistrationCS.hlsl（legacy fallback）

## 当前定位

`RegistrationCS.hlsl` 仍然保留在项目里，但它已经**不是主配准路线**。

当前主路径见：

- `docs\Registration.md`

`RegistrationCS.hlsl` 现在的作用是：

1. 当 CPU phase correlation 因输入条件不足或估计失败时，作为 **fallback 搜索器**
2. 为后续可能的 **GPU 化 phase correlation** 提供现成的 D3D11 / compute shader 基础设施参考

## 它做什么

这个 shader 的工作方式仍然是：

1. 在搜索窗中枚举候选 `(deltaX, deltaY)`
2. 对每个候选在 GPU 上计算一张 `ScoreMap`
3. C# 回读 `ScoreMap`
4. 选取得分最高的候选位移

当前输出格式：

- `R`: final score
- `G`: gradient score
- `B`: luma score
- `A`: coverage

## 为什么它降级成 fallback

在真实显微图上，旧路线存在明显问题：

1. `peak margin` 经常很低，主峰不尖
2. `roundTrip` 和 `local drift` 经常偏大
3. 说明不是“最佳点找错了一点”，而是整张候选图就缺乏稳定唯一解

因此它不再适合作为主路径。

## 现在什么时候会用到它

只有当以下情况出现时，`GpuRegistration` 才会回退到它：

- 图像文件路径不可用，无法从磁盘读取 phase 预览
- phase correlation 无法得到有效估计
- 后续需要做对照实验

## 维护约定

如果继续修改这个 shader，需要同步检查：

- `Core\RegistrationConstants.cs`
- `Core\GpuRegistration.cs`

不要只改 HLSL 一侧。
