# -*- coding: utf-8 -*-
"""
Maya Cam Link  —  Maya カメラ -> Unity Cinemachine リアルタイム連携

使い方 (Script Editor の Python タブ / シェルフボタン):
    import maya_cam_link
    maya_cam_link.show()

更新後に再読み込み:
    import importlib, maya_cam_link
    maya_cam_link.stop(); importlib.reload(maya_cam_link); maya_cam_link.show()

  * ライブ   : カメラ / レンズ / 被写界深度の変更を Unity に即時反映
  * 時間同期 : Maya のタイムスライダー → Unity Timeline 再生ヘッド
  * キー同期 : キーを編集して 0.6 秒止まると全フレームをベイクして送信 → Timeline
  * ボケ     : F値 / ピント / ボケ強度 をパネルのスライダーで調整
               (ボケ強度はカメラシェイプのカスタムアトリビュートとしてキー可能)

座標変換は Maya 側で実施: Maya 右手系 cm -> Unity 左手系 m (X 反転, FBX と同じ)
Maya 2022+ (PySide2) / Maya 2025+ (PySide6)
"""
from __future__ import absolute_import, division, print_function

import json
import math
import socket
import time

import maya.cmds as cmds
import maya.OpenMayaUI as omui
import maya.api.OpenMaya as om

try:
    from PySide6 import QtCore, QtGui, QtWidgets
    from shiboken6 import wrapInstance
except ImportError:  # Maya 2022-2024
    from PySide2 import QtCore, QtGui, QtWidgets
    from shiboken2 import wrapInstance

VERSION = "1.1.0"
PROTOCOL = 1
DEFAULT_PORT = 7788
OPT_PREFIX = "mayaCamLink_"

ATTR_BOOST = "mclBokehBoost"
ATTR_MAXBLUR = "mclMaxBlur"
DEFAULT_BOOST = 1.0
DEFAULT_MAXBLUR = 6.0

LINEAR_TO_M = {
    "mm": 0.001, "cm": 0.01, "m": 1.0, "km": 1000.0,
    "in": 0.0254, "ft": 0.3048, "yd": 0.9144, "mi": 1609.344,
}
FPS_NAMES = {
    "game": 15.0, "film": 24.0, "pal": 25.0, "ntsc": 30.0, "show": 48.0,
    "palf": 50.0, "ntscf": 60.0, "millisec": 1000.0, "sec": 1.0,
}
# Maya filmFit (0 Fill,1 Horizontal,2 Vertical,3 Overscan) -> Unity GateFitMode
GATE_FIT_TO_UNITY = {0: 3, 1: 2, 2: 1, 3: 4}

INCH_TO_MM = 25.4
INTERNAL_CM_TO_M = 0.01  # API の行列は常に内部単位 cm


# ---------------------------------------------------------------------------
# シーン情報
# ---------------------------------------------------------------------------
def unit_scale():
    """現在のリニア単位 -> m"""
    return LINEAR_TO_M.get(cmds.currentUnit(q=True, linear=True), 0.01)


def angle_to_deg(v):
    return math.degrees(v) if cmds.currentUnit(q=True, angle=True) == "rad" else v


def scene_fps():
    u = cmds.currentUnit(q=True, time=True)
    if u in FPS_NAMES:
        return FPS_NAMES[u]
    try:
        return float(u.replace("fps", ""))
    except ValueError:
        return 24.0


def playback_range():
    return (float(cmds.playbackOptions(q=True, min=True)),
            float(cmds.playbackOptions(q=True, max=True)))


def render_resolution():
    try:
        return (int(cmds.getAttr("defaultResolution.width")),
                int(cmds.getAttr("defaultResolution.height")))
    except Exception:
        return (1920, 1080)


def list_cameras():
    """[(表示名, transform, shape)] 正投影カメラは除外"""
    out = []
    for shape in cmds.ls(type="camera", long=True) or []:
        if cmds.getAttr(shape + ".orthographic"):
            continue
        parents = cmds.listRelatives(shape, parent=True, fullPath=True) or []
        if not parents:
            continue
        xf = parents[0]
        out.append((cmds.ls(xf)[0], xf, shape))
    out.sort(key=lambda x: (x[0] == "persp", x[0].lower()))
    return out


def short_name(xf):
    return xf.split("|")[-1].split(":")[-1]


def has_attr(node, attr):
    return cmds.attributeQuery(attr, node=node, exists=True)


def ensure_bokeh_attrs(shape):
    """ボケ演出用のカスタムアトリビュートを追加 (キー可能)"""
    if not has_attr(shape, ATTR_BOOST):
        cmds.addAttr(shape, ln=ATTR_BOOST, nn="Bokeh Boost", at="double",
                     min=0.0, smx=20.0, dv=DEFAULT_BOOST, k=True)
    if not has_attr(shape, ATTR_MAXBLUR):
        cmds.addAttr(shape, ln=ATTR_MAXBLUR, nn="Max Blur %", at="double",
                     min=0.5, max=25.0, dv=DEFAULT_MAXBLUR, k=True)


