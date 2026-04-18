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
