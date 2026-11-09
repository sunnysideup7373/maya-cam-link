// Maya Cam Link - 物理ベースのボケ (URP RenderGraph 用)
// URP 標準 Bokeh の 14px 上限を持たない Gather 方式の被写界深度
Shader "Hidden/MayaCamLink/Bokeh"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        float4 _MCL_CoCParams; // x: CoC 係数 K, y: ピント距離(m), z: 最大半径(画面高さ比), w: 高さ/幅
        float4 _MCL_Texel;     // x: 1/フル解像度高さ, y: 1/半解像度高さ, z: 1/半解像度幅
        TEXTURE2D_X(_MCL_BokehTex);

        #define SAMPLE_COUNT 96
        #define GOLDEN_ANGLE 2.39996323

        // 符号付き CoC (画面高さ比)。+ = 後ボケ, - = 前ボケ
        float ComputeCoC(float2 uv)
        {
            float d = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            float coc = _MCL_CoCParams.x * (d - _MCL_CoCParams.y) / max(d, 1e-4);
            return clamp(coc, -_MCL_CoCParams.z, _MCL_CoCParams.z);
        }

        float4 FragPrefilter(Varyings i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = i.texcoord;
            float3 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            return float4(c, ComputeCoC(uv));
        }

        float4 FragBokeh(Varyings i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = i.texcoord;
            float4 c0 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
            float maxR = _MCL_CoCParams.z;
            float margin = _MCL_Texel.y * 2.0;

            float4 bg = float4(c0.rgb, 1.0) * 0.001;
            float4 fg = 0;
            [loop]
            for (int k = 0; k < SAMPLE_COUNT; k++)
            {
                float r = sqrt((k + 0.5) / SAMPLE_COUNT) * maxR;
                float th = k * GOLDEN_ANGLE;
                float2 disp = float2(cos(th), sin(th)) * r;
                float2 suv = uv + float2(disp.x * _MCL_CoCParams.w, disp.y);
                float4 s = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, suv);

                float bgCoC = max(min(c0.a, s.a), 0.0);
                float bw = saturate((bgCoC - r + margin) / margin);
                float fw = saturate((-s.a - r + margin) / margin) * step(_MCL_Texel.y, -s.a);
                bg += float4(s.rgb, 1.0) * bw;
                fg += float4(s.rgb, 1.0) * fw;
            }
            bg.rgb /= max(bg.a, 1e-5);
            fg.rgb /= max(fg.a, 1e-5);
            float fa = saturate(fg.a * PI / SAMPLE_COUNT);
            return float4(lerp(bg.rgb, fg.rgb, fa), fa);
        }

        float4 FragTent(Varyings i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = i.texcoord;
            float2 t = float2(_MCL_Texel.z, _MCL_Texel.y) * 0.5;
            float4 s = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x, -t.y));
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( t.x, -t.y));
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-t.x,  t.y));
            s += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2( t.x,  t.y));
            return s * 0.25;
        }

        float4 FragComposite(Varyings i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            float2 uv = i.texcoord;
            float4 c = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
            float4 dof = SAMPLE_TEXTURE2D_X(_MCL_BokehTex, sampler_LinearClamp, uv);
            float coc = ComputeCoC(uv);
            float ffa = smoothstep(_MCL_Texel.x * 2.0, _MCL_Texel.x * 4.0, coc);
            float a = saturate(ffa + dof.a - ffa * dof.a);
            return float4(lerp(c.rgb, dof.rgb, a), c.a);
        }
        ENDHLSL

        Pass { Name "Prefilter" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilter
        ENDHLSL }
        Pass { Name "Bokeh" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBokeh
        ENDHLSL }
        Pass { Name "Tent" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragTent
        ENDHLSL }
        Pass { Name "Composite" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
        ENDHLSL }
    }
}
