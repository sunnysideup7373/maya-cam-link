using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace MayaCamLink.Editor
{
    /// <summary>
    /// Maya のベイクデータを AnimationClip に書き出し、Timeline の AnimationTrack に配置する。
    /// 再送信時は同じ .anim を上書き (GUID は変わらないので Timeline の参照も維持)。
    /// </summary>
    public static class MayaCamClipBuilder
    {
        const float ConstEps = 1e-5f;

        public static AnimationClip Build(MayaCamBakePacket p, MayaCameraRig rig, PlayableDirector director)
        {
            var frames = p.frames;
            int n = frames.Length;
            float fps = Mathf.Max(1f, p.fps);

            // ---- clip asset ----
            string folder = MayaCamLinkProcessor.EnsureFolder(MayaCamLinkSettings.OutputFolder + "/Clips");
            string safe = MayaCamLinkProcessor.Sanitize(p.camera);
            string path = $"{folder}/{MayaCamLinkProcessor.SceneName()}_{safe}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            bool isNew = clip == null;
            if (isNew) clip = new AnimationClip { name = safe };
            else clip.ClearCurves();
            clip.frameRate = fps;

            var times = new float[n];
            for (int i = 0; i < n; i++) times[i] = (frames[i].frame - p.start) / fps;

            var bindings = new List<EditorCurveBinding>();
            var curves = new List<AnimationCurve>();

            void Add(string relPath, Type type, string prop, Func<int, float> get, bool stepped = false)
            {
                var v = new float[n];
                for (int i = 0; i < n; i++) v[i] = get(i);
                bindings.Add(EditorCurveBinding.FloatCurve(relPath, type, prop));
                curves.Add(MakeCurve(times, v, stepped));
            }

            // ---- Transform (CM カメラの子オブジェクト) ----
            string camPath = rig.cmCamera
                ? AnimationUtility.CalculateTransformPath(rig.cmCamera.transform, rig.transform)
                : "";
            var euler = UnwrappedEuler(frames);
            Add(camPath, typeof(Transform), "m_LocalPosition.x", i => frames[i].px);
            Add(camPath, typeof(Transform), "m_LocalPosition.y", i => frames[i].py);
            Add(camPath, typeof(Transform), "m_LocalPosition.z", i => frames[i].pz);
            Add(camPath, typeof(Transform), "localEulerAnglesRaw.x", i => euler[i].x);
            Add(camPath, typeof(Transform), "localEulerAnglesRaw.y", i => euler[i].y);
            Add(camPath, typeof(Transform), "localEulerAnglesRaw.z", i => euler[i].z);

            // ---- Maya の数値 (MayaCameraRig のフィールド) ----
            var rt = typeof(MayaCameraRig);
            Add("", rt, nameof(MayaCameraRig.focalLength), i => frames[i].focal);
            Add("", rt, nameof(MayaCameraRig.sensorSize) + ".x", i => frames[i].sensorW);
            Add("", rt, nameof(MayaCameraRig.sensorSize) + ".y", i => frames[i].sensorH);
            Add("", rt, nameof(MayaCameraRig.lensShift) + ".x", i => frames[i].shiftX);
            Add("", rt, nameof(MayaCameraRig.lensShift) + ".y", i => frames[i].shiftY);
            Add("", rt, nameof(MayaCameraRig.nearClip), i => frames[i].near);
            Add("", rt, nameof(MayaCameraRig.farClip), i => frames[i].far);
            Add("", rt, nameof(MayaCameraRig.dofEnabled), i => frames[i].dof != 0 ? 1f : 0f, stepped: true);
            Add("", rt, nameof(MayaCameraRig.focusDistance), i => frames[i].focus);
            Add("", rt, nameof(MayaCameraRig.fStop), i => frames[i].fstop);
            Add("", rt, nameof(MayaCameraRig.shutterAngle), i => frames[i].shutter);
            Add("", rt, nameof(MayaCameraRig.bokehBoost), i => frames[i].boost);
            Add("", rt, nameof(MayaCameraRig.maxBlur), i => frames[i].maxBlur);

            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());

            if (isNew) AssetDatabase.CreateAsset(clip, path);
            else EditorUtility.SetDirty(clip);

            // アニメーションしない設定
            Undo.RecordObject(rig, "Maya Cam Link Bake");
            rig.gateFit = (Camera.GateFitMode)frames[0].gateFit;
            rig.frameRate = fps;
            EditorUtility.SetDirty(rig);

            // ---- Timeline ----
            if (director && director.playableAsset is TimelineAsset timeline)
            {
                timeline.editorSettings.frameRate = fps;
                string trackName = "Maya: " + p.camera;
                var track = timeline.GetOutputTracks().OfType<AnimationTrack>()
                    .FirstOrDefault(t => t.name == trackName);
                if (track == null) track = timeline.CreateTrack<AnimationTrack>(null, trackName);
                track.trackOffset = TrackOffset.ApplySceneOffsets;

                var tc = track.GetClips().FirstOrDefault();
                if (tc == null) tc = track.CreateClip(clip);
                else if (tc.asset is AnimationPlayableAsset apa) apa.clip = clip;

                tc.displayName = p.camera;
                tc.start = 0;
                tc.clipIn = 0;
                tc.duration = Math.Max(clip.length, 1.0 / fps);

                var animator = rig.GetComponent<Animator>();
                if (!animator) animator = Undo.AddComponent<Animator>(rig.gameObject);
                director.SetGenericBinding(track, animator);

                EditorUtility.SetDirty(timeline);
                EditorUtility.SetDirty(director);
                AssetDatabase.SaveAssets();
                director.RebuildGraph();
                TimelineEditor.Refresh(RefreshReason.ContentsModified);
            }
            else
            {
                AssetDatabase.SaveAssets();
            }
            return clip;
        }

        // ------------------------------------------------------------------
        static AnimationCurve MakeCurve(float[] t, float[] v, bool stepped)
        {
            int n = t.Length;
            bool constant = true;
            for (int i = 1; i < n && constant; i++)
                if (Mathf.Abs(v[i] - v[0]) > ConstEps) constant = false;

            if (constant)
            {
                // 一定値のチャンネルは最初と最後の 2 キーだけにして Timeline を見やすくする
                var c = new AnimationCurve(new Keyframe(t[0], v[0], 0, 0));
                if (n > 1) c.AddKey(new Keyframe(t[n - 1], v[0], 0, 0));
                return c;
            }

            var keys = new Keyframe[n];
            for (int i = 0; i < n; i++)
            {
                float tan;
                if (stepped) tan = float.PositiveInfinity;
                else if (i == 0) tan = Slope(t, v, 0, 1);
                else if (i == n - 1) tan = Slope(t, v, n - 2, n - 1);
                else tan = Slope(t, v, i - 1, i + 1);
                keys[i] = new Keyframe(t[i], v[i], tan, tan);
            }
            return new AnimationCurve(keys);
        }

        static float Slope(float[] t, float[] v, int a, int b)
        {
            float dt = t[b] - t[a];
            return Mathf.Abs(dt) < 1e-9f ? 0f : (v[b] - v[a]) / dt;
        }

        /// <summary>毎フレームのオイラー角を求め、±360 とジンバル反転を補正してカーブを連続させる</summary>
        static Vector3[] UnwrappedEuler(MayaCamState[] frames)
        {
            var r = new Vector3[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                var e = frames[i].Rotation.eulerAngles;
                if (i == 0)
                {
                    r[i] = new Vector3(Wrap180(e.x), Wrap180(e.y), Wrap180(e.z));
                    continue;
                }
                var prev = r[i - 1];
                var a = Unwrap(e, prev);
                var b = Unwrap(new Vector3(180f - e.x, e.y + 180f, e.z + 180f), prev); // 等価なオイラー角
                r[i] = (a - prev).sqrMagnitude <= (b - prev).sqrMagnitude ? a : b;
            }
            return r;
        }

        static Vector3 Unwrap(Vector3 e, Vector3 prev) =>
            new Vector3(UnwrapAngle(e.x, prev.x), UnwrapAngle(e.y, prev.y), UnwrapAngle(e.z, prev.z));

        static float UnwrapAngle(float a, float prev)
        {
            float d = Mathf.Repeat(a - prev + 180f, 360f) - 180f;
            return prev + d;
        }

        static float Wrap180(float a) => Mathf.Repeat(a + 180f, 360f) - 180f;
    }
}
