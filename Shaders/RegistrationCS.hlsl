// ============================================================
// RegistrationCS.hlsl - GPU hybrid correlation registration
// ============================================================
// For stop-and-shoot stitching, stage motion already provides a
// coarse overlap estimate. This shader only searches a local
// window and scores each candidate shift by a hybrid of:
// 1. zero-mean luminance correlation
// 2. normalized gradient correlation
// Gradient correlation is robust to illumination drift; luminance
// correlation keeps working when images are heavily blurred.
// ============================================================

cbuffer RegistrationParams : register(b0)
{
    int   LeftWidth;
    int   LeftHeight;
    int   RightWidth;
    int   RightHeight;

    int   OverlapWidth;
    int   SearchRangeX;
    int   SearchRangeY;
    int   SampleStep;

    float MinGradientEnergy;
    int   MinSampleCount;
    float MinLumaVariance;
    float GradientWeight;

    float LumaWeight;
    float Padding0;
    float Padding1;
    float Padding2;
};

Texture2D<float4> LeftImage  : register(t0);
Texture2D<float4> RightImage : register(t1);

RWTexture2D<float> ScoreMap : register(u0);

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
    float gx =
        SampleLuma(image, pixel + int2(1, 0)) -
        SampleLuma(image, pixel + int2(-1, 0));

    float gy =
        SampleLuma(image, pixel + int2(0, 1)) -
        SampleLuma(image, pixel + int2(0, -1));

    return float2(gx, gy);
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

    int leftStartX = max(1, LeftWidth - OverlapWidth);
    int leftEndX = LeftWidth - 1;
    int yStart = max(1, -deltaY + 1);
    int yEnd = min(LeftHeight - 1, RightHeight - deltaY - 1);

    float sumDot = 0.0;
    float sumLeft = 0.0;
    float sumRight = 0.0;
    int gradientSampleCount = 0;

    float sumLeftLuma = 0.0;
    float sumRightLuma = 0.0;
    float sumLeftLuma2 = 0.0;
    float sumRightLuma2 = 0.0;
    float sumCrossLuma = 0.0;
    int lumaSampleCount = 0;

    [loop]
    for (int y = yStart; y < yEnd; y += max(SampleStep, 1))
    {
        [loop]
        for (int x = leftStartX; x < leftEndX; x += max(SampleStep, 1))
        {
            int rightX = x - (LeftWidth - OverlapWidth) + deltaX;
            int rightY = y + deltaY;

            if (rightX <= 0 || rightX >= RightWidth - 1)
                continue;

            float leftLuma = SampleLuma(LeftImage, int2(x, y));
            float rightLuma = SampleLuma(RightImage, int2(rightX, rightY));

            sumLeftLuma += leftLuma;
            sumRightLuma += rightLuma;
            sumLeftLuma2 += leftLuma * leftLuma;
            sumRightLuma2 += rightLuma * rightLuma;
            sumCrossLuma += leftLuma * rightLuma;
            lumaSampleCount++;

            float2 leftGradient = ComputeGradient(LeftImage, int2(x, y));
            float2 rightGradient = ComputeGradient(RightImage, int2(rightX, rightY));

            float leftEnergy = dot(leftGradient, leftGradient);
            float rightEnergy = dot(rightGradient, rightGradient);

            if (leftEnergy < MinGradientEnergy || rightEnergy < MinGradientEnergy)
                continue;

            sumDot += dot(leftGradient, rightGradient);
            sumLeft += leftEnergy;
            sumRight += rightEnergy;
            gradientSampleCount++;
        }
    }

    float score = -1.0;
    float gradientScore = -2.0;
    if (gradientSampleCount >= max(MinSampleCount / 2, 16) && sumLeft > 1e-6 && sumRight > 1e-6)
    {
        gradientScore = sumDot * rsqrt(sumLeft * sumRight);
    }

    float lumaScore = -2.0;
    if (lumaSampleCount >= MinSampleCount)
    {
        float sampleCountF = (float)lumaSampleCount;
        float numerator = sumCrossLuma - ((sumLeftLuma * sumRightLuma) / sampleCountF);
        float leftVariance = sumLeftLuma2 - ((sumLeftLuma * sumLeftLuma) / sampleCountF);
        float rightVariance = sumRightLuma2 - ((sumRightLuma * sumRightLuma) / sampleCountF);

        if (leftVariance > MinLumaVariance && rightVariance > MinLumaVariance)
        {
            lumaScore = numerator * rsqrt(leftVariance * rightVariance);
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
