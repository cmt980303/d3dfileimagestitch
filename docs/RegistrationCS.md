# RegistrationCS.hlsl 说明

## 作用概述

`RegistrationCS.hlsl` 是 GPU 配准链路的核心。  
它不会直接输出“哪一个位移最好”，而是先为**每一个候选位移**计算一个分数，写进 `ScoreMap`。

之后 C# 侧会把整张 `ScoreMap` 回读到 CPU，再选取得分最高的那个候选点。

这意味着它本质上在做一件事：

> 把“给定搜索窗口内每个候选偏移的匹配质量”并行评估出来。

## 源码

```hlsl
// ============================================================
// RegistrationCS.hlsl - hybrid GPU registration
// ============================================================
// This shader evaluates one candidate offset per thread and writes
// the score into ScoreMap. It supports both horizontal neighbors
// (left -> right) and vertical neighbors (top -> bottom).
// ============================================================

cbuffer RegistrationParams : register(b0)
{
    int   FirstWidth;
    int   FirstHeight;
    int   SecondWidth;
    int   SecondHeight;

    int   OverlapSize;
    int   SearchRangeX;
    int   SearchRangeY;
    int   SampleStep;

    int   Orientation;       // 0 = horizontal, 1 = vertical
    int   MinSampleCount;
    float MinGradientEnergy;
    float MinLumaVariance;

    float GradientWeight;
    float LumaWeight;
    float Padding0;
    float Padding1;
};

Texture2D<float4> FirstImage  : register(t0);
Texture2D<float4> SecondImage : register(t1);
RWTexture2D<float> ScoreMap   : register(u0);

float ToLuma(float4 color)
{
    return dot(color.rgb, float3(0.299, 0.587, 0.114));
}

float SampleLuma(Texture2D<float4> image, int2 pixel)
{
    return ToLuma(image.Load(int3(pixel, 0)));
}

float2 ComputeGradient(Texture2D<float4> image, int2 pixel)
{
    float gx = SampleLuma(image, pixel + int2(1, 0)) - SampleLuma(image, pixel + int2(-1, 0));
    float gy = SampleLuma(image, pixel + int2(0, 1)) - SampleLuma(image, pixel + int2(0, -1));
    return float2(gx, gy);
}

void ComputeOverlapBounds(
    int deltaX,
    int deltaY,
    out int xStart,
    out int xEnd,
    out int yStart,
    out int yEnd)
{
    if (Orientation == 0)
    {
        xStart = max(1, FirstWidth - OverlapSize);
        xEnd = FirstWidth - 1;
        yStart = max(1, -deltaY + 1);
        yEnd = min(FirstHeight - 1, SecondHeight - deltaY - 1);
    }
    else
    {
        xStart = max(1, -deltaX + 1);
        xEnd = min(FirstWidth - 1, SecondWidth - deltaX - 1);
        yStart = max(1, FirstHeight - OverlapSize);
        yEnd = FirstHeight - 1;
    }
}

bool ComputeSecondPixel(
    int firstX,
    int firstY,
    int deltaX,
    int deltaY,
    out int2 secondPixel)
{
    if (Orientation == 0)
    {
        secondPixel = int2(
            firstX - (FirstWidth - OverlapSize) + deltaX,
            firstY + deltaY);
    }
    else
    {
        secondPixel = int2(
            firstX + deltaX,
            firstY - (FirstHeight - OverlapSize) + deltaY);
    }

    return secondPixel.x > 0 && secondPixel.x < (SecondWidth - 1) &&
           secondPixel.y > 0 && secondPixel.y < (SecondHeight - 1);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    const int candidateCountX = SearchRangeX * 2 + 1;
    const int candidateCountY = SearchRangeY * 2 + 1;

    int candidateX = (int)dispatchThreadId.x;
    int candidateY = (int)dispatchThreadId.y;

    if (candidateX >= candidateCountX || candidateY >= candidateCountY)
        return;

    int deltaX = candidateX - SearchRangeX;
    int deltaY = candidateY - SearchRangeY;

    int xStart;
    int xEnd;
    int yStart;
    int yEnd;
    ComputeOverlapBounds(deltaX, deltaY, xStart, xEnd, yStart, yEnd);

    float sumDot = 0.0;
    float sumFirstGradient = 0.0;
    float sumSecondGradient = 0.0;
    int gradientSampleCount = 0;

    float sumFirstLuma = 0.0;
    float sumSecondLuma = 0.0;
    float sumFirstLuma2 = 0.0;
    float sumSecondLuma2 = 0.0;
    float sumCrossLuma = 0.0;
    int lumaSampleCount = 0;

    [loop]
    for (int y = yStart; y < yEnd; y += max(SampleStep, 1))
    {
        [loop]
        for (int x = xStart; x < xEnd; x += max(SampleStep, 1))
        {
            int2 secondPixel;
            if (!ComputeSecondPixel(x, y, deltaX, deltaY, secondPixel))
                continue;

            float firstLuma = SampleLuma(FirstImage, int2(x, y));
            float secondLuma = SampleLuma(SecondImage, secondPixel);

            sumFirstLuma += firstLuma;
            sumSecondLuma += secondLuma;
            sumFirstLuma2 += firstLuma * firstLuma;
            sumSecondLuma2 += secondLuma * secondLuma;
            sumCrossLuma += firstLuma * secondLuma;
            lumaSampleCount++;

            float2 firstGradient = ComputeGradient(FirstImage, int2(x, y));
            float2 secondGradient = ComputeGradient(SecondImage, secondPixel);

            float firstEnergy = dot(firstGradient, firstGradient);
            float secondEnergy = dot(secondGradient, secondGradient);

            if (firstEnergy < MinGradientEnergy || secondEnergy < MinGradientEnergy)
                continue;

            sumDot += dot(firstGradient, secondGradient);
            sumFirstGradient += firstEnergy;
            sumSecondGradient += secondEnergy;
            gradientSampleCount++;
        }
    }

    float score = -1.0;

    float gradientScore = -2.0;
    if (gradientSampleCount >= max(MinSampleCount / 2, 16) &&
        sumFirstGradient > 1e-6 && sumSecondGradient > 1e-6)
    {
        gradientScore = sumDot * rsqrt(sumFirstGradient * sumSecondGradient);
    }

    float lumaScore = -2.0;
    if (lumaSampleCount >= MinSampleCount)
    {
        float sampleCountF = (float)lumaSampleCount;
        float numerator = sumCrossLuma - ((sumFirstLuma * sumSecondLuma) / sampleCountF);
        float firstVariance = sumFirstLuma2 - ((sumFirstLuma * sumFirstLuma) / sampleCountF);
        float secondVariance = sumSecondLuma2 - ((sumSecondLuma * sumSecondLuma) / sampleCountF);

        if (firstVariance > MinLumaVariance && secondVariance > MinLumaVariance)
        {
            lumaScore = numerator * rsqrt(firstVariance * secondVariance);
        }
    }

    bool hasGradientScore = gradientScore > -1.5;
    bool hasLumaScore = lumaScore > -1.5;

    if (hasGradientScore && hasLumaScore)
    {
        float totalWeight = max(GradientWeight + LumaWeight, 1e-5);
        score = ((gradientScore * GradientWeight) + (lumaScore * LumaWeight)) / totalWeight;
    }
    else if (hasGradientScore)
    {
        score = gradientScore;
    }
    else if (hasLumaScore)
    {
        score = lumaScore;
    }

    ScoreMap[int2(candidateX, candidateY)] = score;
}
```

