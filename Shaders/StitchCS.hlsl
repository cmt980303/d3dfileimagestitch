// ============================================================
// StitchCS.hlsl - GPU Stitch Compute Shader
// ============================================================
// Blends multiple source images onto a single output canvas.
// Overlap regions use linear distance-based alpha blending.
// ============================================================

// Constant buffer: per-image placement (offset + size), up to 16 images
cbuffer StitchParams : register(b0)
{
    float4 ImageParams[16]; // .x=offsetX, .y=offsetY, .z=width, .w=height
    float2 OutputSize;      // canvas width/height
    int    ImageCount;       // number of images (1~16)
    float  BlendWidth;       // blend transition width in pixels
};

// Source textures (up to 8)
Texture2D<float4> SrcImage0 : register(t0);
Texture2D<float4> SrcImage1 : register(t1);
Texture2D<float4> SrcImage2 : register(t2);
Texture2D<float4> SrcImage3 : register(t3);
Texture2D<float4> SrcImage4 : register(t4);
Texture2D<float4> SrcImage5 : register(t5);
Texture2D<float4> SrcImage6 : register(t6);
Texture2D<float4> SrcImage7 : register(t7);

// Output UAV
RWTexture2D<float4> OutputTex : register(u0);

// Linear clamp sampler
SamplerState LinearSampler : register(s0);

// Sample from indexed source texture (no dynamic texture array indexing in SM5.0)
float4 SampleImage(int idx, float2 uv)
{
    switch (idx)
    {
        case 0: return SrcImage0.SampleLevel(LinearSampler, uv, 0);
        case 1: return SrcImage1.SampleLevel(LinearSampler, uv, 0);
        case 2: return SrcImage2.SampleLevel(LinearSampler, uv, 0);
        case 3: return SrcImage3.SampleLevel(LinearSampler, uv, 0);
        case 4: return SrcImage4.SampleLevel(LinearSampler, uv, 0);
        case 5: return SrcImage5.SampleLevel(LinearSampler, uv, 0);
        case 6: return SrcImage6.SampleLevel(LinearSampler, uv, 0);
        case 7: return SrcImage7.SampleLevel(LinearSampler, uv, 0);
        default: return float4(0, 0, 0, 0);
    }
}

// Compute blend weight: linear ramp from 0 to 1 over BlendWidth pixels from each edge
float ComputeBlendWeight(float2 localPos, float2 imgSize)
{
    if (BlendWidth <= 0)
        return 1.0;

    float distLeft   = localPos.x;
    float distRight  = imgSize.x - localPos.x;
    float distTop    = localPos.y;
    float distBottom = imgSize.y - localPos.y;

    float minDist = min(min(distLeft, distRight), min(distTop, distBottom));
    return saturate(minDist / BlendWidth);
}

// Main entry: each thread processes one output pixel
[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 pixelCoord = int2(dispatchThreadId.xy);

    if (pixelCoord.x >= (int)OutputSize.x || pixelCoord.y >= (int)OutputSize.y)
        return;

    float4 colorAccum = float4(0, 0, 0, 0);
    float  weightSum  = 0.0;

    for (int i = 0; i < ImageCount && i < 8; i++)
    {
        float offsetX = ImageParams[i].x;
        float offsetY = ImageParams[i].y;
        float imgW    = ImageParams[i].z;
        float imgH    = ImageParams[i].w;

        float2 localPos = float2(pixelCoord.x - offsetX, pixelCoord.y - offsetY);

        if (localPos.x >= 0 && localPos.x < imgW &&
            localPos.y >= 0 && localPos.y < imgH)
        {
            float2 uv = float2(localPos.x / imgW, localPos.y / imgH);
            float4 color = SampleImage(i, uv);
            float weight = ComputeBlendWeight(localPos, float2(imgW, imgH));

            colorAccum += color * weight;
            weightSum  += weight;
        }
    }

    float4 finalColor;
    if (weightSum > 0.0001)
        finalColor = colorAccum / weightSum;
    else
        finalColor = float4(0, 0, 0, 0);

    OutputTex[pixelCoord] = finalColor;
}
