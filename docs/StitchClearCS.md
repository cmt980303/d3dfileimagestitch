# StitchClearCS.hlsl 说明

## 作用概述

`StitchClearCS.hlsl` 的职责非常单一：  
把浮点累积纹理 `AccumTex` 清零。

之所以单独做一个 Clear 着色器，而不是在 CPU 侧遍历写零，有两个原因：

1. 累积纹理通常比较大，直接在 GPU 上清空更自然。
2. 清空动作和后续 `Accumulate -> Finalize` 共用同一套 GPU 资源和调度链路，逻辑更统一。

## 源码

```hlsl
// ============================================================
// StitchClearCS.hlsl - clear accumulation canvas
// ============================================================

cbuffer StitchFinalizeParams : register(b0)
{
    float2 OutputSize;
    float2 Padding0;
};

RWTexture2D<float4> AccumTex : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 pixelCoord = int2(dispatchThreadId.xy);
    if (pixelCoord.x >= (int)OutputSize.x || pixelCoord.y >= (int)OutputSize.y)
        return;

    AccumTex[pixelCoord] = float4(0.0, 0.0, 0.0, 0.0);
}
```

## 逐段解释

### 1. 为什么复用 `StitchFinalizeParams`

这里使用的常量缓冲区名字与 `StitchFinalizeCS.hlsl` 一致，并且布局也一致：

```hlsl
cbuffer StitchFinalizeParams : register(b0)
```

原因很简单：  
清空和归一化都只需要知道输出画布尺寸，因此 C# 侧可以共用同一个常量缓冲区结构和更新逻辑。

这能减少：

- 一份重复的常量结构
- 一段重复的 CPU 更新代码
- 一类“两个 cbuffer 字段顺序逐渐漂移”的维护风险

### 2. 为什么写 `float4(0, 0, 0, 0)`

累积纹理每个像素里保存的是：

- `rgb`：颜色和
- `a`：权重和

因此清空时必须四个分量都归零。  
如果只清 `rgb` 不清 `a`，后续归一化会直接出错。

### 3. 为什么还要做边界判断

线程组是按整块尺寸启动的，例如 8×8。  
如果画布宽高不是 8 的整数倍，那么最边缘那一组线程里会有一部分线程落在有效区域外。

所以必须通过：

```hlsl
if (pixelCoord.x >= (int)OutputSize.x || pixelCoord.y >= (int)OutputSize.y)
    return;
```

把越界线程提前裁掉。

## 和整体拼图链路的关系

典型调用顺序如下：

1. `GpuStitcher.PrepareCanvas`  
2. `StitchClearCS.hlsl` 清空累积纹理  
3. `StitchCS.hlsl` 逐张图累加  
4. `StitchFinalizeCS.hlsl` 做归一化输出

在渐进式导入模式中，这个 Clear 只发生在：

- 新画布创建时
- 画布尺寸变化时
- 用户重新开始一轮新拼图时

它不会在每一帧都重复执行，这也是当前版本不卡顿的重要原因之一。