## 设计思路

## 1. 为什么“一个线程 = 一个候选位移”

这里不是让每个线程负责一个像素，而是让每个线程负责一个 `(deltaX, deltaY)` 候选位移。

也就是说，`ScoreMap` 的含义是：

- 横轴：X 方向候选修正量
- 纵轴：Y 方向候选修正量
- 每个像素：该候选位移的匹配分数

这样 GPU 最擅长的“并行评估大量独立候选”的能力就被直接利用起来了。

## 2. 为什么先转亮度

无论是亮度相关还是梯度相关，当前版本都建立在灰度亮度上：

```hlsl
return dot(color.rgb, float3(0.299, 0.587, 0.114));
```

原因是显微拼图里真正稳定的匹配信息往往来自结构和纹理，而不是颜色本身。  
先压成亮度可以：

1. 降低颜色噪声干扰
2. 让后续梯度计算更简单
3. 让不同染色/曝光轻微波动时更稳

## 3. `ComputeOverlapBounds`

这一步决定“在当前候选位移下，第一张图中哪些像素会落到有效重叠区域里”。

它分两种情况：

- `Orientation == 0`：左右相邻图
- `Orientation == 1`：上下相邻图

水平配准时，第一张图参与比较的是它的**右侧重叠带**；  
垂直配准时，参与比较的是它的**下侧重叠带**。

