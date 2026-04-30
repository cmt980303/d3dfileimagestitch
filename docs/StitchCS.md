# StitchCS.hlsl 说明

## 作用概述

`StitchCS.hlsl` 是拼图链路中最核心的着色器。  
它一次只处理**一张**输入图像，并把该图像按照给定 `ImagePlacement` 放到一张浮点累积画布中。

这一步并不直接生成最终显示图，而是生成中间结果：

- `AccumTex.rgb`：累加后的颜色和
- `AccumTex.a`：累加后的权重和

后续 `StitchFinalizeCS.hlsl` 会再对它做一次 `rgb / a` 归一化，得到真正的拼图结果。

这种两阶段设计有几个直接好处：

1. 不再受“一个着色器同时绑定多少张输入纹理”的槽位限制。
2. 支持渐进式导入：新图片到来时只需要再累加那一张。
3. 支持后续做大图分块输出，因为每个 tile 都可以重复这套流程。

## 源码

```hlsl
// ============================================================
// StitchCS.hlsl - per-image accumulation shader
// ============================================================
// The old version bound many source textures in one pass, which
// hard-limited the stitch count. The new version processes one
// image per dispatch and accumulates color/weight into a floating
// point canvas, so the number of input images is no longer tied
// to shader resource slots.
// ============================================================

cbuffer StitchImageParams : register(b0)
{
    float4 ImageParam;  // x=offsetX, y=offsetY, z=displayWidth, w=displayHeight
    float4 FeatherParam; // x=leftOverlap, y=rightOverlap, z=topOverlap, w=bottomOverlap
    float2 OutputSize;  // canvas width / height
    float  BlendWidth;
    float  Padding0;
};

Texture2D<float4> SrcImage : register(t0);
RWTexture2D<float4> AccumTex : register(u0); // rgb = weighted color sum, a = weight sum

SamplerState LinearSampler : register(s0);

float ComputeEdgeWeight(float distanceFromEdge, float overlapSize)
{
    if (overlapSize <= 0.0)
        return 1.0;

    float transitionWidth = min(max(BlendWidth, 0.0), overlapSize);
    if (transitionWidth <= 1e-4)
        return distanceFromEdge >= overlapSize * 0.5 ? 1.0 : 0.0;

    float seamCenter = overlapSize * 0.5;
    float halfTransition = transitionWidth * 0.5;
    float transitionStart = max(0.0, seamCenter - halfTransition);
    float transitionEnd = min(overlapSize, seamCenter + halfTransition);

    if (transitionEnd <= transitionStart + 1e-4)
        return distanceFromEdge >= seamCenter ? 1.0 : 0.0;

    return smoothstep(transitionStart, transitionEnd, distanceFromEdge);
}

float ComputeBlendWeight(float2 localPos, float2 imageSize)
{
    if (BlendWidth <= 0.0)
        return 1.0;

    float weight = 1.0;

    if (FeatherParam.x > 0.0)
        weight *= ComputeEdgeWeight(localPos.x, FeatherParam.x);

    if (FeatherParam.y > 0.0)
        weight *= ComputeEdgeWeight(imageSize.x - localPos.x, FeatherParam.y);

    if (FeatherParam.z > 0.0)
        weight *= ComputeEdgeWeight(localPos.y, FeatherParam.z);

    if (FeatherParam.w > 0.0)
        weight *= ComputeEdgeWeight(imageSize.y - localPos.y, FeatherParam.w);

    return max(weight, 1e-4);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 localPixel = int2(dispatchThreadId.xy);
    float2 bboxOrigin = floor(ImageParam.xy);
    int2 bboxSize = int2(ceil(ImageParam.xy + ImageParam.zw) - bboxOrigin);

    if (localPixel.x >= bboxSize.x || localPixel.y >= bboxSize.y)
        return;

    int2 dstPixel = int2(bboxOrigin) + localPixel;
    if (dstPixel.x < 0 || dstPixel.y < 0 ||
        dstPixel.x >= (int)OutputSize.x || dstPixel.y >= (int)OutputSize.y)
    {
        return;
    }

    float2 localCenter = (float2(dstPixel) + 0.5) - ImageParam.xy;
    if (localCenter.x < 0.0 || localCenter.y < 0.0 ||
        localCenter.x >= ImageParam.z || localCenter.y >= ImageParam.w)
    {
        return;
    }

    float2 uv = float2(
        localCenter.x / max(ImageParam.z, 1.0),
        localCenter.y / max(ImageParam.w, 1.0));
    float4 color = SrcImage.SampleLevel(LinearSampler, uv, 0);
    float weight = ComputeBlendWeight(localCenter, ImageParam.zw);

    float4 accum = AccumTex[dstPixel];
    accum.rgb += color.rgb * weight;
    accum.a += weight;
    AccumTex[dstPixel] = accum;
}
```

## 逐段解释

### 1. `StitchImageParams`

这个常量缓冲区由 C# 侧的 `StitchImageConstants` 填充，字段顺序必须一一对应。

