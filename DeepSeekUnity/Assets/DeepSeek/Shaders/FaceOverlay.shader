Shader "Hidden/DeepSeek/FaceOverlay"
{
    Properties
    {
        _OverlayMap ("Base Texture", 2D) = "white" {}
        _OverlayTint ("Base Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
        TEXTURE2D(_OverlayMap);
        SAMPLER(sampler_OverlayMap);
        TEXTURE2D_X(_DeepSeekHairMask);
        CBUFFER_START(UnityPerMaterial)
            half4 _OverlayTint;
        CBUFFER_END

        struct MeshAttributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct MeshVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        MeshVaryings Vertex(MeshAttributes input)
        {
            MeshVaryings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv;
            return output;
        }

        float EyeDepth(float rawDepth)
        {
            return unity_OrthoParams.w > 0.5 ? LinearDepthToEyeDepth(rawDepth) : LinearEyeDepth(rawDepth, _ZBufferParams);
        }
        ENDHLSL

        Pass
        {
            Name "HairMask"
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment HairMask

            half4 HairMask(MeshVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float sceneDepth = EyeDepth(SampleSceneDepth(screenUV));
                float hairDepth = EyeDepth(input.positionCS.z);
                // A hair pixel behind the nearest opaque surface cannot act as a mask.
                clip(max(0.0001, hairDepth * 0.00005) - abs(sceneDepth - hairDepth));
                return 1;
            }
            ENDHLSL
        }

        Pass
        {
            Name "FaceDepth"
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment FaceDepth

            half4 FaceDepth(MeshVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "CaptureFeatures"
            ZTest LEqual
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment CaptureFeatures

            half4 CaptureFeatures(MeshVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                half mask = SAMPLE_TEXTURE2D_X(_DeepSeekHairMask, sampler_PointClamp, screenUV).r;
                clip(mask - 0.001h);
                float sceneDepth = EyeDepth(SampleSceneDepth(screenUV));
                float featureDepth = EyeDepth(input.positionCS.z);
                clip(featureDepth - sceneDepth - max(0.0001, featureDepth * 0.00005));
                half3 color = SAMPLE_TEXTURE2D(_OverlayMap, sampler_OverlayMap, input.uv).rgb * _OverlayTint.rgb;
                return half4(color, mask);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BlendFeatures"
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment BlendFeatures
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half4 BlendFeatures(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 feature = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord);
                return half4(feature.rgb, 0.5h * feature.a);
            }
            ENDHLSL
        }
    }
}

