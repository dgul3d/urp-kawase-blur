using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

// Original script by Sebastian Hein: https://github.com/sebastianhein/urp_kawase_blur
// This version has been rewritten to be compatible with URP's Render Graph API.
public class KawaseBlurRenderGraph : ScriptableRendererFeature
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

        [Tooltip("If true, the final blurred result is copied back to the camera's color buffer. If false, the camera's color buffer is swapped with the blurred texture for subsequent passes, which is more efficient.")]
        public bool copyToFramebuffer;
        
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

    private KawaseBlurPass m_ScriptablePass;

    public override void Create()
    {
        m_ScriptablePass = new KawaseBlurPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.blurMaterial == null)
        {
            Debug.LogWarning("Missing Kawase Blur material. The pass will not be executed.");
            return;
        }

        // Pass settings to the render pass before enqueuing it.
        m_ScriptablePass.Setup(settings);
        renderer.EnqueuePass(m_ScriptablePass);
    }
    
    // The main render pass for the Kawase blur effect.
    private class KawaseBlurPass : ScriptableRenderPass
    {
        private KawaseBlurSettings m_Settings;
        private const string m_ProfilerTag = "Kawase Blur Pass";

        public KawaseBlurPass()
        {
            // The pass reads the camera color texture. To ensure this is not the final backbuffer,
            // we must request an intermediate texture.
            this.requiresIntermediateTexture = true;
        }
        
        // Receives the settings from the ScriptableRendererFeature.
        public void Setup(KawaseBlurSettings settings)
        {
            m_Settings = settings;
            this.renderPassEvent = settings.renderPassEvent;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // Get the Universal Render Pipeline's resource data container.
            var resourceData = frameData.Get<UniversalResourceData>();

            // Define the descriptor for our temporary blur textures.
            var cameraTargetDesc = resourceData.activeColorTexture.GetDescriptor(renderGraph);
            var blurTextureDesc = cameraTargetDesc;
            blurTextureDesc.width /= m_Settings.downsample;
            blurTextureDesc.height /= m_Settings.downsample;
            blurTextureDesc.msaaSamples = MSAASamples.None; // MSAA is not needed for blit operations.
            blurTextureDesc.depthBufferBits = 0; // Depth is not required.
            
            // Create two temporary textures for the ping-pong blur technique.
            TextureHandle tempTex1 = renderGraph.CreateTexture(blurTextureDesc);
            TextureHandle tempTex2 = renderGraph.CreateTexture(blurTextureDesc);

            TextureHandle currentSource = resourceData.activeColorTexture;

            // --- Blur Passes ---
            
            // First pass: Blit from the main camera source to our downsampled temporary texture.
            m_Settings.blurMaterial.SetFloat("_offset", 1.5f);
            var blitParams = new RenderGraphUtils.BlitMaterialParameters(currentSource, tempTex1, m_Settings.blurMaterial, 0);
            renderGraph.AddBlitPass(blitParams, $"{m_ProfilerTag}: Pass 0");
            currentSource = tempTex1;

            // Intermediate passes: Ping-pong between the two temporary textures.
            for (var i = 1; i < m_Settings.blurPasses - 1; i++)
            {
                TextureHandle destination = (i % 2 == 1) ? tempTex2 : tempTex1;
                m_Settings.blurMaterial.SetFloat("_offset", 0.5f + i);
                blitParams = new RenderGraphUtils.BlitMaterialParameters(currentSource, destination, m_Settings.blurMaterial, 0);
                renderGraph.AddBlitPass(blitParams, $"{m_ProfilerTag}: Pass {i}");
                currentSource = destination;
            }
            
            // Final pass and output.
            m_Settings.blurMaterial.SetFloat("_offset", 0.5f + m_Settings.blurPasses - 1f);

            if (m_Settings.copyToFramebuffer)
            {
                // Blit the final result back to the camera's active color texture.
                blitParams = new RenderGraphUtils.BlitMaterialParameters(currentSource, resourceData.activeColorTexture, m_Settings.blurMaterial, 0);
                renderGraph.AddBlitPass(blitParams, $"{m_ProfilerTag}: Final to Camera Color");
            }
            else
            {
                // This is the more efficient Render Graph approach. Instead of a costly copy back to the
                // camera target, we create one more texture for the final result and then swap the
                // pipeline's reference to `cameraColor` to point to our blurred texture.
                // Subsequent passes will automatically use the blurred result.
                blurTextureDesc.width = cameraTargetDesc.width;
                blurTextureDesc.height = cameraTargetDesc.height;
                TextureHandle finalBlurTexture = renderGraph.CreateTexture(blurTextureDesc);

                blitParams = new RenderGraphUtils.BlitMaterialParameters(currentSource, finalBlurTexture, m_Settings.blurMaterial, 0);
                renderGraph.AddBlitPass(blitParams, $"{m_ProfilerTag}: Final to Swapped Texture");
                
                // Swap the pipeline's main color texture with our blurred one.
                resourceData.cameraColor = finalBlurTexture;
            }
        }
    }
}