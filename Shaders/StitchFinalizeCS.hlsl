// ============================================================
// StitchFinalizeCS.hlsl - finalize accumulated stitch result
// ============================================================
// Converts the floating point accumulation canvas into the final
// normalized preview texture.
// ============================================================

cbuffer StitchFinalizeParams : register(b0)
{
    float2 OutputSize;
    float2 Padding0;
};

Texture2D<float4> AccumTex : register(t0);
RWTexture2D<float4> OutputTex : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    int2 pixelCoord = int2(dispatchThreadId.xy);
    if (pixelCoord.x >= (int)OutputSize.x || pixelCoord.y >= (int)OutputSize.y)
        return;

    float4 accum = AccumTex[pixelCoord];
    if (accum.a > 1e-5)
    {
        OutputTex[pixelCoord] = float4(accum.rgb / accum.a, 1.0);
    }
    else
    {
        OutputTex[pixelCoord] = float4(0.0, 0.0, 0.0, 0.0);
    }
}
