using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
#if MCL_SRP
using UnityEngine.Rendering;
#endif
#if MCL_HDRP
using HD = UnityEngine.Rendering.HighDefinition;
#elif MCL_URP
using URP = UnityEngine.Rendering.Universal;
#endif

namespace MayaCamLink
{
    /// <summary>
    /// Maya カメラの Unity 側の受け皿。
    ///  - フィールドは「Maya の数値」(焦点距離 mm / フィルム mm / F値 / ピント距離 m) をそのまま保持。
    ///    Timeline の AnimationClip はこのフィールドをキーするので、カーブで Maya と同じ値を確認できる。
    ///  - 毎フレーム CinemachineCamera (Physical Lens) と被写界深度へ反映する。
    /// 階層:  MayaCam_xxx (Animator + 本コンポーネント + Volume)
    ///          └ CM_xxx  (CinemachineCamera / Transform をキー)
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)] // CinemachineBrain より先
    [AddComponentMenu("Maya Cam Link/Maya Camera Rig")]
    public class MayaCameraRig : MonoBehaviour
    {
        public enum DofRenderer
        {
            [InspectorName("URP 標準")] Urp = 0,
            [InspectorName("Maya Bokeh (物理・上限なし)")] MayaBokeh = 1
        }

        [Tooltip("Maya のカメラ名 (照合用)")]
        public string mayaCameraName;
        public CinemachineCamera cmCamera;

        [Header("レンズ")]
        [Tooltip("焦点距離 mm")] public float focalLength = 35f;
        [Tooltip("センサーサイズ mm")] public Vector2 sensorSize = new Vector2(36f, 24f);
        [Tooltip("レンズシフト")] public Vector2 lensShift;
        public Camera.GateFitMode gateFit = Camera.GateFitMode.Horizontal;
        [Tooltip("m")] public float nearClip = 0.1f;
        [Tooltip("m")] public float farClip = 1000f;

        [Header("被写界深度")]
        public bool dofEnabled;
        [Tooltip("ピント距離 m")] public float focusDistance = 5f;
        [Tooltip("F値")] public float fStop = 5.6f;
        [Tooltip("ボケ強度 (物理値に対する倍率)")] public float bokehBoost = 1f;
        [Tooltip("最大ボケ半径 (画面高さに対する %)")] public float maxBlur = 6f;
        [Tooltip("シャッター角度")] public float shutterAngle = 144f;
        [Range(3, 11)] public int bladeCount = 5;

        [Header("設定")]
        public DofRenderer dofRenderer = DofRenderer.Urp;
        public float frameRate = 24f;
        public bool driveDepthOfField = true;
        [Tooltip("このカメラが Live の時だけ被写界深度を有効にする")]
        public bool dofOnlyWhenLive = true;
        public float volumePriority = 100f;

#if MCL_SRP
        public Volume volume;
        VolumeProfile _runtimeProfile;
#endif

        [System.NonSerialized] MayaCamState _live;
        public bool HasLiveOverride => _live != null;

        public float VerticalFov =>
            Camera.FocalLengthToFieldOfView(Mathf.Max(0.01f, focalLength), Mathf.Max(0.01f, sensorSize.y));

        /// <summary>独自ボケ (Renderer Feature) を使うか</summary>
        public bool UsesMayaBokeh
        {
            get
            {
#if MCL_URP17 && !MCL_HDRP
                return driveDepthOfField && dofEnabled && dofRenderer == DofRenderer.MayaBokeh;
#else
                return false;
#endif
            }
        }

        // ---- インスタンス管理 ----
        static readonly List<MayaCameraRig> s_Rigs = new List<MayaCameraRig>();
        public static IReadOnlyList<MayaCameraRig> All => s_Rigs;

        /// <summary>カメラの CinemachineBrain が現在使っている Rig</summary>
        public static MayaCameraRig FindActiveForCamera(Camera cam)
        {
            if (cam && cam.TryGetComponent(out CinemachineBrain brain))
            {
                var active = brain.ActiveVirtualCamera as CinemachineCamera;
                if (active)
                    foreach (var r in s_Rigs)
                        if (r.cmCamera == active) return r;
            }
            foreach (var r in s_Rigs)
                if (r.cmCamera && CinemachineCore.IsLive(r.cmCamera)) return r;
            return null;
        }

        // ------------------------------------------------------------------
        void OnEnable()
        {
            if (!s_Rigs.Contains(this)) s_Rigs.Add(this);
#if MCL_SRP
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
#endif
            Apply(true);
        }

        void OnDisable()
        {
            s_Rigs.Remove(this);
#if MCL_SRP
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            if (volume) volume.weight = 0f;
#endif
        }

        void OnValidate() => Apply(false);
        void Update() => Refresh();
        void LateUpdate() => Refresh();
        void OnDidApplyAnimationProperties() => Refresh();

#if MCL_SRP
        void OnBeginContextRendering(ScriptableRenderContext ctx, List<Camera> cams) => Refresh();
#endif

        void Refresh()
        {
            if (_live != null) WriteState(_live);
            Apply(true);
        }

        // ------------------------------------------------------------------
        public void SetLiveState(MayaCamState s)
        {
            _live = s;
            WriteState(s);
            Apply(true);
        }

        public void ClearLiveOverride() => _live = null;

        public void WriteState(MayaCamState s)
        {
            if (s == null) return;
            if (cmCamera)
            {
                var t = cmCamera.transform;
                t.SetPositionAndRotation(transform.TransformPoint(s.Position), transform.rotation * s.Rotation);
            }
            focalLength = s.focal;
            sensorSize = new Vector2(s.sensorW, s.sensorH);
            lensShift = new Vector2(s.shiftX, s.shiftY);
            nearClip = s.near;
            farClip = s.far;
            dofEnabled = s.dof != 0;
            focusDistance = s.focus;
            fStop = s.fstop;
            bokehBoost = s.boost;
            maxBlur = s.maxBlur;
            shutterAngle = s.shutter;
            gateFit = (Camera.GateFitMode)s.gateFit;
        }

        public void Apply(bool allowCreate = true)
        {
            if (cmCamera)
            {
                var lens = cmCamera.Lens;
                lens.ModeOverride = LensSettings.OverrideModes.Physical;
                lens.FieldOfView = Mathf.Clamp(VerticalFov, 0.01f, 179f);
                lens.NearClipPlane = Mathf.Max(0.0001f, nearClip);
                lens.FarClipPlane = Mathf.Max(lens.NearClipPlane + 0.001f, farClip);
                var p = lens.PhysicalProperties;
                p.SensorSize = new Vector2(Mathf.Max(0.1f, sensorSize.x), Mathf.Max(0.1f, sensorSize.y));
                p.LensShift = lensShift;
                p.GateFit = gateFit;
                p.FocusDistance = Mathf.Max(0.01f, focusDistance);
                p.Aperture = Mathf.Clamp(fStop, 0.7f, 32f);
                p.ShutterSpeed = Mathf.Clamp(shutterAngle / 360f / Mathf.Max(1f, frameRate), 1e-5f, 1f);
                p.BladeCount = bladeCount;
                lens.PhysicalProperties = p;
                cmCamera.Lens = lens;
            }
            ApplyDof(allowCreate);
        }

        // ------------------------------------------------------------------
        void ApplyDof(bool allowCreate)
        {
#if MCL_SRP
            if (!driveDepthOfField) return;
            if (!volume)
            {
                volume = GetComponent<Volume>();
                if (!volume)
                {
                    if (!allowCreate) return;
                    volume = gameObject.AddComponent<Volume>();
                }
            }
            if (!volume.sharedProfile)
            {
                if (!allowCreate) return;
                if (_runtimeProfile == null)
                {
                    _runtimeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                    _runtimeProfile.name = "MayaCamLink DOF (runtime)";
                    _runtimeProfile.hideFlags = HideFlags.DontSave;
                }
                volume.sharedProfile = _runtimeProfile;
            }
            volume.isGlobal = true;
            volume.priority = volumePriority;
            bool live = !dofOnlyWhenLive || (cmCamera && CinemachineCore.IsLive(cmCamera));
            volume.weight = live ? 1f : 0f;

            var prof = volume.sharedProfile;
#if MCL_HDRP
            if (!prof.TryGet(out HD.DepthOfField dof))
            {
                if (!allowCreate) return;
                dof = prof.Add<HD.DepthOfField>(true);
            }
            dof.active = true;
            dof.focusMode.Override(dofEnabled ? HD.DepthOfFieldMode.UsePhysicalCamera : HD.DepthOfFieldMode.Off);
            dof.focusDistanceMode.Override(HD.FocusDistanceMode.Volume);
            dof.focusDistance.Override(Mathf.Max(0.1f, focusDistance));
#elif MCL_URP
            if (!prof.TryGet(out URP.DepthOfField dof))
            {
                if (!allowCreate) return;
                dof = prof.Add<URP.DepthOfField>(true);
            }
            dof.active = true;
            // Maya Bokeh 使用時は URP 標準 DOF を Off (二重掛け防止)
            bool urpOn = dofEnabled && !UsesMayaBokeh;
            dof.mode.Override(urpOn ? URP.DepthOfFieldMode.Bokeh : URP.DepthOfFieldMode.Off);
            dof.focusDistance.Override(Mathf.Max(0.1f, focusDistance));
            dof.focalLength.Override(Mathf.Clamp(focalLength, 1f, 300f));
            // ボケ強度 = 実効 F値を小さくする (URP の範囲 1–32 に収まる)
            dof.aperture.Override(Mathf.Clamp(fStop / Mathf.Max(0.01f, bokehBoost), 1f, 32f));
            dof.bladeCount.Override(Mathf.Clamp(bladeCount, 3, 9));
#endif
#endif
        }
    }
}
