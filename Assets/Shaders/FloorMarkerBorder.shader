Shader "Custom/FloorMarkerBorder"
{
    // Renders only the border of a quad (center is fully transparent).
    // Used as a floor indicator under tutorial-highlighted objects.
    Properties
    {
        _Color       ("Border Color",  Color)         = (0.25, 0.65, 1.0, 1.0)
        _BorderWidth ("Border Width",  Range(0, 0.5)) = 0.06
        _PulseSpeed  ("Pulse Speed",   Float)         = 2.5
        _MinAlpha    ("Min Alpha",     Range(0, 1))   = 0.25
        _MaxAlpha    ("Max Alpha",     Range(0, 1))   = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent"
        }

        Pass
        {
            Name "FloorMarkerBorder"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull   Off
            ZTest  LEqual

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _BorderWidth;
                float  _PulseSpeed;
                float  _MinAlpha;
                float  _MaxAlpha;
            CBUFFER_END

            struct Attributes
            {
                float4 posOS : POSITION;
                float2 uv    : TEXCOORD0;
            };

            struct Varyings
            {
                float4 posCS : SV_POSITION;
                float2 uv    : TEXCOORD0;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.posCS = TransformObjectToHClip(v.posOS.xyz);
                o.uv    = v.uv;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;

                // 1.0 on border pixels, 0.0 in the center
                float bx = step(uv.x,              _BorderWidth)
                         + step(1.0 - _BorderWidth, uv.x);
                float by = step(uv.y,              _BorderWidth)
                         + step(1.0 - _BorderWidth, uv.y);
                float onBorder = saturate(bx + by);

                if (onBorder < 0.5) discard;

                float pulse = lerp(_MinAlpha, _MaxAlpha,
                                   0.5 + 0.5 * sin(_Time.y * _PulseSpeed));

                return half4(_Color.rgb, pulse);
            }
            ENDHLSL
        }
    }
}
