# mayapy 用: テスト用 Maya シーンを作成し、ジオメトリを FBX で Unity に書き出す (カメラは連携ツールで同期するので書き出さない)
import os, sys
import maya.standalone
maya.standalone.initialize()
import maya.cmds as cmds
import maya.mel as mel

HERE = os.path.dirname(os.path.abspath(__file__))
MA_PATH = os.path.join(HERE, "maya_scene", "camlink_test.ma").replace("\\", "/")
FBX_PATH = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "camlink_test_geo.fbx")
FBX_PATH = FBX_PATH.replace("\\", "/")

cmds.file(new=True, force=True)
cmds.currentUnit(linear="cm", time="film")
cmds.playbackOptions(min=1, max=120, ast=1, aet=120)
cmds.setAttr("defaultResolution.width", 1920)
cmds.setAttr("defaultResolution.height", 1080)
cmds.setAttr("defaultResolution.deviceAspectRatio", 1920.0 / 1080.0)


def mat(name, rgb):
    m = cmds.shadingNode("lambert", asShader=True, name=name)
    cmds.setAttr(m + ".color", *rgb, type="double3")
    sg = cmds.sets(renderable=True, noSurfaceShader=True, empty=True, name=name + "SG")
    cmds.connectAttr(m + ".outColor", sg + ".surfaceShader")
    return sg


def assign(obj, sg):
    cmds.sets(obj, edit=True, forceElement=sg)


geo = []
ground = cmds.polyPlane(name="ground", w=4000, h=6000, sx=1, sy=1)[0]
cmds.move(0, 0, -1500, ground)
assign(ground, mat("groundMat", (0.35, 0.35, 0.38)))
geo.append(ground)

colors = [(0.9, 0.3, 0.2), (0.2, 0.6, 0.9), (0.95, 0.8, 0.2), (0.3, 0.8, 0.4)]
sgs = [mat("pillarMat%d" % i, c) for i, c in enumerate(colors)]
for i in range(12):
    z = -200 - i * 300
    for side in (-1, 1):
        p = cmds.polyCube(name="pillar_%02d_%s" % (i, "L" if side < 0 else "R"), w=60, h=300, d=60)[0]
        cmds.move(side * 220, 150, z, p)
        assign(p, sgs[(i + (side > 0)) % len(sgs)])
        geo.append(p)

target = cmds.polySphere(name="focusTarget", r=40)[0]
cmds.move(0, 120, -1400, target)
assign(target, mat("targetMat", (1.0, 1.0, 1.0)))
geo.append(target)

# ---- カメラ (アニメーション: ドリー + ズーム + ピント送り) ----
cam, shape = cmds.camera(name="shotCam")
cam = cmds.rename(cam, "shotCam")
shape = cmds.listRelatives(cam, shapes=True)[0]
cmds.setAttr(shape + ".horizontalFilmAperture", 1.417)   # 36mm
cmds.setAttr(shape + ".verticalFilmAperture", 0.945)     # 24mm
cmds.setAttr(shape + ".filmFit", 1)
cmds.setAttr(shape + ".depthOfField", 1)
cmds.setAttr(shape + ".fStop", 1.8)
cmds.setAttr(shape + ".farClipPlane", 100000)

for f, (pos, rot, focal, focus) in {
    1:   ((0, 160, 600),  (-3, 0, 0),  35, 400),
    60:  ((60, 140, 0),   (-2, 8, 0),  50, 1400),
    120: ((-40, 130, -500), (0, -5, 0), 85, 900),
}.items():
    cmds.setKeyframe(cam, at="translateX", t=f, v=pos[0])
    cmds.setKeyframe(cam, at="translateY", t=f, v=pos[1])
    cmds.setKeyframe(cam, at="translateZ", t=f, v=pos[2])
    cmds.setKeyframe(cam, at="rotateX", t=f, v=rot[0])
    cmds.setKeyframe(cam, at="rotateY", t=f, v=rot[1])
    cmds.setKeyframe(cam, at="rotateZ", t=f, v=rot[2])
    cmds.setKeyframe(shape, at="focalLength", t=f, v=focal)
    cmds.setKeyframe(shape, at="focusDistance", t=f, v=focus)

light = cmds.directionalLight(name="sun", intensity=1.2)
cmds.rotate(-50, 30, 0, cmds.listRelatives(light, parent=True)[0])

cmds.currentTime(1)
os.makedirs(os.path.dirname(MA_PATH), exist_ok=True)
cmds.file(rename=MA_PATH)
cmds.file(save=True, type="mayaAscii", force=True)
print("saved", MA_PATH)

# ---- FBX (ジオメトリのみ) ----
cmds.loadPlugin("fbxmaya", quiet=True)
os.makedirs(os.path.dirname(FBX_PATH), exist_ok=True)
cmds.select(geo, replace=True)
mel.eval("FBXResetExport")
mel.eval("FBXExportCameras -v false")
mel.eval("FBXExportLights -v false")
mel.eval('FBXExport -f "%s" -s' % FBX_PATH)
print("exported", FBX_PATH)
