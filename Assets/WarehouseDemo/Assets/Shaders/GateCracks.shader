// Cracks that spread across a roller gate while raiders hammer on it from outside.
//
// Drawn as an extra pass over the gate's own material rather than replacing it: the shutter keeps
// its authored look and this only adds the damage on top, so nothing about the gate's appearance
// has to be rebuilt to get the effect.
//
// The pattern is procedural — a Voronoi cell network, whose cell borders are what a sheet actually
// tears along — masked by a ragged front that grows outward from the point being struck. Nothing is
// authored per gate and no texture is needed, so it costs one material and works on every gate in
// the warehouse at whatever size it happens to be.
//
// Coordinates come from the mesh's own object-space bounds, not from UVs. The gate meshes are an
// imported set whose UV layout is not something this can rely on, and object-space bounds are
// always there and always mean the same thing: the panel's own width and height in metres. That
// also lets the crack cells stay square regardless of how oblong the door is.
Shader "Warehouse/GateCracks"
{
    Properties
    {
        [HDR] _CrackColor ("Crack colour",     Color) = (0.015, 0.014, 0.017, 1)
        [HDR] _EdgeColor  ("Torn edge colour", Color) = (0.62, 0.60, 0.56, 1)

        _Progress ("Damage", Range(0,1)) = 0

        _Density  ("Cells per metre",       Range(0.2, 8))     = 0.85
        _Width    ("Crack width",           Range(0.002, 0.2)) = 0.030
        _Ragged   ("Front raggedness",      Range(0, 1))       = 0.4
        _Reach    ("Spread reach, metres",  Range(0.5, 12))    = 2.2
        _Fine     ("Hairline amount",       Range(0, 1))       = 0.45
        _Rim      ("Torn edge strength",    Range(0, 1))       = 0.30
        _Bruise   ("Dented darkening",      Range(0, 0.5))     = 0.09

        // Where on the panel they are hitting it, in 0..1 across the panel and up it. Chest height
        // by default, because that is where a body swinging at a door connects.
        _Origin ("Impact origin (u,v)", Vector) = (0.5, 0.38, 0, 0)

        // The panel in object space, and which two of its three axes span the face. Written by
        // GateCracks from the mesh, because only it knows which axis is the thickness.
        _BoundsMin  ("Bounds min",  Vector) = (0,0,0,0)
        _BoundsSize ("Bounds size", Vector) = (1,1,1,0)
        _AxisU ("U axis mask", Vector) = (1,0,0,0)
        _AxisV ("V axis mask", Vector) = (0,1,0,0)
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
            Name "GateCracks"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            // Pulled towards the camera so it never fights the surface it is lying on. The gate is
            // one flat panel, so a constant bias is enough and costs nothing.
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CrackColor;
                float4 _EdgeColor;
                float  _Progress;
                float  _Density;
                float  _Width;
                float  _Ragged;
                float  _Reach;
                float  _Fine;
                float  _Rim;
                float  _Bruise;
                float4 _Origin;
                float4 _BoundsMin;
                float4 _BoundsSize;
                float4 _AxisU;
                float4 _AxisV;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.positionOS = IN.positionOS.xyz;
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            float2 hash2 (float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453);
            }

            float hash1 (float2 p)
            {
                return frac(sin(dot(p, float2(41.3, 289.1))) * 43758.5453);
            }

            // Distance between the two nearest cell centres — near zero exactly on a cell border,
            // which is the line a panel splits along.
            float VoronoiEdge (float2 x)
            {
                float2 n = floor(x);
                float2 f = frac(x);

                float f1 = 8.0;
                float f2 = 8.0;

                for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    float2 g = float2(i, j);
                    float2 o = hash2(n + g);
                    float  d = length(g + o - f);

                    if (d < f1)      { f2 = f1; f1 = d; }
                    else if (d < f2) { f2 = d; }
                }

                return f2 - f1;
            }

            float ValueNoise (float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = hash1(i);
                float b = hash1(i + float2(1, 0));
                float c = hash1(i + float2(0, 1));
                float d = hash1(i + float2(1, 1));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // A crack network at one scale, returned as (core, halo): the dark fissure itself and
            // the slightly wider band around it where the metal is bent up.
            float2 CrackLines (float2 p, float width)
            {
                float e = VoronoiEdge(p);
                float core = 1.0 - smoothstep(0.0, width, e);
                float halo = 1.0 - smoothstep(width, width * 2.0, e);
                return float2(core, halo);
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // Panel space: 0..1 across and up the face, plus the same in metres so the cells
                // come out square on a door that is wider than it is tall.
                float3 local = IN.positionOS - _BoundsMin.xyz;

                float spanU = max(dot(_BoundsSize.xyz, _AxisU.xyz), 1e-4);
                float spanV = max(dot(_BoundsSize.xyz, _AxisV.xyz), 1e-4);

                float2 uv     = float2(dot(local, _AxisU.xyz) / spanU, dot(local, _AxisV.xyz) / spanV);
                float2 metres = float2(uv.x * spanU, uv.y * spanV);

                // How far the damage has spread, and a wobble on that front so it eats outward in a
                // ragged shape rather than a clean circle.
                float2 originM = float2(_Origin.x * spanU, _Origin.y * spanV);
                float  dist    = distance(metres, originM);
                float  wobble  = (ValueNoise(metres * 0.9) - 0.5) * _Ragged * _Reach * 0.5;

                float front = _Progress * _Reach;
                float mask  = 1.0 - smoothstep(front - 0.6, front + 0.05, dist + wobble);
                mask       *= step(0.001, _Progress);

                // Wide splits from the first blow; hairlines only once it has taken a beating.
                float width = _Width * (0.4 + 0.6 * _Progress);

                float2 big  = CrackLines(metres * _Density, width);
                float2 thin = CrackLines(metres * _Density * 2.6 + 17.3, width * 0.6);

                float fineAmount = _Fine * smoothstep(0.3, 0.85, _Progress);

                float core = saturate(big.x + thin.x * fineAmount);
                float halo = saturate(big.y + thin.y * fineAmount);

                // The lip of bent metal alongside the split — everything the halo covers that the
                // fissure itself does not.
                float rim = saturate(halo - core) * _Rim;

                // The torn edge is lit by whatever ambient the gate stands in, so a bright rim does
                // not glow out of a dark warehouse the way an unlit overlay would.
                half3 ambient  = SampleSH(normalize(IN.normalWS));
                half3 tornEdge = _EdgeColor.rgb * saturate(ambient + 0.15);

                // A shallow darkening over the struck area, so the panel reads as dented and not
                // merely as a panel with lines drawn on it.
                float bruise = _Bruise * (0.45 + 0.55 * ValueNoise(metres * 2.3));

                // Composited by weight rather than lerped, so the dark fissure stays dark and the
                // bright lip stays a thin edge on it instead of washing the whole line out.
                float  aCore   = core * mask;
                float  aRim    = rim  * mask;
                float  aBruise = bruise * mask * (1.0 - core);

                float alpha = saturate(aCore + aRim + aBruise);

                half3 colour = (_CrackColor.rgb * aCore
                              + tornEdge        * aRim
                              + _CrackColor.rgb * aBruise) / max(aCore + aRim + aBruise, 1e-4);

                clip(alpha - 0.004);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
