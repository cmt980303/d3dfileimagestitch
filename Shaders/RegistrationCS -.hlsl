// ============================================================
// RegistrationCS.hlsl - GPU 混合相关性图像配准着色器
// ============================================================

// --- 常量缓冲区 (Constant Buffer) ---
// 由 C# (Vortice) 每帧更新并传入 GPU 的参数
cbuffer RegistrationParams : register(b0)
{
    int LeftWidth; // 左图(基准图)宽度
    int LeftHeight; // 左图(基准图)高度
    int RightWidth; // 右图(当前图)宽度
    int RightHeight; // 右图(当前图)高度

    int OverlapWidth; // 预计的重叠区域宽度 (基于载物台物理坐标估算)
    int SearchRangeX; // X方向搜索半径 (例如 10，则搜索 -10 到 +10)
    int SearchRangeY; // Y方向搜索半径
    int SampleStep; // 采样步长 (例如为2时，隔一个像素采一个，用于加速)

    float MinGradientEnergy; // 最小梯度能量阈值 (过滤掉纯色、无特征的平滑区域)
    int MinSampleCount; // 最小有效采样点数 (防止重叠区太小导致计算结果不可靠)
    float MinLumaVariance; // 最小亮度方差 (过滤掉全黑或全白的无效区域)
    float GradientWeight; // 梯度得分的权重 (融合打分用)

    float LumaWeight; // 亮度得分的权重 (融合打分用)
    float Padding0; // HLSL 的 cbuffer 必须按 16 字节(4个float)对齐，这是占位符
    float Padding1;
    float Padding2;
};

// --- 输入与输出资源绑定 ---
Texture2D<float4> LeftImage : register(t0); // 纹理寄存器 0: 左侧基准图
Texture2D<float4> RightImage : register(t1); // 纹理寄存器 1: 右侧待匹配图
RWTexture2D<float> ScoreMap : register(u0); // UAV 读写纹理 0: 输出的热力图/得分矩阵

// --- 辅助函数：RGB 转灰度 (Luminance) ---
float ToLuma(float4 color)
{
    // 经典的 NTSC 加权灰度公式，符合人眼对绿光最敏感的物理规律
    return dot(color.rgb, float3(0.299, 0.587, 0.114));
}

// --- 辅助函数：采样特定坐标的灰度值 ---
float SampleLuma(Texture2D<float4> image, int2 pixel)
{
    // Load 函数直接使用整数像素坐标读取，不进行纹理过滤，速度极快
    return ToLuma(image.Load(int3(pixel, 0)));
}

// --- 辅助函数：计算像素的 2D 梯度 (边缘强度和方向) ---,其结果的平方和即为梯度能量
float2 ComputeGradient(Texture2D<float4> image, int2 pixel)
{
    // 中心差分法 (Central Difference) 计算 X 和 Y 方向的导数
    float gx = SampleLuma(image, pixel + int2(1, 0)) - SampleLuma(image, pixel + int2(-1, 0));
    float gy = SampleLuma(image, pixel + int2(0, 1)) - SampleLuma(image, pixel + int2(0, -1));
    return float2(gx, gy);
}

