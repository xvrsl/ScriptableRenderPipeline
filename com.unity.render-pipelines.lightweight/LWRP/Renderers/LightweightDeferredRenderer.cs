using System.Collections.Generic;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.LightweightPipeline
{
    public class LightweightDeferredRenderer : ScriptableRenderer
    {
        const string k_GBufferProfilerTag = "Render GBuffer";

        Material m_BlitMaterial;
        Material m_DeferredLightingMaterial;
        Mesh m_PointLightProxyMesh;
        Mesh m_SpotLightProxyMesh;

        MaterialPropertyBlock m_DeferredLightingProperties = new MaterialPropertyBlock();

        RenderPassAttachment m_GBufferAlbedo;
        RenderPassAttachment m_GBufferSpecRough;
        RenderPassAttachment m_GBufferNormal;
        RenderPassAttachment m_LightAccumulation;
        RenderPassAttachment m_DepthAttachment;
        
        public LightweightDeferredRenderer(LightweightPipelineAsset asset)
        {
            m_GBufferAlbedo = new RenderPassAttachment(RenderTextureFormat.ARGB32);
            m_GBufferSpecRough = new RenderPassAttachment(RenderTextureFormat.ARGB32);
            m_GBufferNormal = new RenderPassAttachment(RenderTextureFormat.ARGB2101010);
            m_LightAccumulation = new RenderPassAttachment(asset.supportsHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
            m_DepthAttachment = new RenderPassAttachment(RenderTextureFormat.Depth);

            m_LightAccumulation.Clear(Color.black, 1.0f, 0);
            m_DepthAttachment.Clear(Color.black, 1.0f, 0);

            m_BlitMaterial = CoreUtils.CreateEngineMaterial(asset.blitTransientShader);
            m_DeferredLightingMaterial = CoreUtils.CreateEngineMaterial(asset.deferredLightingShader);

            m_PointLightProxyMesh = asset.pointLightProxyMesh;
            m_SpotLightProxyMesh = asset.spotLightPointMesh;
        }

        public override void Dispose()
        {

        }

        public override void Setup(ref ScriptableRenderContext context, ref CullResults cullResults,
            ref RenderingData renderingData)
        {

        }

        public override void Execute(ref ScriptableRenderContext context, ref CullResults cullResults,
            ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            float renderScale = renderingData.cameraData.renderScale;
            int cameraPixelWidth = (int) (camera.pixelWidth * renderScale);
            int cameraPixelHeight = (int) (camera.pixelHeight * renderScale);

            m_LightAccumulation.BindSurface(BuiltinRenderTextureType.CameraTarget, false, true);

            context.SetupCameraProperties(renderingData.cameraData.camera, renderingData.cameraData.isStereoEnabled);

            using (RenderPass rp = new RenderPass(context, cameraPixelWidth, cameraPixelHeight, 1, 
                new[] { m_GBufferAlbedo, m_GBufferSpecRough, m_GBufferNormal, m_LightAccumulation }, m_DepthAttachment))
            {
                using (new RenderPass.SubPass(rp, new[] { m_GBufferAlbedo, m_GBufferSpecRough, m_GBufferNormal, m_LightAccumulation }, null))
                {
                    GBufferPass(ref context, ref cullResults, camera);
                }

                using (new RenderPass.SubPass(rp, new[] { m_LightAccumulation }, new[] { m_GBufferAlbedo, m_GBufferSpecRough, m_GBufferNormal, m_DepthAttachment }, true))
                {
                    LightingPass(ref context, ref cullResults, ref renderingData.lightData);
                }

                using (new RenderPass.SubPass(rp, new[] { m_LightAccumulation }, null))
                {
                    context.DrawSkybox(camera);
                }

                //using (new RenderPass.SubPass(rp, new[] { m_LightAccumulation }, null))
                //{
                //   TransparentPass();
                //}
            }

#if UNITY_EDITOR
            if (renderingData.cameraData.isSceneViewCamera)
            {
                // Restore Render target for additional editor rendering.
                // Note: Scene view camera always perform depth prepass
                CommandBuffer cmd = CommandBufferPool.Get("Copy Depth to Camera");
                CoreUtils.SetRenderTarget(cmd, BuiltinRenderTextureType.CameraTarget);
                //cmd.Blit(GetSurface(RenderTargetHandles.DepthTexture), BuiltinRenderTextureType.CameraTarget, GetMaterial(MaterialHandles.DepthCopy));
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#endif
        }

        public void GBufferPass(ref ScriptableRenderContext context, ref CullResults cullResults, Camera camera)
        {
            CommandBuffer cmd = CommandBufferPool.Get(k_GBufferProfilerTag);
            using (new ProfilingSample(cmd, k_GBufferProfilerTag))
            {
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                var drawSettings = new DrawRendererSettings(camera, new ShaderPassName("LightweightDeferred"))
                {
                    sorting = {flags = SortFlags.CommonOpaque},
                    rendererConfiguration = RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightmaps,
                    flags = DrawRendererFlags.EnableInstancing,
                };

                var filterSettings = new FilterRenderersSettings(true)
                {
                    renderQueueRange = RenderQueueRange.opaque,
                };

                context.DrawRenderers(cullResults.visibleRenderers, ref drawSettings, filterSettings);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            //using (var cmd = new CommandBuffer { name = "Create G-Buffer" })
            //{

            //    cmd.EnableShaderKeyword("UNITY_HDR_ON");
            //    cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));
            //    loop.ExecuteCommandBuffer(cmd);

            //    // render opaque objects using Deferred pass
            //    var drawSettings = new DrawRendererSettings(camera, new ShaderPassName("LightweightDeferred"))
            //    {
            //        sorting = { flags = SortFlags.CommonOpaque },
            //        rendererConfiguration = RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe
            //    };
            //    var filterSettings = new FilterRenderersSettings(true) { renderQueueRange = RenderQueueRange.opaque };
            //    loop.DrawRenderers(cullResults.visibleRenderers, ref drawSettings, filterSettings);

            //}
        }

        public void LightingPass(ref ScriptableRenderContext context, ref CullResults cullResults, ref LightData lightData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Deferred Lighting");
            List<VisibleLight> visibleLights = lightData.visibleLights;

            m_DeferredLightingProperties.Clear();
            Vector4 lightPosition;
            Vector4 lightColor;
            Vector4 lightAttenuation;
            Vector4 lightSpotDirection;
            Vector4 lightSpotAttenuation;
            InitializeLightConstants(visibleLights, lightData.mainLightIndex, MixedLightingSetup.None, out lightPosition, out lightColor, out lightAttenuation, out lightSpotDirection, out lightSpotAttenuation);

            m_DeferredLightingProperties.SetVector(PerCameraBuffer._MainLightPosition, new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, lightAttenuation.w));
            m_DeferredLightingProperties.SetVector(PerCameraBuffer._MainLightColor, lightColor);
            LightweightPipeline.DrawFullScreen(cmd, m_DeferredLightingMaterial, m_DeferredLightingProperties);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public void TransparentPass()
        {
            //using (var cmd = new CommandBuffer { name = "Forwward Lighting Setup" })
            //{

            //    SetupLightShaderVariables(cullResults, camera, loop, cmd);
            //    loop.ExecuteCommandBuffer(cmd);

            //    var settings = new DrawRendererSettings(camera, new ShaderPassName("ForwardSinglePass"))
            //    {
            //        sorting = { flags = SortFlags.CommonTransparent },
            //        rendererConfiguration = RendererConfiguration.PerObjectLightmaps | RendererConfiguration.PerObjectLightProbe,
            //    };
            //    var filterSettings = new FilterRenderersSettings(true) { renderQueueRange = RenderQueueRange.transparent };
            //    loop.DrawRenderers(cullResults.visibleRenderers, ref settings, filterSettings);
            //}
        }

        public void FinalPass(ref ScriptableRenderContext context, ref CullResults cullResults, ref CameraData cameraData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Final Blit Pass");
            LightweightPipeline.DrawFullScreen(cmd, m_BlitMaterial);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
