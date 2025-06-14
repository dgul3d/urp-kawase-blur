Shader "CustomEffects/KawaseBlur"
{
    HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // Adjust _BlurStrength for the overall intensity of the blur.
        // For Kawase blur, this often acts as the sample offset.
        float _BlurStrength; 

        // This is typically set by the C# script controlling the blur, 
        // incrementing for each pass.
        float _offset; 

        float4 KawaseBlurPass(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float3 color = 0;
            // The sample offsets are based on the _offset, which should increase
            // with each pass to sample further away pixels.
            // _BlitTexture_TexelSize.xy gives us (1/width, 1/height)
            float2 offset = _offset * _BlitTexture_TexelSize.xy;

            // Sample in a cross pattern for Kawase blur
            color += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord + float2(-offset.x, -offset.y)).rgb;
            color += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord + float2( offset.x, -offset.y)).rgb;
            color += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord + float2(-offset.x,  offset.y)).rgb;
            color += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord + float2( offset.x,  offset.y)).rgb;
            
            // Average the 4 samples
            return float4(color / 4.0, 1.0);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZWrite Off Cull Off

        // The single pass for Kawase Blur. 
        // The blur effect is achieved by rendering this pass multiple times
        // with increasing _offset values.
        Pass
        {
            Name "KawaseBlurPass"

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment KawaseBlurPass

            ENDHLSL
        }
    }
}