- `ImageParam.xy`：当前图左上角在目标画布中的偏移
- `ImageParam.zw`：当前图在目标画布中的显示尺寸
- `FeatherParam`：四个方向与相邻图的实际重叠范围
- `OutputSize`：整张输出画布大小
- `BlendWidth`：缝中部过渡带的目标宽度

CPU 侧会先计算每个边缘与邻图的真实重叠范围，再把它写进 `FeatherParam`。  
`BlendWidth` 不再表示“整条边都要羽化多宽”，而是表示“真正做过渡的带子多宽”；如果它大于实际重叠范围，着色器会自动把过渡压缩回整个重叠区。

### 2. `AccumTex`

`AccumTex` 不是最终显示结果，而是“颜色和 + 权重和”的中间缓存。

为什么不直接把颜色写到输出纹理？

因为多张图片可能在同一个像素位置发生重叠。  
如果直接覆盖写，后写入的图会把前面的图完全顶掉；  
如果只做平均，又必须知道参与平均的总权重。

所以这里采用经典的加权累积表达：

```text
sumColor += color * weight
sumWeight += weight
```

最后再统一计算：

```text
finalColor = sumColor / sumWeight
```

### 3. `ComputeBlendWeight`

这段函数决定“当前像素在当前图中占多少权重”。

当前版本有两个关键点：

1. **先根据真实重叠范围求出缝中心，再只在中心附近做过渡**
2. **四个方向权重仍然用乘法组合，而不是取 `min`**

这样做是为了避免旧版本里最明显的问题：  
如果两张图真实重叠 120 像素，而羽化宽度只设成 40 像素，那么重叠中间会出现一大段“左右两张图权重都等于 1”的区域，归一化后就变成整段平均，轻微错位时会直接形成模糊带。

新版本会先用 `FeatherParam` 表示真实重叠范围，再把 `BlendWidth` 当作缝中部的过渡带宽度：  

- 重叠区靠本图一侧：权重保持接近 1
- 重叠区靠邻图一侧：权重降到接近 0
- 只有靠近缝中心的窄带才做 `smoothstep` 过渡

这样既保留了羽化拼缝的柔和感，也避免整段重叠区长期处于双图平均状态。

最后的 `max(weight, 1e-4)` 也很重要：

- 防止边缘像素权重直接变成 0
- 避免后续归一化阶段某些位置 `sumWeight` 过小甚至为 0
- 在渐进式导入时，即便邻居还没加载，单张图边缘也不至于被完全抹空

### 4. 浮点 placement 的包围盒 dispatch

当前版本为了支持亚像素配准结果，不再直接把 `ImageParam.xy` 四舍五入后落图。  
而是先求出这张图在目标画布上的包围盒：

```hlsl
float2 bboxOrigin = floor(ImageParam.xy);
int2 bboxSize = int2(ceil(ImageParam.xy + ImageParam.zw) - bboxOrigin);
```

这样只要 `OffsetX / OffsetY` 带有小数，shader 仍然会覆盖到它真正影响到的目标像素范围。

### 5. `localCenter`

```hlsl
float2 localCenter = (float2(dstPixel) + 0.5) - ImageParam.xy;
```

这里不是再用 `localPixel + 0.5`，而是把“目标像素中心”反推回当前图内部的浮点位置。  
这正是把亚像素位移真正保留下来的关键：  
配准器算出来的小数偏移，会直接体现在源图采样坐标里，而不是在渲染阶段被吃掉。

### 6. `uv` 的计算

```hlsl
float2 uv = float2(
    localCenter.x / max(ImageParam.z, 1.0),
    localCenter.y / max(ImageParam.w, 1.0));
```

这里使用像素中心 `(x + 0.5)` 采样，而不是像素左上角。  
这样在缩放预览图时能得到更稳定的线性采样结果。

`max(..., 1.0)` 则是为了防止极端情况下宽高接近 0 时发生除零。

### 7. 主流程

主函数的实际顺序可以概括成：

1. 计算本线程负责的局部像素坐标
2. 判断是否越过当前图的目标尺寸
3. 把局部像素映射到大画布坐标
4. 判断是否越界到画布之外
5. 从源图采样颜色
6. 计算该像素的羽化权重
7. 把颜色和权重写回累积纹理

因为每次 Dispatch 只覆盖一张图的目标区域，所以时间复杂度更接近“所有图像目标面积之和”，而不是“图像数量 × 整张大画布面积”。

## 和 C# 侧的配合关系

这个着色器主要由 `Core\GpuStitcher.cs` 驱动：

- `UpdateImageConstants`：写入 `StitchImageParams`
- `AccumulateSingleImage`：绑定 `SrcImage` 和 `AccumTex`
- `Dispatch`：按当前图的显示宽高启动线程组

如果你要修改这里的常量字段，必须同步修改：

1. HLSL 的 `cbuffer`
2. C# 的 `StitchImageConstants`
3. C# 的 `UpdateImageConstants`

三者只要有一处顺序不一致，就会出现非常隐蔽的错位问题。
