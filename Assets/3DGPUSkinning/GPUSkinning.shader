Shader "Universal Render Pipeline/Optimize/GPUSkinning"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float2 uv           : TEXCOORD0;
                float2 uv1          : TEXCOORD1; // Bone Index 0 and 1
                float2 uv2          : TEXCOORD2; // Bone Index 2 (x), Weight 0 (y)
                float2 uv3          : TEXCOORD3; // Weight 1 (x), Weight 2 (y)
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
            };

            Texture2D _MainTex;
            SamplerState sampler_MainTex;

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _MainTex_ST;
            CBUFFER_END

            StructuredBuffer<float4x4> _BoneMatrices;

            Varyings vert(Attributes input)
            {
                Varyings output;

                // Decode bone indices and weights from UV channels
                int id0 = (int)round(input.uv1.x);
                int id1 = (int)round(input.uv1.y);
                int id2 = (int)round(input.uv2.x);

                float w0 = input.uv2.y;
                float w1 = input.uv3.x;
                float w2 = input.uv3.y;

                // Calculate blended skinning matrix from 3 bones
                float4x4 boneTransform = _BoneMatrices[id0] * w0
                                       + _BoneMatrices[id1] * w1
                                       + _BoneMatrices[id2] * w2;

                // Skin position (object space to skinned space)
                float4 skinnedPos = mul(boneTransform, float4(input.positionOS.xyz, 1.0));

                // Transform skinned space to clip space
                output.positionCS = TransformObjectToHClip(skinnedPos.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);

                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float4 col = _MainTex.Sample(sampler_MainTex, input.uv) * _Color;
                return col;
            }
            ENDHLSL
        }
    }
}
