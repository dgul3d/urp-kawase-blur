using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// Original script by Sebastian Hein: https://github.com/sebastianhein/urp_kawase_blur
// This version is rewritten for URP's Render Graph API, using a single UnsafePass for maximum efficiency.
public class KawaseBlurRenderGraphUnsafe : ScriptableRendererFeature
{
    [System.Serializable]
    public class KawaseBlurSettings
    {
        [Tooltip("The event where the blur effect is injected into the render pipeline.")]
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

        [Tooltip("The Kawase blur shader.")]
        public Shader blurShader = null;


        [Tooltip("The number of blur iterations. Higher values increase the blur effect but cost more performance.")]
        [Range(2, 15)]
        public int blurPasses = 5;

        [Tooltip("Downsampling factor for the blur textures. Higher values increase performance at the cost of quality.")]
        [Range(1, 4)]
        public int downsample = 1;

        private Material m_BlurMaterial;
        public Material blurMaterial
        {
            get
            {
                if (m_BlurMaterial == null && blurShader != null)
                {
                    m_BlurMaterial = new Material(blurShader);
                    m_BlurMaterial.hideFlags = HideFlags.HideAndDontSave;
                }
                return m_BlurMaterial;
            }
        }
        public void Cleanup()
        {
            if (m_BlurMaterial != null)
            {
                Object.DestroyImmediate(m_BlurMaterial);
                m_BlurMaterial = null;
            }
        }
    }

    public KawaseBlurSettings settings = new KawaseBlurSettings();
    private KawaseBlurUnsafePass m_ScriptablePass;

    public override void Create()
    {
        m_ScriptablePass = new KawaseBlurUnsafePass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.blurMaterial == null)
        {
            Debug.LogWarning("Missing Kawase Blur material. The pass will not be executed.");
            return;
        }
        m_ScriptablePass.Setup(settings);
        renderer.EnqueuePass(m_ScriptablePass);
    }

    private class KawaseBlurUnsafePass : ScriptableRenderPass
    {
        private KawaseBlurSettings m_Settings;
        private const string m_ProfilerTag = "Kawase Blur (UnsafePass)";

        // This class stores the data needed by the pass execution lambda.
        private class PassData
        {
            internal Material blurMaterial;
            internal int passes;
            internal TextureHandle source;
            internal TextureHandle temp1;
            internal TextureHandle temp2;
            internal TextureHandle finalDestination;
        }

        public KawaseBlurUnsafePass()
        {
            this.requiresIntermediateTexture = true;
        }

        public void Setup(KawaseBlurSettings settings)
        {
            m_Settings = settings;
            this.renderPassEvent = settings.renderPassEvent;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();

            // Define blur texture descriptor
            var cameraTargetDesc = resourceData.activeColorTexture.GetDescriptor(renderGraph);
            var blurTextureDesc = cameraTargetDesc;
            blurTextureDesc.width /= m_Settings.downsample;
            blurTextureDesc.height /= m_Settings.downsample;
            blurTextureDesc.msaaSamples = MSAASamples.None;
            blurTextureDesc.depthBufferBits = 0;

            // Create textures used in the pass
            TextureHandle tempTex1 = renderGraph.CreateTexture(blurTextureDesc);
            TextureHandle tempTex2 = renderGraph.CreateTexture(blurTextureDesc);
            TextureHandle finalDestination;

            finalDestination = resourceData.activeColorTexture;

            // Add a single UnsafePass to contain the entire blur logic
            using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData))
            {
                // Populate the data for the execution function
                passData.blurMaterial = m_Settings.blurMaterial;
                passData.passes = m_Settings.blurPasses;
                passData.source = resourceData.activeColorTexture;
                passData.temp1 = tempTex1;
                passData.temp2 = tempTex2;
                passData.finalDestination = finalDestination;

                // Declare all resource usages for the Render Graph to manage dependencies
                builder.UseTexture(passData.source); // Read from the camera source
                builder.UseTexture(passData.temp1, AccessFlags.Write); // Write and read from temp textures
                builder.UseTexture(passData.temp2, AccessFlags.Write);
                builder.UseTexture(passData.finalDestination, AccessFlags.Write); // Write to the final destination

                // Assign the execution function
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }

        // The static execution function that performs the blur using direct command buffer calls
        private static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            // Get the native command buffer to issue low-level commands
            CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            var scaleBias = new Vector4(1, 1, 0, 0);

            // --- Pass 1: Source -> Temp1 ---
            context.cmd.SetRenderTarget(data.temp1);
            cmd.SetGlobalFloat("_offset", 1.5f);
            Blitter.BlitTexture(cmd, data.source, scaleBias, data.blurMaterial, 0);

            // Setup for ping-pong loop
            TextureHandle readHandle = data.temp1;
            TextureHandle writeHandle = data.temp2;

            // --- Intermediate Passes (Ping-Pong from i=1 to passes-2) ---
            for (var i = 1; i < data.passes - 1; i++)
            {
                context.cmd.SetRenderTarget(writeHandle);
                cmd.SetGlobalFloat("_offset", 0.5f + i);
                Blitter.BlitTexture(cmd, readHandle, scaleBias, data.blurMaterial, 0);

                // Swap handles for the next iteration
                (readHandle, writeHandle) = (writeHandle, readHandle);
            }

            // --- Final Pass: Last Temp -> Final Destination ---
            // After the loop, the result is in readHandle
            context.cmd.SetRenderTarget(data.finalDestination);
            cmd.SetGlobalFloat("_offset", 0.5f + data.passes - 1f);
            Blitter.BlitTexture(cmd, readHandle, scaleBias, data.blurMaterial, 0);
        }


    }
    
}