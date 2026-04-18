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

    float distLeft   = localPos.x;
    float distRight  = imageSize.x - localPos.x;
    float distTop    = localPos.y;
    float distBottom = imageSize.y - localPos.y;

    float minDist = min(min(distLeft, distRight), min(distTop, distBottom));
    return saturate(minDist / BlendWidth);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 localPixel = int2(dispatchThreadId.xy);
    int2 imageSize = int2(ceil(ImageParam.z), ceil(ImageParam.w));

    if (localPixel.x >= imageSize.x || localPixel.y >= imageSize.y)
        return;

    int2 dstPixel = int2(ImageParam.xy) + localPixel;
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
