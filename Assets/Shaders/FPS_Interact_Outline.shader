Shader "Custom/FPS_Interact_Outline"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0.25, 0.65, 1.0, 1.0)
        _OutlineWidth ("Outline Width", Float) = 0.005
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
            Name "FPS_Interact_Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull   Front
            ZWrite Off
            ZTest  LEqual

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _OutlineColor;
                float  _OutlineWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 posOS  : POSITION;
                float3 normOS : NORMAL;
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

                // Transform normal to view space with inverse-transpose of MV
                // to correctly handle non-uniform scaling.
                float3 normVS = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normOS));

                // Project vertex to clip space, then expand in screen XY
                // by scaling the offset with posCS.w for constant pixel width.
                float4 posCS = TransformObjectToHClip(v.posOS.xyz);

                // A cube's front/back faces have a zero XY normal. The old fixed +X fallback
                // pushed every vertex of those faces in one direction, producing the bright
                // half-face seen on cartons instead of a closed silhouette. Use the projected
                // direction from the object's pivot for faces aimed at the camera; it expands
                // their four corners outwards on every side while retaining normal extrusion on
                // side faces.
                float3 radialVS = mul((float3x3)UNITY_MATRIX_MV, v.posOS.xyz);
                float2 sn    = normVS.xy;
                float  snLen = length(sn);
                float2 radial = radialVS.xy;
                float radialLen = length(radial);
                radial = (radialLen > 1e-4) ? radial / radialLen : float2(1.0, 0.0);
                sn = (snLen > 1e-4) ? sn / snLen : radial;

                posCS.xy += sn * (_OutlineWidth * posCS.w);
                o.posCS   = posCS;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return half4(_OutlineColor.rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
