# StitchFinalizeCS.hlsl 说明

## 作用概述

`StitchFinalizeCS.hlsl` 负责把 `StitchCS.hlsl` 输出的浮点累积画布转换成真正可显示的结果。

输入：

- `AccumTex.rgb`：颜色和
- `AccumTex.a`：权重和

输出：

- `OutputTex`：归一化后的最终预览图

这一步做的事可以概括为：

```text
if weight > 0:
    final = colorSum / weight
else:
    final = transparent
```

## 源码

```hlsl
// ============================================================
// StitchFinalizeCS.hlsl - finalize accumulated stitch result
// ============================================================
// Converts the floating point accumulation canvas into the final
// normalized preview texture.
// ============================================================

cbuffer StitchFinalizeParams : register(b0)
{
    float2 OutputSize;
    float2 Padding0;
};

Texture2D<float4> AccumTex : register(t0);
RWTexture2D<float4> OutputTex : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 pixelCoord = int2(dispatchThreadId.xy);
    if (pixelCoord.x >= (int)OutputSize.x || pixelCoord.y >= (int)OutputSize.y)
        return;

    float4 accum = AccumTex[pixelCoord];
    if (accum.a > 1e-5)
    {
        OutputTex[pixelCoord] = float4(accum.rgb / accum.a, 1.0);
    }
    else
    {
        OutputTex[pixelCoord] = float4(0.0, 0.0, 0.0, 0.0);
    }
}
```

## 逐段解释

### 1. 输入和输出为什么分开

这里不会直接原地改写 `AccumTex`，而是单独写入 `OutputTex`。

这样做的好处是：

1. 累积纹理仍然保留在高精度格式中，可以继续接受后续新增图片累加。
2. 输出纹理可以使用更适合显示的 BGRA8 格式，减少显存占用。
3. 预览刷新时只需要重新做一次 Finalize，而不必重建累积结果。

这正是当前“渐进式导入 + 增量累加”策略的基础。

### 2. `accum.a > 1e-5`

`accum.a` 表示当前位置累计的总权重。

只有当它足够大时，才说明这个像素确实被至少一张图贡献过：

```hlsl
if (accum.a > 1e-5)
```

这里使用一个很小的阈值，而不是直接判断 `> 0`，是为了避免浮点误差带来的不稳定行为。

### 3. 归一化为什么写成 `accum.rgb / accum.a`

因为前一阶段保存的是：

```text
sumColor += color * weight
sumWeight += weight
```

所以最终恢复颜色的唯一正确方式就是：

```text
finalColor = sumColor / sumWeight
```

这也是很多图像融合、体渲染和加权平均算法的经典写法。

### 4. 没有贡献时为什么输出透明黑

当某个像素没有任何图像覆盖时：

```hlsl
OutputTex[pixelCoord] = float4(0.0, 0.0, 0.0, 0.0);
```

把 alpha 设为 0 能明确表达“这里没有有效图像内容”。  
对当前 WPF 显示链路来说，这也比写不透明黑色更符合直觉，因为空白区域不会被误当成真实图像。

## 和渐进式预览的关系

这一着色器是“渐进式预览不卡顿”的关键组成部分之一。

当前链路不是每来一张图就重新计算整幅拼图，而是：

1. 只对新图执行一次 `StitchCS`
2. 再对整张累积画布执行一次 `StitchFinalizeCS`

第二步虽然覆盖整张画布，但它只是一次简单的逐像素归一化，代价远小于“重新累加全部图像”。

## 和 C# 侧的对应关系

它由 `GpuStitcher.FinalizeOutput` 驱动：

- `UpdateFinalizeConstants` 写入 `OutputSize`
- `_accumSrv` 绑定到 `t0`
- `_outputUav` 绑定到 `u0`
- `Dispatch` 范围覆盖整张预览画布

如果未来在 Finalize 阶段增加新功能（例如灰度显示、亮度补偿或色彩映射），通常也会先从这里扩展。
