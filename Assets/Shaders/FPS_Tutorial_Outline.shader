Shader "Custom/FPS_Tutorial_Outline"
{
    // Full-body highlight: renders front faces with a semi-transparent fill colour.
    // Replaces the previous outline-only (Cull Front + vertex expansion) approach.
    // ZTest LEqual — only the visible surface is tinted; hidden parts are not shown.
    // Blend — the fill overlays the already-rendered object material.
    Properties
    {
        _OutlineColor ("Fill Color", Color) = (0.25, 0.65, 1.0, 0.45)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent+1"
        }

        Pass
        {
            Name "FPS_Tutorial_Fill"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull   Back
            ZWrite Off
            ZTest  LEqual
            Blend  SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
            CBUFFER_END

            struct Attributes
            {
                float4 posOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 posCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.posCS = TransformObjectToHClip(v.posOS.xyz);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return half4(_OutlineColor.rgb, _OutlineColor.a);
            }
            ENDHLSL
        }
    }
}