// --- 主计算函数 ---
// 定义线程组大小：8x8 个线程为一个 block。
// 这里的每一个【线程】，负责计算【一个特定的偏移量 (deltaX, deltaY)】的整体重叠区得分。
[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    // 1. 计算总的搜索候选位置数量
    const int candidateCountX = SearchRangeX * 2 + 1;
    const int candidateCountY = SearchRangeY * 2 + 1;

    // 2. 当前线程负责计算的 ScoreMap 坐标
    int candidateX = (int) dispatchThreadId.x;
    int candidateY = (int) dispatchThreadId.y;

    // 如果线程坐标超出了搜索范围，直接退出 (防止越界写入)
    if (candidateX >= candidateCountX || candidateY >= candidateCountY)
        return;

    // 3. 将线程坐标转换为实际的像素偏移量 (例如 candidateX=0 时, deltaX = -SearchRangeX)
    int deltaX = candidateX - SearchRangeX;
    int deltaY = candidateY - SearchRangeY;

    // 4. 计算左右两图在当前偏移量下的【有效重叠区域】的边界
    // 注意：只在有效边界内循环，避免采样到图像外部引发崩溃或错误数据
    int leftStartX = max(1, LeftWidth - OverlapWidth);
    int leftEndX = LeftWidth - 1;
    int yStart = max(1, -deltaY + 1);
    int yEnd = min(LeftHeight - 1, RightHeight - deltaY - 1);

    // 5. 初始化累加器 --- 用于梯度 NCC ---
    float sumDot = 0.0; // 梯度向量点积之和
    float sumLeft = 0.0; // 左图梯度能量(模的平方)之和
    float sumRight = 0.0; // 右图梯度能量(模的平方)之和
    int gradientSampleCount = 0; // 成功计算梯度的像素数

    // 6. 初始化累加器 --- 用于亮度 ZNCC (零均值归一化互相关) ---
    // 这里使用了极度优化的“单趟方差算法 (One-pass Variance)”，避免了先算均值再算方差的两次循环
    float sumLeftLuma = 0.0; // 左图灰度总和
    float sumRightLuma = 0.0; // 右图灰度总和
    float sumLeftLuma2 = 0.0; // 左图灰度平方和
    float sumRightLuma2 = 0.0; // 右图灰度平方和
    float sumCrossLuma = 0.0; // 左右图灰度乘积之和
    int lumaSampleCount = 0; // 参与亮度计算的像素数

    // 7. 遍历重叠区域内的所有像素 (支持按 SampleStep 跳跃采样来加速)
    [loop] // 提示编译器尽量不要展开循环，节省寄存器
    for (int y = yStart; y < yEnd; y += max(SampleStep, 1))
    {
        [loop]
        for (int x = leftStartX; x < leftEndX; x += max(SampleStep, 1))
        {
            // 将左图坐标映射到右图坐标 (加上偏移量 delta)
            int rightX = x - (LeftWidth - OverlapWidth) + deltaX;
            int rightY = y + deltaY;

            // 再次进行安全校验，防止右图越界
            if (rightX <= 0 || rightX >= RightWidth - 1)
                continue;

            // --- 阶段 A：提取并累加亮度数据 ---
            float leftLuma = SampleLuma(LeftImage, int2(x, y));
            float rightLuma = SampleLuma(RightImage, int2(rightX, rightY));

            sumLeftLuma += leftLuma;
            sumRightLuma += rightLuma;
            sumLeftLuma2 += leftLuma * leftLuma;
            sumRightLuma2 += rightLuma * rightLuma;
            sumCrossLuma += leftLuma * rightLuma;
            lumaSampleCount++;

            // --- 阶段 B：提取并累加梯度数据 ---
            float2 leftGradient = ComputeGradient(LeftImage, int2(x, y));
            float2 rightGradient = ComputeGradient(RightImage, int2(rightX, rightY));

            // 计算梯度向量的能量(长度的平方)
            float leftEnergy = dot(leftGradient, leftGradient);
            float rightEnergy = dot(rightGradient, rightGradient);

            // 核心优化：如果这片区域是平滑的(比如显微镜下的空白载玻片)，梯度能量极小，则跳过
            // 这排除了纯色噪声对梯度相关性的干扰
            if (leftEnergy < MinGradientEnergy || rightEnergy < MinGradientEnergy)
                continue;

            sumDot += dot(leftGradient, rightGradient);
            sumLeft += leftEnergy;
            sumRight += rightEnergy;
            gradientSampleCount++;
        }
    }

    // 8. 结算得分 (默认值为负数，表示无效打分)
    float score = -1.0;
    float gradientScore = -2.0;
    
    // --- 计算梯度归一化互相关得分 (Cosine Similarity) ---
    if (gradientSampleCount >= max(MinSampleCount / 2, 16) && sumLeft > 1e-6 && sumRight > 1e-6)
    {
        // rsqrt 是快速平方根倒数硬件指令。相当于 sumDot / sqrt(sumLeft * sumRight)
        gradientScore = sumDot * rsqrt(sumLeft * sumRight);
    }

    float lumaScore = -2.0;
    
    // --- 计算亮度 ZNCC 得分 (Zero-mean Normalized Cross-Correlation) ---
    if (lumaSampleCount >= MinSampleCount)
    {
        float sampleCountF = (float) lumaSampleCount;
        
        // 利用平方和与均值推导出的单趟协方差和方差公式 (极简、极速)
        float numerator = sumCrossLuma - ((sumLeftLuma * sumRightLuma) / sampleCountF);
        float leftVariance = sumLeftLuma2 - ((sumLeftLuma * sumLeftLuma) / sampleCountF);
        float rightVariance = sumRightLuma2 - ((sumRightLuma * sumRightLuma) / sampleCountF);

        // 只有当区域内有足够的亮度变化(方差大)时，打分才有意义
        if (leftVariance > MinLumaVariance && rightVariance > MinLumaVariance)
        {
            lumaScore = numerator * rsqrt(leftVariance * rightVariance);
        }
    }

    // 9. 融合得分策略 (加权平均)
    bool hasGradientScore = gradientScore > -1.5;
    bool hasLumaScore = lumaScore > -1.5;

    if (hasGradientScore && hasLumaScore)
    {
        // 两项得分都有效，进行加权融合
        float totalWeight = max(GradientWeight + LumaWeight, 1e-5);
        score = ((gradientScore * GradientWeight) + (lumaScore * LumaWeight)) / totalWeight;
    }
    else if (hasGradientScore)
    {
        score = gradientScore; // 只有梯度得分有效 (例如亮度方差太小被滤除)
    }
    else if (hasLumaScore)
    {
        score = lumaScore; // 只有亮度得分有效 (例如区域太模糊，提取不到梯度)
    }

    // 10. 将当前这个偏移量的最终得分写入输出纹理的对应像素中
    ScoreMap[int2(candidateX, candidateY)] = score;
}