# -*- coding: utf-8 -*-
"""
Maya Cam Link - シェルフボタンの追加

使い方: このファイルを Maya のビューポートにドラッグ & ドロップ
        → 今開いているシェルフに Maya Cam Link のボタンを追加

  クリック                         : パネルを開く
  ダブルクリック / 右クリック Reload : スクリプトを再読み込みして開く
"""
import os
import maya.cmds as cmds
import maya.mel as mel

TAG = "MayaCamLink"   # ボタン識別用 (docTag)

# Maya はドロップした .py をモジュールとして import するので __file__ が使える
HERE = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")


def _commands(script_dir):
    head = ("import sys\n"
            "_p = r'%s'\n"
            "if _p not in sys.path: sys.path.insert(0, _p)\n" % script_dir)
    show = head + "import maya_cam_link\nmaya_cam_link.show()\n"
    reload_ = (head + "import importlib, maya_cam_link\n"
               "maya_cam_link.stop()\n"
               "maya_cam_link = importlib.reload(maya_cam_link)\n"
               "maya_cam_link.show()\n")
    return show, reload_


def install(script_dir=HERE):
    script_dir = script_dir.replace("\\", "/")
    icon = script_dir + "/icons/maya_cam_link.png"
    if not os.path.exists(icon) or not os.path.exists(script_dir + "/maya_cam_link.py"):
        cmds.warning("[MayaCamLink] maya_cam_link.py / icons not found in: " + script_dir)
        return False
    show, reload_ = _commands(script_dir)
    top = mel.eval("$_t = $gShelfTopLevel")
    shelf = cmds.shelfTabLayout(top, q=True, selectTab=True)
    if not shelf:
        cmds.warning("[MayaCamLink] shelf not found")
        return False

    # シェルフファイルの文字化けを避けるため、保存される文字列は ASCII のみ
    btn = cmds.shelfButton(
        parent=top + "|" + shelf,
        visible=True, manage=True,
        label="Maya Cam Link",
        docTag=TAG,
        annotation="Maya Cam Link - Unity camera link (double-click: reload)",
        image=icon, image1=icon,
        style="iconOnly",
        command=show, sourceType="python",
        doubleClickCommand=reload_,
        menuItem=[("Reload", reload_)],
        menuItemPython=[0],
    )
    # 作成直後はまだ表示されておらず、すぐ保存すると "-visible 0" で書かれて
    # 次回起動時にボタンが見えなくなる。表示されてから保存する。
    def _save():
        try:
            cmds.shelfButton(btn, e=True, visible=True)
        except RuntimeError:
            pass
        mel.eval("saveAllShelves $gShelfTopLevel")
    cmds.evalDeferred(_save, lowestPriority=True)

    label = shelf
    try:
        if not mel.eval("exists shelfLabel_melToUI"):
            mel.eval('source "shelfLabel.mel"')
        label = mel.eval('shelfLabel_melToUI "%s"' % shelf)
    except RuntimeError:
        pass
    msg = u"シェルフ「%s」に Maya Cam Link を追加しました" % label
    print(u"[MayaCamLink] " + msg)
    cmds.inViewMessage(amg=msg, pos="topCenter", fade=True, fadeStayTime=2500)
    return True


def onMayaDroppedPythonFile(obj):
    # obj はマウス下のオブジェクト名 (パスではない) なので使わない
    install(HERE)
