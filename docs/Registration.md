# 配准链路说明

## 当前主路线

当前项目的相邻图配准，主路径已经不再是早期的 `RegistrationCS.hlsl` 全窗候选评分搜索。

现在真正承担主配准工作的，是：

1. **已知重叠比先验**
2. **CPU phase correlation 亚像素精修**
3. **forward / reverse / 分段 ROI 诊断**
4. **全局加权布局求解**

对应代码入口：

- `Core\GpuRegistration.cs`
- `Core\PhaseCorrelationPriorEstimator.cs`
- `Models\PairRegistrationDiagnostics.cs`

## 为什么切换路线

在真实显微图上，旧的“亮度 + 梯度相关 score map”路线暴露出两个关键问题：

1. **峰值不尖**
   - 候选图经常呈现大面积平峰
   - `peak margin` 很低，无法稳定判断唯一解

2. **局部不一致**
   - 不同局部块支持不同位移
   - 即使总分不低，也会出现较大的 `roundTrip` 和 `local drift`

实测表明，这类图像更适合：

> 先利用固定重叠比锁定 nominal overlap，再用 phase correlation 做小范围平移精修。

## 主流程

### 1. nominal offset

相邻图在几何上先有一个默认位移：

- 水平邻接：`offsetX = width - overlapWidth`, `offsetY = 0`
- 垂直邻接：`offsetX = 0`, `offsetY = height - overlapHeight`

这里的 `overlapWidth / overlapHeight` 来自：

- 用户 overlap 滑块
- 或固定显微镜重叠比先验（当前默认 10%）

### 2. phase correlation 精修

`PhaseCorrelationPriorEstimator` 会：

1. 从磁盘读取灰度预览
2. 按 nominal overlap 裁出重叠带
3. 对重叠带做 Hann window
4. 做 FFT -> cross-power spectrum -> IFFT
5. 找主峰并做抛物线亚像素细化

输出：

- `deltaX`
- `deltaY`
- `response`
- `secondBestResponse`
- `coverage`

### 3. reverse 验证

除了正向配准，还会做一遍 reverse phase correlation：

- 水平：右图 -> 左图
- 垂直：下图 -> 上图

然后用正反结果构造：

- `roundTripError`

如果正反闭环不成立，就说明这条边并不满足稳定的单一平移模型。

### 4. 分段漂移诊断

主重叠带不会只做一次整体 phase correlation，还会按交叉轴切成 3 段 ROI：

- 水平邻接：沿竖直方向切 3 段
- 垂直邻接：沿水平方向切 3 段

每段单独估计位移，再统计：

- `segmentSpreadX`
- `segmentSpreadY`

它们的合成量就是：

- `local drift`

这一步的意义非常直接：

> 如果同一条边不同局部需要不同位移，说明问题已经不是“峰值不尖”，而是“单一平移本身不够解释这条边”。

### 5. 几何可靠度

当前可信边不再只看单一分数，而是组合：

- `peak margin`
- `roundTrip`
- `local drift`

得到：

- `GeometryReliability`

只有同时满足：

- 相位响应足够高
- 几何可靠度足够高

这条边才会被当作可信边写入全局布局。

### 6. 全局布局

可信边和低可信边都会进入全局最小二乘，但权重不同：

- 可信边：高权重
- 几何失配边 / 回退边：弱约束

这样做的目标不是“每条边都相信”，而是：

> 让好的边主导全局布局，让坏的边只维持连续性而不拉崩整体结果。

## 当前诊断指标含义

### 相位响应

`phase`

- 当前主峰的响应值
- 越高通常说明重叠带里存在更明确的平移主峰

### 峰值优势

`peak`

- `bestResponse - secondBestResponse`
- 用来判断主峰是不是唯一尖峰

### 正反向残差

`roundTrip`

- 正向位移与反向位移闭环后的误差
- 越接近 0 越说明几何上自洽

### 局部漂移

`drift`

- 三段 ROI 支持的位移离散度
- 越大越说明这条边更像“局部失配边”

### 几何可靠度

`geomRel`

- 由 `peak + roundTrip + drift` 组合而来
- 当前“可信边”的核心依据

## 和旧 GPU score-map 路线的关系

旧的 `RegistrationCS.hlsl` 候选评分搜索没有被完全删除，但它现在只承担：

- **fallback**
- 调试对照
- 后续如果需要 GPU 化 phase correlation 时，作为已有 D3D11/Compute Shader 管线参考

也就是说：

> 当前项目的“配准算法主体”已经不再是 `RegistrationCS.hlsl`。

## 为什么真实图效果提升明显

因为 phase correlation 更符合当前问题结构：

1. 已知相邻关系
2. 已知固定重叠比
3. 目标主要是平移 + 亚像素精修

它不再要求在整个候选窗里构造一张必须足够尖锐的混合评分图，而是直接针对重叠带估计平移主峰。

这对真实显微图比“亮度 + 梯度全窗相关”更稳。

## 后续优化方向

当前最值得继续推进的是：

1. **局部漂移边的分段位移融合**
   - 不再强行使用单一 `(dx, dy)`
   - 让三段 phase 结果共同决定最终边位移

2. **phase correlation GPU 化**
   - 当前 CPU 版本已经验证有效
   - 后续再把 FFT / cross-power / 峰值检测搬到 Compute Shader

3. **更强几何模型**
   - 若某些边持续出现较大 `local drift`
   - 再考虑小旋转 / affine-lite，而不是回头继续强化旧 score map
