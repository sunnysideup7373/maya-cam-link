using System;
using UnityEngine;

namespace MayaCamLink
{
    /// <summary>
    /// 1 フレーム分のカメラデータ。Maya 側で Unity 座標 (左手系・m) に変換済み。
    /// </summary>
    [Serializable]
    public class MayaCamState
    {
        public float frame;
        // 位置 / 向き (forward, up)
        public float px, py, pz;
        public float fx, fy, fz;
        public float ux, uy, uz;
        // レンズ (mm)
        public float focal;
        public float sensorW, sensorH;
        public float shiftX, shiftY;
        public float near, far;
        // 被写界深度
        public int dof;
        public float focus;
        public float fstop;
        public float shutter;
        // ボケ演出 (Maya カスタムアトリビュート mclBokehBoost / mclMaxBlur)
        public float boost = 1f;
        public float maxBlur = 6f;
        /// <summary>UnityEngine.Camera.GateFitMode の int 値</summary>
        public int gateFit = 2;

        public Vector3 Position => new Vector3(px, py, pz);

        public Quaternion Rotation
        {
            get
            {
                var f = new Vector3(fx, fy, fz);
                var u = new Vector3(ux, uy, uz);
                if (f.sqrMagnitude < 1e-10f) return Quaternion.identity;
                if (u.sqrMagnitude < 1e-10f) u = Vector3.up;
                return Quaternion.LookRotation(f, u);
            }
        }
    }

    [Serializable]
    public class MayaCamPacketHeader
    {
        public string type;
        public string camera;
        public int version;
    }

    [Serializable]
    public class MayaCamLivePacket
    {
        public string type;
        public string camera;
        public float fps = 24f;
        public float frame;
        public float start;
        public int syncTime = 1;
        public MayaCamState state;
    }

    [Serializable]
    public class MayaCamBakePacket
    {
        public string type;
        public string camera;
        public float fps = 24f;
        public float start;
        public float end;
        public int resW, resH;
        public MayaCamState[] frames;
    }
}