同时这里还会结合 `deltaX / deltaY` 调整有效采样边界，避免后续访问越界。

## 4. `ComputeSecondPixel`

这个函数负责把“第一张图中的一个采样点”映射到“第二张图里与之对应的候选点”。

它体现的是当前版本的几何假设：

- 水平配准：第二张图大体在第一张图右边
- 垂直配准：第二张图大体在第一张图下边

GPU 搜索并不是在全空间暴力乱找，而是在这个先验关系附近做局部修正。

## 5. 双评分：梯度 + 亮度

这是当前配准着色器最重要的思想。

### 梯度分数

梯度分数衡量的是：

> 两张图在边缘方向和纹理变化方向上是否一致

优点：

- 对整体亮度漂移更稳
- 对照明不均更稳
- 对暗角和慢变化背景不敏感

缺点：

- 如果纹理本身太弱、边缘太少，就容易不稳定

### 亮度分数

亮度分数用的是零均值相关（ZNCC 思想）：

```text
covariance / sqrt(var1 * var2)
```

优点：

- 在有足够细节且纹理不弱时很稳
- 对模糊区域有时比梯度更宽容

缺点：

- 更容易受到亮度场变化影响

### 为什么混合

显微图像经常同时出现以下问题：

- 一部分区域亮度漂移明显
- 一部分区域纹理很弱
- 个别图对比度不足

只靠梯度或只靠亮度都不够稳，所以当前版本把两者做加权混合：

```hlsl
score = (gradientScore * GradientWeight + lumaScore * LumaWeight) / totalWeight;
```

如果某一项无效，则退回使用另一项。

## 6. 为什么要有 `MinGradientEnergy` / `MinLumaVariance`

这些阈值是为了过滤“信息量不够”的样本。

例如：

- 一片几乎纯色的背景
- 极度平滑、没有纹理的区域
- 方差非常低的亮度块

这些区域虽然也能参与数学计算，但对正确配准几乎没有帮助，反而会稀释真正有信息的样本。

## 7. 为什么返回 `-1` / `-2`

这里约定：

- `-2`：该分项根本无效
- `-1`：整体得分初始化值 / 基本无参考意义

后面 C# 侧只需要挑最大值，因此用负值作为“坏候选”很方便。

## 与 C# 侧的配合

这个着色器由 `GpuRegistration.RegisterPair` 驱动：

1. C# 侧决定当前是水平配准还是垂直配准
2. C# 侧设定搜索半径、重叠宽度、采样步长和权重
3. 着色器生成整张 `ScoreMap`
4. C# 侧回读 `ScoreMap`
5. C# 侧选最大值并换算成真实相对位移

也就是说：

- HLSL 负责**并行算分**
- C# 负责**调度、回读、选优和构建全局布局**

这是一种很适合当前项目规模的分工方式。