# ---------------------------------------------------------------------------
# サンプリング
# ---------------------------------------------------------------------------
def _world_matrix(xf, t=None):
    sel = om.MSelectionList()
    sel.add(xf)
    node = sel.getDependNode(0)
    plug = om.MFnDependencyNode(node).findPlug("worldMatrix", False).elementByLogicalIndex(0)
    if t is None:
        mobj = plug.asMObject()
    else:
        ctx = om.MDGContext(om.MTime(t, om.MTime.uiUnit()))
        try:
            prev = ctx.makeCurrent()
            try:
                mobj = plug.asMObject()
            finally:
                prev.makeCurrent()
        except AttributeError:
            mobj = plug.asMObject(ctx)
    return om.MFnMatrixData(mobj).matrix()


def _ga(attr, t):
    return cmds.getAttr(attr) if t is None else cmds.getAttr(attr, time=t)


def _norm(v):
    l = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    return (v[0] / l, v[1] / l, v[2] / l) if l > 1e-12 else (0.0, 0.0, 1.0)


def sample_camera(xf, shape, t=None, use_arnold=False):
    """1 フレーム分のカメラ情報 (Unity 座標系)。t=None は現在時刻"""
    us = unit_scale()
    m = _world_matrix(xf, t)
    row = lambda r: (m.getElement(r, 0), m.getElement(r, 1), m.getElement(r, 2))
    yax = _norm(row(1))
    zax = _norm(row(2))
    tr = row(3)

    fwd_m = (-zax[0], -zax[1], -zax[2])  # Maya カメラは -Z を向く
    pos = (-tr[0] * INTERNAL_CM_TO_M, tr[1] * INTERNAL_CM_TO_M, tr[2] * INTERNAL_CM_TO_M)
    fwd = (-fwd_m[0], fwd_m[1], fwd_m[2])
    up = (-yax[0], yax[1], yax[2])

    focal = float(_ga(shape + ".focalLength", t))
    cam_scale = float(_ga(shape + ".cameraScale", t)) or 1.0
    h_ap = float(_ga(shape + ".horizontalFilmAperture", t))
    v_ap = float(_ga(shape + ".verticalFilmAperture", t))
    h_off = float(_ga(shape + ".horizontalFilmOffset", t))
    v_off = float(_ga(shape + ".verticalFilmOffset", t))
    squeeze = float(_ga(shape + ".lensSqueezeRatio", t)) or 1.0

    sensor_w = h_ap * INCH_TO_MM * cam_scale * squeeze
    sensor_h = v_ap * INCH_TO_MM * cam_scale

    near = float(_ga(shape + ".nearClipPlane", t)) * us
    far = float(_ga(shape + ".farClipPlane", t)) * us

    dof = bool(_ga(shape + ".depthOfField", t))
    focus = float(_ga(shape + ".focusDistance", t)) * us
    fstop = float(_ga(shape + ".fStop", t))

    if use_arnold and has_attr(shape, "aiEnableDOF"):
        dof = bool(_ga(shape + ".aiEnableDOF", t))
        focus = float(_ga(shape + ".aiFocusDistance", t)) * us
        ap_radius_mm = float(_ga(shape + ".aiApertureSize", t)) * us * 1000.0
        fstop = focal / (2.0 * ap_radius_mm) if ap_radius_mm > 1e-6 else 32.0

    boost = float(_ga(shape + "." + ATTR_BOOST, t)) if has_attr(shape, ATTR_BOOST) else DEFAULT_BOOST
    max_blur = float(_ga(shape + "." + ATTR_MAXBLUR, t)) if has_attr(shape, ATTR_MAXBLUR) else DEFAULT_MAXBLUR

    try:
        shutter = angle_to_deg(float(_ga(shape + ".shutterAngle", t)))
    except Exception:
        shutter = 144.0

    r6 = lambda v: round(float(v), 6)
    return {
        "px": r6(pos[0]), "py": r6(pos[1]), "pz": r6(pos[2]),
        "fx": r6(fwd[0]), "fy": r6(fwd[1]), "fz": r6(fwd[2]),
        "ux": r6(up[0]), "uy": r6(up[1]), "uz": r6(up[2]),
        "focal": r6(focal),
        "sensorW": r6(sensor_w), "sensorH": r6(sensor_h),
        "shiftX": r6(h_off / h_ap if h_ap else 0.0),
        "shiftY": r6(v_off / v_ap if v_ap else 0.0),
        "near": r6(near), "far": r6(far),
        "dof": 1 if dof else 0,
        "focus": r6(max(focus, 0.001)),
        "fstop": r6(fstop),
        "boost": r6(boost),
        "maxBlur": r6(max_blur),
        "shutter": r6(shutter),
        "gateFit": GATE_FIT_TO_UNITY.get(int(cmds.getAttr(shape + ".filmFit")), 2),
    }


def anim_curves_for(xf, shape):
    """カメラ (親・コンストレイン上流を含む) に関係する animCurve"""
    nodes = [shape]
    p = xf
    while p:
        nodes.append(p)
        par = cmds.listRelatives(p, parent=True, fullPath=True)
        p = par[0] if par else None
    curves, seen = set(), set()
    frontier = list(nodes)
    depth = 0
    while frontier and depth < 6 and len(seen) < 2000:
        nxt = []
        for n in frontier:
            if n in seen:
                continue
            seen.add(n)
            for up in cmds.listConnections(n, s=True, d=False, skipConversionNodes=True) or []:
                if cmds.objectType(up, isAType="animCurve"):
                    curves.add(up)
                elif up not in seen and not cmds.objectType(up, isAType="time"):
                    nxt.append(up)
        frontier = nxt
        depth += 1
    return sorted(curves)


