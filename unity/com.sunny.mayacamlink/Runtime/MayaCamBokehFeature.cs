#if MCL_URP17
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MayaCamLink
{
    /// <summary>
    /// 物理ベースのボケ (URP Renderer Feature)。
    /// URP 標準 Bokeh の半径上限 (14px) が無いので、大口径レンズの強いボケを再現できる。
    /// Maya Cam Link がアクティブな URP Renderer に自動で追加する。
    /// </summary>
    [DisallowMultipleRendererFeature("Maya Cam Bokeh")]
    public class MayaCamBokehFeature : ScriptableRendererFeature
    {
        public Shader shader;

        Material _mat;
        BokehPass _pass;

        public override void Create()
        {
            if (!shader) shader = Shader.Find("Hidden/MayaCamLink/Bokeh");
            if (!shader) return;
            _mat = CoreUtils.CreateEngineMaterial(shader);
            _pass = new BokehPass(_mat) { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || _mat == null) return;
            var camData = renderingData.cameraData;
            if (camData.cameraType != CameraType.Game) return;
            var rig = MayaCameraRig.FindActiveForCamera(camData.camera);
            if (rig == null || !rig.UsesMayaBokeh) return;
            _pass.Setup(rig, camData.camera);
            _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_mat);
            _mat = null;
        }

        // ------------------------------------------------------------------
        class BokehPass : ScriptableRenderPass
        {
            static readonly int CoCParams = Shader.PropertyToID("_MCL_CoCParams");
            static readonly int Texel = Shader.PropertyToID("_MCL_Texel");
            static readonly int BokehTex = Shader.PropertyToID("_MCL_BokehTex");

            readonly Material _mat;
            MayaCameraRig _rig;
            Camera _cam;

            class PassData
            {
                public TextureHandle src;
                public TextureHandle aux;
                public Material mat;
                public int pass;
            }

            public BokehPass(Material mat)
            {
                _mat = mat;
                profilingSampler = new ProfilingSampler("Maya Cam Bokeh");
            }

            public void Setup(MayaCameraRig rig, Camera cam)
            {
                _rig = rig;
                _cam = cam;
            }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer || _rig == null) return;

                TextureHandle src = res.activeColorTexture;
                var desc = rg.GetTextureDesc(src);
                int fw = Mathf.Max(1, desc.width), fh = Mathf.Max(1, desc.height);
                int hw = Mathf.Max(1, fw / 2), hh = Mathf.Max(1, fh / 2);

                // ---- 物理 CoC 係数 (画面高さ比) ----
                float f = Mathf.Max(0.001f, _rig.focalLength / 1000f);
                float N = Mathf.Max(0.1f, _rig.fStop);
                float P = Mathf.Max(f + 0.001f, _rig.focusDistance);
                float A = f / N;                                                     // 有効口径 (m)
                float visH = 2f * f * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad); // 実際に写る撮像面の高さ (m)
                float k = A * f / (P - f) / Mathf.Max(1e-6f, visH) * Mathf.Max(0f, _rig.bokehBoost);
                float maxR = Mathf.Clamp(_rig.maxBlur / 100f, 0.002f, 0.25f);
                _mat.SetVector(CoCParams, new Vector4(k, P, maxR, (float)fh / fw));
                _mat.SetVector(Texel, new Vector4(1f / fh, 1f / hh, 1f / hw, 0f));

                var hdesc = new TextureDesc(hw, hh)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    clearBuffer = false,
                    name = "MCL_Prefilter"
                };
                var pre = rg.CreateTexture(hdesc);
                hdesc.name = "MCL_Bokeh";
                var bok = rg.CreateTexture(hdesc);
                hdesc.name = "MCL_Tent";
                var tent = rg.CreateTexture(hdesc);

                var odesc = desc;
                odesc.name = "MCL_DOF_Out";
                odesc.clearBuffer = false;
                odesc.msaaSamples = MSAASamples.None;
                var dst = rg.CreateTexture(odesc);

                var depth = res.cameraDepthTexture;
                Blit(rg, "MCL Prefilter", src, pre, 0, depth, TextureHandle.nullHandle);
                Blit(rg, "MCL Bokeh", pre, bok, 1, TextureHandle.nullHandle, TextureHandle.nullHandle);
                Blit(rg, "MCL Tent", bok, tent, 2, TextureHandle.nullHandle, TextureHandle.nullHandle);
                Blit(rg, "MCL Composite", src, dst, 3, depth, tent);
                res.cameraColor = dst;
            }

            void Blit(RenderGraph rg, string name, TextureHandle src, TextureHandle dst, int pass,
                TextureHandle depth, TextureHandle aux)
            {
                using (var b = rg.AddRasterRenderPass<PassData>(name, out var d, profilingSampler))
                {
                    d.src = src;
                    d.aux = aux;
                    d.mat = _mat;
                    d.pass = pass;
                    b.UseTexture(src);
                    if (depth.IsValid()) b.UseTexture(depth);
                    if (aux.IsValid()) b.UseTexture(aux);
                    b.SetRenderAttachment(dst, 0);
                    b.SetRenderFunc((PassData p, RasterGraphContext ctx) =>
                    {
                        if (p.aux.IsValid()) p.mat.SetTexture(BokehTex, (RTHandle)p.aux);
                        Blitter.BlitTexture(ctx.cmd, p.src, new Vector4(1, 1, 0, 0), p.mat, p.pass);
                    });
                }
            }
        }
    }
}
#endif
