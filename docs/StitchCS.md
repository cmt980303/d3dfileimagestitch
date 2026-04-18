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
    float4 FeatherParam; // x=left, y=right, z=top, w=bottom
    float2 OutputSize;  // canvas width / height
    float  BlendWidth;
    float  Padding0;
};

Texture2D<float4> SrcImage : register(t0);
RWTexture2D<float4> AccumTex : register(u0); // rgb = weighted color sum, a = weight sum

SamplerState LinearSampler : register(s0);

float ComputeBlendWeight(float2 localPos, float2 imageSize)
{
    if (BlendWidth <= 0.0)
        return 1.0;

    float weight = 1.0;

    if (FeatherParam.x > 0.0)
        weight *= smoothstep(0.0, FeatherParam.x, localPos.x);

    if (FeatherParam.y > 0.0)
        weight *= smoothstep(0.0, FeatherParam.y, imageSize.x - localPos.x);

    if (FeatherParam.z > 0.0)
        weight *= smoothstep(0.0, FeatherParam.z, localPos.y);

    if (FeatherParam.w > 0.0)
        weight *= smoothstep(0.0, FeatherParam.w, imageSize.y - localPos.y);

    return max(weight, 1e-4);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 localPixel = int2(dispatchThreadId.xy);
    int2 imageSize = int2(ceil(ImageParam.z), ceil(ImageParam.w));

    if (localPixel.x >= imageSize.x || localPixel.y >= imageSize.y)
        return;

    int2 dstPixel = int2(round(ImageParam.xy)) + localPixel;
    if (dstPixel.x < 0 || dstPixel.y < 0 ||
        dstPixel.x >= (int)OutputSize.x || dstPixel.y >= (int)OutputSize.y)
    {
        return;
    }

    float2 uv = float2(
        (localPixel.x + 0.5) / max(ImageParam.z, 1.0),
        (localPixel.y + 0.5) / max(ImageParam.w, 1.0));

    float4 color = SrcImage.SampleLevel(LinearSampler, uv, 0);
    float weight = ComputeBlendWeight(float2(localPixel), ImageParam.zw);

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
- `FeatherParam`：四个方向的羽化宽度
- `OutputSize`：整张输出画布大小
- `BlendWidth`：是否启用混合的总开关

虽然 `BlendWidth` 在 `ComputeBlendWeight` 里只作为开/关判断使用，但保留它能让 CPU 侧逻辑更统一：滑块为 0 时可以直接关闭羽化。

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

1. **使用 `smoothstep` 而不是线性 `saturate`**
2. **四个方向权重用乘法组合，而不是取 `min`**

原因是之前线性斜坡 + `min` 组合时，边缘一阶导数不连续，在缩小显示时更容易出现暗纹和摩尔纹。  
`smoothstep` 会让边缘过渡更平滑，乘法组合也更适合表达“同时受到多个边的衰减”。

最后的 `max(weight, 1e-4)` 也很重要：

- 防止边缘像素权重直接变成 0
- 避免后续归一化阶段某些位置 `sumWeight` 过小甚至为 0
- 在渐进式导入时，即便邻居还没加载，单张图边缘也不至于被完全抹空

### 4. `round(ImageParam.xy)`

这是当前版本里修复缩小时条纹问题的关键之一。

如果目标偏移是小数，例如 `100.4`，那实际纹理采样和 WPF 的再次缩放会共同制造亚像素缝隙。  
这些缝隙在放大时不明显，但在缩小时很容易形成暗纹。

因此这里明确把 placement 偏移对齐到整数像素：

```hlsl
int2 dstPixel = int2(round(ImageParam.xy)) + localPixel;
```

对应地，C# 侧也会在预览布局阶段把偏移和尺寸做整数化，两边共同保证几何对齐。

### 5. `uv` 的计算

```hlsl
float2 uv = float2(
    (localPixel.x + 0.5) / max(ImageParam.z, 1.0),
    (localPixel.y + 0.5) / max(ImageParam.w, 1.0));
```

这里使用像素中心 `(x + 0.5)` 采样，而不是像素左上角。  
这样在缩放预览图时能得到更稳定的线性采样结果。

`max(..., 1.0)` 则是为了防止极端情况下宽高接近 0 时发生除零。

### 6. 主流程

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
