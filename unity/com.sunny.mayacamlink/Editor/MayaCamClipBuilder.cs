using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace MayaCamLink.Editor
{
    /// <summary>
    /// Maya のベイクデータを AnimationClip に書き出し、Timeline の AnimationTrack に配置する。
    /// 初回は Maya Cam Link ウィンドウで名前を決めて作成し、以降は同じ .anim を上書き
    /// (GUID は変わらないので Timeline の参照も維持)。
    /// </summary>
    public static class MayaCamClipBuilder
    {
        const float ConstEps = 1e-5f;

        public static string TrackName(string camera) => "Maya: " + camera;

        /// <summary>Clip 名の設定から .anim のファイル名 (拡張子なし) を作る</summary>
        public static string ClipFileName(string camera)
        {
            string n = MayaCamLinkSettings.ClipName;
            n = Regex.Replace(n, @"\{cam\}", MayaCamLinkProcessor.Sanitize(camera), RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\{scene\}", MayaCamLinkProcessor.SceneName(), RegexOptions.IgnoreCase);
            return SanitizeFileName(n, camera);
        }

        /// <summary>ファイル名として使えない文字を置き換え、拡張子を外す</summary>
        public static string SanitizeFileName(string n, string camera)
        {
            n = n ?? "";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim();
            if (n.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 5);
            n = n.TrimEnd('.', ' ');
            return string.IsNullOrEmpty(n) ? MayaCamLinkProcessor.Sanitize(camera) : n;
        }

        /// <summary>カメラのトラックを探す (トラック名 → 無ければ Rig の Animator にバインドされたトラック)</summary>
        public static AnimationTrack FindCameraTrack(PlayableDirector director, TimelineAsset timeline,
            UnityEngine.Object binding, string camera)
        {
            string name = TrackName(camera);
            AnimationTrack byBinding = null;
            foreach (var t in timeline.GetOutputTracks().OfType<AnimationTrack>())
            {
                if (t.name == name) return t;
                if (byBinding == null && binding && director && director.GetGenericBinding(t) == binding)
                    byBinding = t;
            }
            return byBinding;
        }

        /// <summary>path (Assets/....anim) に書き出す。既にあれば中身だけ上書き</summary>
        public static AnimationClip Build(MayaCamBakePacket p, MayaCameraRig rig, PlayableDirector director, string path)
        {
            var frames = p.frames;
            int n = frames.Length;
            float fps = Mathf.Max(1f, p.fps);

            // ---- clip asset ----
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            bool isNew = clip == null;
            if (isNew)
            {
                MayaCamLinkProcessor.EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
                clip = new AnimationClip { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            }
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
                // 既存の Timeline に「Maya: カメラ名」トラックを追加 (他のトラックには触らない)
                var animator = rig.GetComponent<Animator>();
                if (!animator) animator = Undo.AddComponent<Animator>(rig.gameObject);
                var track = FindCameraTrack(director, timeline, animator, p.camera);
                if (track == null) track = timeline.CreateTrack<AnimationTrack>(null, TrackName(p.camera));
                track.trackOffset = TrackOffset.ApplySceneOffsets;

                double len = Math.Max(clip.length, 1.0 / fps);
                var tc = track.GetClips().FirstOrDefault();
                if (tc == null)
                {
                    tc = track.CreateClip(clip);
                    tc.start = 0;
                    tc.clipIn = 0;
                    tc.duration = len;
                }
                else
                {
                    if (tc.asset is AnimationPlayableAsset apa) apa.clip = clip;
                    // Timeline 上で動かした位置 (start) と先頭トリム (clipIn) はそのまま
                    tc.duration = Math.Max(len - tc.clipIn, 1.0 / fps);
                }
                tc.displayName = clip.name;
                director.SetGenericBinding(track, animator);

                EditorUtility.SetDirty(timeline);
                EditorUtility.SetDirty(director);
                AssetDatabase.SaveAssets();
                director.RebuildGraph();
                // トラックを追加した時は ContentsAddedOrRemoved でないと Timeline ウィンドウに表示されない
                TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved | RefreshReason.ContentsModified);
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