def key_signature(curves):
    parts = []
    for c in curves:
        if not cmds.objExists(c):
            continue
        parts.append((
            c,
            tuple(cmds.keyframe(c, q=True, tc=True) or ()),
            tuple(cmds.keyframe(c, q=True, vc=True) or ()),
            tuple(cmds.keyTangent(c, q=True, inAngle=True) or ()),
            tuple(cmds.keyTangent(c, q=True, outAngle=True) or ()),
        ))
    parts.append(playback_range())
    parts.append(scene_fps())
    parts.append(cmds.currentUnit(q=True, linear=True))
    return hash(repr(parts))


# ---------------------------------------------------------------------------
# 連携コントローラー
# ---------------------------------------------------------------------------
class LinkController(QtCore.QObject):
    statusChanged = QtCore.Signal(str, str)   # (メッセージ, 状態: off/wait/ok/err)

    TICK_MS = 33
    RECONNECT_SEC = 1.5
    SIG_INTERVAL = 0.5
    CURVE_LIST_INTERVAL = 3.0
    BAKE_DEBOUNCE = 0.6

    def __init__(self, parent=None):
        super(LinkController, self).__init__(parent)
        self.host = "127.0.0.1"
        self.port = DEFAULT_PORT
        self.xf = None
        self.shape = None
        self.live = True
        self.sync_time = True
        self.auto_keys = True
        self.use_arnold = False

        self.enabled = False
        self.sock = None
        self.state = "off"
        self._last_status = None
        self._last_attempt = 0.0
        self._last_live = None
        self._last_state = None
        self._last_frame = None
        self._curves = []
        self._curves_at = 0.0
        self._key_sig = None
        self._sig_at = 0.0
        self._dirty_since = None
        self._err_at = 0.0
        self.last_send_at = 0.0

        self.timer = QtCore.QTimer(self)
        self.timer.setInterval(self.TICK_MS)
        self.timer.timeout.connect(self._tick)

    # ---------------- 接続 ----------------
    def start(self):
        self.enabled = True
        self._last_attempt = 0.0
        self.timer.start()
        self._status(u"接続中…", "wait")

    def stop(self):
        self.enabled = False
        self.timer.stop()
        self._close()
        self._status(u"切断", "off")

    @property
    def connected(self):
        return self.sock is not None

    def _status(self, msg, state=None):
        if state:
            self.state = state
        if msg != self._last_status:
            self._last_status = msg
            print(u"[MayaCamLink] " + msg)
        self.statusChanged.emit(msg, self.state)

    def _close(self):
        if self.sock:
            try:
                self.sock.close()
            except Exception:
                pass
        self.sock = None

    def _ensure_connected(self):
        if self.sock:
            return True
        now = time.time()
        if now - self._last_attempt < self.RECONNECT_SEC:
            return False
        self._last_attempt = now
        try:
            s = socket.create_connection((self.host, self.port), timeout=0.25)
            s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            s.settimeout(5.0)
            self.sock = s
        except (socket.error, OSError):
            self._status(u"Unity 待機中  %s:%d" % (self.host, self.port), "wait")
            return False
        self._last_live = None
        self._key_sig = None
        self._dirty_since = time.time() - self.BAKE_DEBOUNCE if self.auto_keys else None
        self._send({"type": "hello", "version": PROTOCOL, "app": "maya",
                    "mayaVersion": cmds.about(version=True)})
        self._status(u"接続  %s:%d" % (self.host, self.port), "ok")
        return self.sock is not None

    def _send(self, payload):
        if not self.sock:
            return False
        data = (json.dumps(payload, separators=(",", ":")) + "\n").encode("utf-8")
        try:
            self.sock.sendall(data)
            self.last_send_at = time.time()
            return True
        except (socket.error, OSError) as e:
            self._close()
            self._status(u"切断されました (再接続します): %s" % e, "err")
            return False

    # ---------------- カメラ ----------------
    def set_camera(self, xf, shape):
        self.xf, self.shape = xf, shape
        self._last_live = None
        self._last_state = None
        self._curves = []
        self._curves_at = 0.0
        self._key_sig = None
        if self.auto_keys and self.connected:
            self.mark_dirty()

    def camera_name(self):
        return short_name(self.xf) if self.xf else ""

    def camera_ok(self):
        return bool(self.xf and self.shape and cmds.objExists(self.xf) and cmds.objExists(self.shape))

    # ---------------- ペイロード ----------------
    def build_bake(self):
        start, end = playback_range()
        s, e = int(math.floor(start)), int(math.ceil(end))
        frames = []
        for f in range(s, e + 1):
            st = sample_camera(self.xf, self.shape, float(f), self.use_arnold)
            st["frame"] = float(f)
            frames.append(st)
        rw, rh = render_resolution()
        return {"type": "bake", "version": PROTOCOL, "camera": self.camera_name(),
                "fps": scene_fps(), "start": start, "end": end, "resW": rw, "resH": rh,
                "frames": frames}

    def send_bake(self):
        if not self.camera_ok() or not self.connected:
            return False
        t0 = time.time()
        payload = self.build_bake()
        ok = self._send(payload)
        self._dirty_since = None
        if ok:
            self._status(u"キーを送信しました  %d フレーム  (%.2f秒)" % (len(payload["frames"]), time.time() - t0), "ok")
        return ok

    def export_json(self, path):
        payload = self.build_bake()
        with open(path, "w") as f:
            json.dump(payload, f, separators=(",", ":"))
        return len(payload["frames"])

    def mark_dirty(self):
        self._dirty_since = time.time()

    # ---------------- メインループ ----------------
    def _tick(self):
        try:
            self._tick_impl()
        except Exception as e:
            now = time.time()
            if now - self._err_at > 3.0:
                self._err_at = now
                cmds.warning("[MayaCamLink] %s" % e)

    def _tick_impl(self):
        if not self.enabled or not self._ensure_connected():
            return
        if not self.camera_ok():
            return

        now = time.time()
        playing = bool(cmds.play(q=True, state=True))
        frame = float(cmds.currentTime(q=True))
        start, _ = playback_range()

        state = None
        if self.live:
            state = sample_camera(self.xf, self.shape, None, self.use_arnold)
            msg = {"type": "live", "version": PROTOCOL, "camera": self.camera_name(),
                   "fps": scene_fps(), "frame": frame, "start": start,
                   "syncTime": 1 if self.sync_time else 0, "state": state}
            s = json.dumps(msg, separators=(",", ":"), sort_keys=True)
            if s != self._last_live:
                if not self._send(msg):
                    return
                self._last_live = s
            # 同じフレームで値が変化 = 調整中
            if (not playing and frame == self._last_frame and self._last_state is not None
                    and state != self._last_state):
                self.mark_dirty()
        self._last_state = state
        self._last_frame = frame

        if not self.auto_keys or playing:
            return

        if now - self._curves_at > self.CURVE_LIST_INTERVAL:
            self._curves = anim_curves_for(self.xf, self.shape)
            self._curves_at = now
        if now - self._sig_at > self.SIG_INTERVAL:
            self._sig_at = now
            sig = key_signature(self._curves)
            if sig != self._key_sig:
                if self._key_sig is not None:
                    self.mark_dirty()
                self._key_sig = sig

        # ドラッグ中は値が変わり続けて _dirty_since が延長され、止まってからベイク
        if self._dirty_since is not None and now - self._dirty_since > self.BAKE_DEBOUNCE:
            self.send_bake()


