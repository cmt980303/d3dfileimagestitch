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

    // Accumulate luma statistics with online mean/variance/covariance updates
    // to avoid catastrophic cancellation in the single-pass ZNCC formula.
    float meanFirstLuma = 0.0;
    float meanSecondLuma = 0.0;
    float firstLumaM2 = 0.0;
    float secondLumaM2 = 0.0;
    float lumaCovariance = 0.0;
    int lumaSampleCount = 0;
    int sampleStep = max(SampleStep, 1);

    [loop]
    for (int y = yStart; y < yEnd; y += sampleStep)
    {
        [loop]
        for (int x = xStart; x < xEnd; x += sampleStep)
        {
            int2 secondPixel;
            if (!ComputeSecondPixel(x, y, deltaX, deltaY, secondPixel))
                continue;

            // Shift the luma distribution closer to zero before the Welford update.
            // This keeps intermediate values smaller and further reduces precision loss.
            float firstLuma = SampleLuma(FirstImage, int2(x, y)) - 0.5f;
            float secondLuma = SampleLuma(SecondImage, secondPixel) - 0.5f;

            lumaSampleCount++;
            float sampleCountF = (float)lumaSampleCount;
            float firstDelta = firstLuma - meanFirstLuma;
            meanFirstLuma += firstDelta / sampleCountF;
            float secondDelta = secondLuma - meanSecondLuma;
            meanSecondLuma += secondDelta / sampleCountF;
            firstLumaM2 += firstDelta * (firstLuma - meanFirstLuma);
            secondLumaM2 += secondDelta * (secondLuma - meanSecondLuma);
            lumaCovariance += firstDelta * (secondLuma - meanSecondLuma);

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

    float finalScore = -1.0;

    float gradientScore = -2.0;
    if (gradientSampleCount >= max(MinSampleCount / 2, 16) &&
        sumFirstGradient > 1e-6 && sumSecondGradient > 1e-6)
    {
        gradientScore = sumDot * rsqrt(sumFirstGradient * sumSecondGradient);
    }

    float lumaScore = -2.0;
    if (lumaSampleCount >= MinSampleCount)
    {
        float numerator = lumaCovariance;
        float firstVariance = firstLumaM2;
        float secondVariance = secondLumaM2;

        if (firstVariance > MinLumaVariance && secondVariance > MinLumaVariance)
        {
            lumaScore = numerator * rsqrt(max(firstVariance * secondVariance, 1e-12));
        }
    }

    bool hasGradientScore = gradientScore > -1.5;
    bool hasLumaScore = lumaScore > -1.5;

    if (hasGradientScore && hasLumaScore)
    {
        float totalWeight = max(GradientWeight + LumaWeight, 1e-5);
        finalScore = ((gradientScore * GradientWeight) + (lumaScore * LumaWeight)) / totalWeight;
    }
    else if (hasGradientScore)
    {
        finalScore = gradientScore;
    }
    else if (hasLumaScore)
    {
        finalScore = lumaScore;
    }

    ScoreMap[int2(candidateX, candidateY)] = finalScore;
}
