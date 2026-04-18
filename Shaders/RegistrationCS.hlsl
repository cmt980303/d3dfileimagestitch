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