# ---------------------------------------------------------------------------
# UI 部品
# ---------------------------------------------------------------------------
def _maya_main_window():
    ptr = omui.MQtUtil.mainWindow()
    return wrapInstance(int(ptr), QtWidgets.QWidget) if ptr else None


def _opt(name, default):
    key = OPT_PREFIX + name
    if cmds.optionVar(exists=key):
        return cmds.optionVar(q=key)
    return default


def _set_opt(name, value):
    key = OPT_PREFIX + name
    if isinstance(value, bool):
        cmds.optionVar(iv=(key, int(value)))
    elif isinstance(value, int):
        cmds.optionVar(iv=(key, value))
    else:
        cmds.optionVar(sv=(key, str(value)))


STYLE = """
QDialog { background:#262a30; }
QLabel { color:#d8dde3; }
QLabel#dim { color:#7d8590; font-size:11px; }
QLabel#icon { color:#c9d1d9; font-size:12px; min-width:58px; }
QLabel#section { color:#9fb3c8; font-weight:bold; font-size:11px; letter-spacing:1px; }
QComboBox, QLineEdit, QSpinBox, QDoubleSpinBox {
    background:#1c1f24; color:#e6e9ee; border:1px solid #3a404a; border-radius:4px; padding:3px 6px; }
QPushButton { background:#363c46; color:#e6e9ee; border:none; border-radius:5px; padding:6px 10px; }
QPushButton:hover { background:#434a56; }
QPushButton:pressed { background:#2b3038; }
QPushButton[chip="true"] { background:#30353d; color:#8b949e; padding:6px 4px; font-size:12px; }
QPushButton[chip="true"]:checked { background:#2d6cb5; color:white; }
QPushButton#connect { font-size:14px; font-weight:bold; padding:9px; border-radius:6px; }
QPushButton#tool { padding:4px 10px; font-size:12px; }
QPushButton#dof { padding:3px 10px; font-weight:bold; border-radius:10px; background:#30353d; color:#8b949e; }
QPushButton#dof:checked { background:#d08a2e; color:#1c1f24; }
QSlider::groove:horizontal { height:4px; background:#3a404a; border-radius:2px; }
QSlider::sub-page:horizontal { background:#d08a2e; border-radius:2px; }
QSlider::handle:horizontal { background:#e6e9ee; width:12px; margin:-5px 0; border-radius:6px; }
QFrame#card { background:#2e333b; border-radius:8px; }
"""


