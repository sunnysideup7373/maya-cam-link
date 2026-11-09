# シェルフアイコン生成 (mayapy で実行):  mayapy make_icon.py
# 512px で描いて 32 / 48 / 64 px に縮小 (Maya の高 DPI 用 _150 / _200)
import os, sys
from PySide6 import QtCore, QtGui

app = QtGui.QGuiApplication(sys.argv)
here = os.path.dirname(os.path.abspath(__file__))
icon_dir = os.path.join(here, "icons")
os.makedirs(icon_dir, exist_ok=True)

S = 512.0
img = QtGui.QImage(int(S), int(S), QtGui.QImage.Format_ARGB32_Premultiplied)
img.fill(QtCore.Qt.transparent)
p = QtGui.QPainter(img)
p.setRenderHint(QtGui.QPainter.Antialiasing)
R = QtCore.QRectF
P = QtCore.QPointF
C = QtGui.QColor

# 背景: 角丸 + 青のグラデーション
g = QtGui.QLinearGradient(0, 0, 0, S)
g.setColorAt(0.0, C("#3b8fe0"))
g.setColorAt(1.0, C("#1d4f96"))
p.setPen(QtCore.Qt.NoPen)
p.setBrush(g)
p.drawRoundedRect(R(16, 16, S - 32, S - 32), 96, 96)

white = C("#ffffff")
orange = C("#ff9f2e")

# リール (2 つ)
p.setBrush(white)
p.drawEllipse(P(150, 150), 58, 58)
p.drawEllipse(P(286, 150), 58, 58)
# 本体
p.drawRoundedRect(R(84, 200, 270, 180), 36, 36)
# ファインダー (台形)
p.drawPolygon(QtGui.QPolygonF([P(354, 262), P(448, 214), P(448, 366), P(354, 318)]))

# レンズ (オレンジのボケ玉)
p.setBrush(orange)
p.drawEllipse(P(219, 290), 62, 62)
p.setBrush(C("#ffd08a"))
p.drawEllipse(P(200, 270), 20, 20)

# ライブリンクの波 (右下)
pen = QtGui.QPen(orange, 30, QtCore.Qt.SolidLine, QtCore.Qt.RoundCap)
p.setPen(pen)
p.setBrush(QtCore.Qt.NoBrush)
for r in (52, 104):
    p.drawArc(R(400 - r, 450 - r, 2 * r, 2 * r), 100 * 16, 70 * 16)
p.setPen(QtCore.Qt.NoPen)
p.setBrush(orange)
p.drawEllipse(P(400, 450), 22, 22)
p.end()

for size, suffix in ((32, ""), (48, "_150"), (64, "_200")):
    out = os.path.join(icon_dir, "maya_cam_link%s.png" % suffix)
    img.scaled(size, size, QtCore.Qt.IgnoreAspectRatio, QtCore.Qt.SmoothTransformation).save(out)
    print("icon", out)
img.scaled(256, 256, QtCore.Qt.IgnoreAspectRatio, QtCore.Qt.SmoothTransformation).save(os.path.join(icon_dir, "preview_256.png"))
