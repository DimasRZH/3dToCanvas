Shader "UI/DepthClear"
{
    Properties
    {
        // Require a MainTex property to satisfy CanvasRenderer/MaskableGraphic defaults, though we don't use it.
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags 
        { 
            "Queue"="Transparent" 
            "IgnoreProjector"="True" 
            "RenderType"="Transparent" 
            "PreviewType"="Plane"
        }

        Cull Off
        Lighting Off
        ZWrite On
        ZTest Always
        ColorMask 0 // Do not write to the color buffer, only depth

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                
                o.vertex = UnityObjectToClipPos(v.vertex);

                // Force the depth to the far clip plane
                #if defined(UNITY_REVERSED_Z)
                    // In Reversed-Z (DX11, Metal, Vulkan), far plane is 0.
                    // We use a tiny epsilon to avoid potential clipping precision issues.
                    o.vertex.z = 1.0e-5; 
                #else
                    // In Standard Z (OpenGL), far plane is w.
                    o.vertex.z = o.vertex.w;
                #endif
                
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(0,0,0,0);
            }
            ENDCG
        }
    }
}