class ParamSlider(QtWidgets.QWidget):
    """アイコン + スライダー(対数/線形) + 数値"""
    valueEdited = QtCore.Signal(float)
    pressed = QtCore.Signal()
    released = QtCore.Signal()

    STEPS = 1000

    def __init__(self, icon, tip, lo, hi, log=True, decimals=1, prefix="", suffix="", parent=None):
        super(ParamSlider, self).__init__(parent)
        self.lo, self.hi, self.log = lo, hi, log
        lay = QtWidgets.QHBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(6)
        ic = QtWidgets.QLabel(icon)
        ic.setObjectName("icon")
        ic.setToolTip(tip)
        ic.setAlignment(QtCore.Qt.AlignLeft | QtCore.Qt.AlignVCenter)
        lay.addWidget(ic)
        self.slider = QtWidgets.QSlider(QtCore.Qt.Horizontal)
        self.slider.setRange(0, self.STEPS)
        self.slider.setToolTip(tip)
        lay.addWidget(self.slider, 1)
        self.spin = QtWidgets.QDoubleSpinBox()
        self.spin.setRange(lo, hi)
        self.spin.setDecimals(decimals)
        self.spin.setPrefix(prefix)
        self.spin.setSuffix(suffix)
        self.spin.setButtonSymbols(QtWidgets.QAbstractSpinBox.NoButtons)
        self.spin.setFixedWidth(72)
        self.spin.setAlignment(QtCore.Qt.AlignRight)
        self.spin.setKeyboardTracking(False)
        lay.addWidget(self.spin)
        self.extra = lay

        self.slider.valueChanged.connect(self._from_slider)
        self.slider.sliderPressed.connect(self.pressed.emit)
        self.slider.sliderReleased.connect(self.released.emit)
        self.spin.valueChanged.connect(self._from_spin)

    def _to_pos(self, v):
        v = min(max(v, self.lo), self.hi)
        if self.log:
            t = (math.log(v) - math.log(self.lo)) / (math.log(self.hi) - math.log(self.lo))
        else:
            t = (v - self.lo) / (self.hi - self.lo)
        return int(round(t * self.STEPS))

    def _from_pos(self, p):
        t = p / float(self.STEPS)
        if self.log:
            return math.exp(math.log(self.lo) + t * (math.log(self.hi) - math.log(self.lo)))
        return self.lo + t * (self.hi - self.lo)

    def _from_slider(self, p):
        v = self._from_pos(p)
        self.spin.blockSignals(True)
        self.spin.setValue(v)
        self.spin.blockSignals(False)
        self.valueEdited.emit(self.spin.value())

    def _from_spin(self, v):
        self.slider.blockSignals(True)
        self.slider.setValue(self._to_pos(v))
        self.slider.blockSignals(False)
        self.valueEdited.emit(v)

    def busy(self):
        return self.slider.isSliderDown() or self.spin.hasFocus()

    def set_value(self, v):
        if self.busy():
            return
        for w in (self.slider, self.spin):
            w.blockSignals(True)
        self.spin.setValue(v)
        self.slider.setValue(self._to_pos(v))
        for w in (self.slider, self.spin):
            w.blockSignals(False)


def _chip(text, tip):
    b = QtWidgets.QPushButton(text)
    b.setCheckable(True)
    b.setProperty("chip", True)
    b.setToolTip(tip)
    return b


def _tool(text, tip):
    b = QtWidgets.QPushButton(text)
    b.setObjectName("tool")
    b.setToolTip(tip)
    return b


