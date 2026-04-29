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

    float2 localCenter = float2(localPixel) + 0.5;
    float4 color = SrcImage.SampleLevel(LinearSampler, uv, 0);
    float weight = ComputeBlendWeight(localCenter, ImageParam.zw);

    float4 accum = AccumTex[dstPixel];
    accum.rgb += color.rgb * weight;
    accum.a += weight;
    AccumTex[dstPixel] = accum;
}