# ---------------------------------------------------------------------------
# メインウィンドウ
# ---------------------------------------------------------------------------
class MayaCamLinkWindow(QtWidgets.QDialog):
    OBJECT_NAME = "MayaCamLinkWindow"
    DOT_COLORS = {"off": "#6e7681", "wait": "#d29922", "ok": "#3fb950", "err": "#f85149"}

    def __init__(self, controller, parent=None):
        super(MayaCamLinkWindow, self).__init__(parent or _maya_main_window())
        self.ctrl = controller
        self.setObjectName(self.OBJECT_NAME)
        self.setWindowTitle(u"Maya Cam Link")
        self.setWindowFlags(self.windowFlags() | QtCore.Qt.Tool)
        self.setStyleSheet(STYLE)
        self.setMinimumWidth(340)
        self._cams = []
        self._undo_open = False
        self._build()
        self._load_settings()
        self.refresh_cameras()
        self.ctrl.statusChanged.connect(self._on_status)
        self._on_status(self.ctrl._last_status or u"未接続", self.ctrl.state)
        self._update_connect_button()

        self.sync_timer = QtCore.QTimer(self)
        self.sync_timer.setInterval(250)
        self.sync_timer.timeout.connect(self._sync_from_maya)
        self.sync_timer.start()

    # ---------------- 構築 ----------------
    def _build(self):
        lay = QtWidgets.QVBoxLayout(self)
        lay.setContentsMargins(10, 10, 10, 10)
        lay.setSpacing(8)

        # ヘッダー: 状態ドット + カメラ
        row = QtWidgets.QHBoxLayout()
        self.dot = QtWidgets.QLabel(u"●")
        self.dot.setStyleSheet("font-size:18px;")
        row.addWidget(self.dot)
        self.cam_combo = QtWidgets.QComboBox()
        self.cam_combo.setToolTip(u"カメラ")
        self.cam_combo.currentIndexChanged.connect(self._on_camera_changed)
        row.addWidget(self.cam_combo, 1)
        b = _tool(u"一覧更新", u"カメラ一覧を更新")
        b.clicked.connect(self.refresh_cameras)
        row.addWidget(b)
        b = _tool(u"選択から", u"選択中のカメラを使う")
        b.clicked.connect(self.use_selected)
        row.addWidget(b)
        lay.addLayout(row)

        # 接続
        self.connect_btn = QtWidgets.QPushButton()
        self.connect_btn.setObjectName("connect")
        self.connect_btn.clicked.connect(self.toggle_connect)
        lay.addWidget(self.connect_btn)

        # 同期トグル
        row = QtWidgets.QHBoxLayout()
        row.setSpacing(4)
        self.cb_live = _chip(u"ライブ", u"カメラをリアルタイム反映")
        self.cb_sync = _chip(u"時間同期", u"Unity Timeline の再生ヘッドを同期")
        self.cb_keys = _chip(u"キー同期", u"キー編集を自動で Timeline に送信")
        for c in (self.cb_live, self.cb_sync, self.cb_keys):
            c.toggled.connect(self._apply_options)
            row.addWidget(c)
        lay.addLayout(row)

        # ---- 被写界深度カード ----
        card = QtWidgets.QFrame()
        card.setObjectName("card")
        cl = QtWidgets.QVBoxLayout(card)
        cl.setContentsMargins(10, 8, 10, 10)
        cl.setSpacing(6)
        head = QtWidgets.QHBoxLayout()
        lab = QtWidgets.QLabel(u"被写界深度")
        lab.setObjectName("section")
        head.addWidget(lab)
        head.addStretch(1)
        self.dof_btn = QtWidgets.QPushButton(u"OFF")
        self.dof_btn.setObjectName("dof")
        self.dof_btn.setCheckable(True)
        self.dof_btn.setToolTip(u"被写界深度 ON / OFF")
        self.dof_btn.toggled.connect(self._on_dof_toggled)
        head.addWidget(self.dof_btn)
        cl.addLayout(head)

        self.p_fstop = ParamSlider(u"F値", u"F値 (小さいほどボケる)", 0.7, 22.0, log=True, decimals=1, prefix="f/")
        self.p_focus = ParamSlider(u"ピント", u"ピント距離", 0.1, 200.0, log=True, decimals=2, suffix=" m")
        self.p_boost = ParamSlider(u"ボケ強度", u"ボケ強度 (Unity 側の実効F値 = F値 ÷ 強度)", 0.25, 8.0, log=True,
                                   decimals=2, prefix=u"×")
        pick = _tool(u"選択に合わせる", u"選択オブジェクトにピントを合わせる")
        pick.clicked.connect(self.focus_selected)
        self.p_focus.extra.addWidget(pick)

        bind = ((self.p_fstop, self._write_fstop), (self.p_focus, self._write_focus),
                (self.p_boost, lambda v: self._write_custom(ATTR_BOOST, v)))
        for p, fn in bind:
            p.valueEdited.connect(fn)
            p.pressed.connect(self._undo_begin)
            p.released.connect(self._undo_end)
            cl.addWidget(p)

        foot = QtWidgets.QHBoxLayout()
        b = QtWidgets.QPushButton(u"キーを打つ")
        b.setToolTip(u"現在フレームに DOF 関連のキーを打つ")
        b.clicked.connect(self.key_dof)
        foot.addWidget(b)
        b = QtWidgets.QPushButton(u"ボケ強度リセット")
        b.setToolTip(u"ボケ強度をリセット")
        b.clicked.connect(self.reset_boost)
        foot.addWidget(b)
        foot.addStretch(1)
        cl.addLayout(foot)
        lay.addWidget(card)

        # ツール行
        row = QtWidgets.QHBoxLayout()
        b = _tool(u"キーを今すぐ送信", u"全フレームをベイクして Unity に送信")
        b.clicked.connect(self.send_now)
        row.addWidget(b)
        b = _tool(u"JSON 書き出し", u"JSON ファイルに書き出す")
        b.clicked.connect(self.export_json)
        row.addWidget(b)
        self.info = QtWidgets.QLabel()
        self.info.setObjectName("dim")
        row.addWidget(self.info, 1)
        self.gear = _tool(u"設定", u"接続先 / Arnold")
        self.gear.setCheckable(True)
        self.gear.toggled.connect(lambda on: self.settings.setVisible(on))
        row.addWidget(self.gear)
        lay.addLayout(row)

        # 設定 (折りたたみ)
        self.settings = QtWidgets.QFrame()
        sl = QtWidgets.QHBoxLayout(self.settings)
        sl.setContentsMargins(0, 0, 0, 0)
        self.host_edit = QtWidgets.QLineEdit("127.0.0.1")
        self.host_edit.setToolTip(u"Unity のホスト")
        sl.addWidget(self.host_edit, 1)
        self.port_spin = QtWidgets.QSpinBox()
        self.port_spin.setRange(1024, 65535)
        self.port_spin.setValue(DEFAULT_PORT)
        self.port_spin.setToolTip(u"ポート")
        sl.addWidget(self.port_spin)
        self.cb_arnold = QtWidgets.QCheckBox(u"Arnold")
        self.cb_arnold.setToolTip(u"aiFocusDistance / aiApertureSize を使う")
        self.cb_arnold.toggled.connect(self._apply_options)
        sl.addWidget(self.cb_arnold)
        self.settings.setVisible(False)
        lay.addWidget(self.settings)

        self.status = QtWidgets.QLabel()
        self.status.setObjectName("dim")
        lay.addWidget(self.status)

    # ---------------- 設定 ----------------
    def _load_settings(self):
        self.host_edit.setText(_opt("host", "127.0.0.1"))
        self.port_spin.setValue(int(_opt("port", DEFAULT_PORT)))
        for cb, key, default in ((self.cb_live, "live", 1), (self.cb_sync, "sync", 1),
                                 (self.cb_keys, "keys", 1), (self.cb_arnold, "arnold", 0)):
            cb.blockSignals(True)
            cb.setChecked(bool(_opt(key, default)))
            cb.blockSignals(False)
        self._apply_options()

    def _save_settings(self):
        _set_opt("host", self.host_edit.text().strip())
        _set_opt("port", int(self.port_spin.value()))
        _set_opt("live", self.cb_live.isChecked())
        _set_opt("sync", self.cb_sync.isChecked())
        _set_opt("keys", self.cb_keys.isChecked())
        _set_opt("arnold", self.cb_arnold.isChecked())
        if self.ctrl.xf:
            _set_opt("camera", self.ctrl.xf)

    def _apply_options(self, *_):
        c = self.ctrl
        old_keys, old_arnold = c.auto_keys, c.use_arnold
        c.live = self.cb_live.isChecked()
        c.sync_time = self.cb_sync.isChecked()
        c.auto_keys = self.cb_keys.isChecked()
        c.use_arnold = self.cb_arnold.isChecked()
        c._last_live = None
        if c.connected and c.auto_keys and (not old_keys or old_arnold != c.use_arnold):
            c.mark_dirty()
        self._save_settings()

    # ---------------- カメラ ----------------
    def refresh_cameras(self):
        cur = self.ctrl.xf or _opt("camera", "")
        self._cams = list_cameras()
        self.cam_combo.blockSignals(True)
        self.cam_combo.clear()
        idx = 0
        for i, (label, xf, shape) in enumerate(self._cams):
            self.cam_combo.addItem(label)
            if xf == cur:
                idx = i
        self.cam_combo.blockSignals(False)
        if self._cams:
            self.cam_combo.setCurrentIndex(idx)
            self._on_camera_changed(idx)

    def use_selected(self):
        for s in cmds.ls(sl=True, long=True) or []:
            shapes = [s] if cmds.nodeType(s) == "camera" else (
                cmds.listRelatives(s, shapes=True, type="camera", fullPath=True) or [])
            if shapes:
                xf = cmds.listRelatives(shapes[0], parent=True, fullPath=True)[0]
                for i, (_, cxf, _) in enumerate(self._cams):
                    if cxf == xf:
                        self.cam_combo.setCurrentIndex(i)
                        return
                self.refresh_cameras()
                return
        cmds.warning(u"カメラを選択してください")

    def _on_camera_changed(self, idx):
        if 0 <= idx < len(self._cams):
            _, xf, shape = self._cams[idx]
            self.ctrl.set_camera(xf, shape)
            self._save_settings()
            self._sync_from_maya(force=True)

    # ---------------- Maya -> UI ----------------
    def _sync_from_maya(self, force=False):
        c = self.ctrl
        s, e = playback_range()
        rw, rh = render_resolution()
        self.info.setText(u"解像度 %d×%d ・ 範囲 %g–%g ・ %g fps" % (rw, rh, s, e, scene_fps()))
        if not c.camera_ok():
            return
        sh = c.shape
        us = unit_scale()
        try:
            if c.use_arnold and has_attr(sh, "aiEnableDOF"):
                dof = bool(cmds.getAttr(sh + ".aiEnableDOF"))
                focus = cmds.getAttr(sh + ".aiFocusDistance") * us
                rad_mm = cmds.getAttr(sh + ".aiApertureSize") * us * 1000.0
                fstop = cmds.getAttr(sh + ".focalLength") / (2 * rad_mm) if rad_mm > 1e-6 else 22.0
            else:
                dof = bool(cmds.getAttr(sh + ".depthOfField"))
                focus = cmds.getAttr(sh + ".focusDistance") * us
                fstop = cmds.getAttr(sh + ".fStop")
            boost = cmds.getAttr(sh + "." + ATTR_BOOST) if has_attr(sh, ATTR_BOOST) else DEFAULT_BOOST
        except Exception:
            return
        self.dof_btn.blockSignals(True)
        self.dof_btn.setChecked(dof)
        self.dof_btn.setText(u"ON" if dof else u"OFF")
        self.dof_btn.blockSignals(False)
        self.p_fstop.set_value(fstop)
        self.p_focus.set_value(focus)
        self.p_boost.set_value(boost)

    # ---------------- UI -> Maya ----------------
    def _undo_begin(self):
        if not self._undo_open:
            cmds.undoInfo(openChunk=True, chunkName="MayaCamLink DOF")
            self._undo_open = True

    def _undo_end(self):
        if self._undo_open:
            cmds.undoInfo(closeChunk=True)
            self._undo_open = False

    def _set(self, attr, value):
        sh = self.ctrl.shape
        if not self.ctrl.camera_ok():
            return
        plug = sh + "." + attr
        try:
            cmds.setAttr(plug, value)
        except RuntimeError as e:
            self.status.setText(u"エラー %s: %s" % (attr, e))
            return
        # オートキー: 既にキーがある属性のみ
        if cmds.autoKeyframe(q=True, state=True) and cmds.keyframe(plug, q=True, keyframeCount=True):
            cmds.setKeyframe(plug)

    def _write_fstop(self, v):
        if self.ctrl.use_arnold and has_attr(self.ctrl.shape, "aiApertureSize"):
            focal = cmds.getAttr(self.ctrl.shape + ".focalLength")
            self._set("aiApertureSize", focal / (2.0 * v) / 1000.0 / unit_scale())
        else:
            self._set("fStop", v)

    def _write_focus(self, meters):
        ui = meters / unit_scale()
        if self.ctrl.use_arnold and has_attr(self.ctrl.shape, "aiFocusDistance"):
            self._set("aiFocusDistance", ui)
        else:
            self._set("focusDistance", ui)

    def _write_custom(self, attr, v):
        if not self.ctrl.camera_ok():
            return
        ensure_bokeh_attrs(self.ctrl.shape)
        self._set(attr, v)

    def _on_dof_toggled(self, on):
        self.dof_btn.setText(u"ON" if on else u"OFF")
        if not self.ctrl.camera_ok():
            return
        if self.ctrl.use_arnold and has_attr(self.ctrl.shape or "", "aiEnableDOF"):
            self._set("aiEnableDOF", on)
        else:
            self._set("depthOfField", on)

    def focus_selected(self):
        c = self.ctrl
        if not c.camera_ok():
            return
        sel = [s for s in (cmds.ls(sl=True, long=True, type="transform") or []) if s != c.xf]
        if not sel:
            cmds.warning(u"ピントを合わせるオブジェクトを選択してください")
            return
        bb = cmds.exactWorldBoundingBox(sel)
        center = ((bb[0] + bb[3]) * 0.5, (bb[1] + bb[4]) * 0.5, (bb[2] + bb[5]) * 0.5)
        cam = cmds.xform(c.xf, q=True, ws=True, rp=True)
        m = _world_matrix(c.xf)
        z = _norm((m.getElement(2, 0), m.getElement(2, 1), m.getElement(2, 2)))
        d = [center[i] - cam[i] for i in range(3)]
        dist = -(d[0] * z[0] + d[1] * z[1] + d[2] * z[2])  # -Z 方向への距離
        if dist <= 0:
            dist = math.sqrt(sum(x * x for x in d))
        meters = dist * unit_scale()
        self._write_focus(meters)
        self.p_focus.set_value(meters)
        self.status.setText(u"ピント: %s  %.2f m" % (short_name(sel[0]), meters))

    def key_dof(self):
        c = self.ctrl
        if not c.camera_ok():
            return
        ensure_bokeh_attrs(c.shape)
        attrs = ["depthOfField", "fStop", "focusDistance", ATTR_BOOST]
        if c.use_arnold and has_attr(c.shape, "aiEnableDOF"):
            attrs += ["aiEnableDOF", "aiFocusDistance", "aiApertureSize"]
        for a in attrs:
            cmds.setKeyframe(c.shape, at=a)
        self.status.setText(u"DOF のキーを打ちました  (フレーム %g)" % cmds.currentTime(q=True))

    def reset_boost(self):
        self._write_custom(ATTR_BOOST, DEFAULT_BOOST)
        self._sync_from_maya(force=True)

    # ---------------- アクション ----------------
    def toggle_connect(self):
        if self.ctrl.enabled:
            self.ctrl.stop()
        else:
            self.ctrl.host = self.host_edit.text().strip() or "127.0.0.1"
            self.ctrl.port = int(self.port_spin.value())
            self._save_settings()
            self.ctrl.start()
        self._update_connect_button()

    def _update_connect_button(self):
        if self.ctrl.enabled:
            self.connect_btn.setText(u"切断")
            self.connect_btn.setStyleSheet("background:#5a2e33;")
        else:
            self.connect_btn.setText(u"Unity に接続")
            self.connect_btn.setStyleSheet("background:#2f6b45;")

    def _on_status(self, msg, state):
        self.status.setText(msg)
        self.dot.setStyleSheet("font-size:18px; color:%s;" % self.DOT_COLORS.get(state, "#6e7681"))

    def send_now(self):
        if not self.ctrl.connected:
            cmds.warning(u"Unity に接続されていません")
            return
        self.ctrl.send_bake()

    def export_json(self):
        if not self.ctrl.camera_ok():
            cmds.warning(u"有効なカメラがありません")
            return
        path, _ = QtWidgets.QFileDialog.getSaveFileName(
            self, u"JSON 書き出し", self.ctrl.camera_name() + ".mayacam.json", "JSON (*.json)")
        if path:
            n = self.ctrl.export_json(path)
            self.status.setText(u"書き出しました  %d フレーム → %s" % (n, path))

    def closeEvent(self, e):
        self._undo_end()
        self.sync_timer.stop()
        super(MayaCamLinkWindow, self).closeEvent(e)


# ---------------------------------------------------------------------------
_controller = None
_window = None


def controller():
    global _controller
    if _controller is None:
        _controller = LinkController()
    return _controller


def show():
    """ウィンドウを開く (閉じても接続は維持。再度 show() で戻る)"""
    global _window
    try:
        if _window is not None:
            _window.close()
            _window.deleteLater()
    except Exception:
        pass
    _window = MayaCamLinkWindow(controller())
    _window.show()
    return _window


def stop():
    if _controller is not None:
        _controller.stop()
