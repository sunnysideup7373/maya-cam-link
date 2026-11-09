# Maya 起動後: Maya Cam Link を開き、shotCam を選んで Unity に自動接続 (テスト用)
import sys
import maya.cmds as cmds
import maya.utils

sys.path.insert(0, r"C:\Users\sunny\Claude\maya_unity_cam\maya")


def _go():
    import maya_cam_link
    if cmds.objExists("shotCam"):
        cmds.optionVar(sv=("mayaCamLink_camera", cmds.ls("shotCam", long=True)[0]))
        try:
            panel = cmds.getPanel(withFocus=True)
            if cmds.getPanel(typeOf=panel) != "modelPanel":
                panel = cmds.getPanel(visiblePanels=True)
                panel = [p for p in panel if cmds.getPanel(typeOf=p) == "modelPanel"][0]
            cmds.lookThru(panel, "shotCam")
        except Exception as e:
            print("lookThru failed:", e)
    w = maya_cam_link.show()
    if not maya_cam_link.controller().enabled:
        w.toggle_connect()


maya.utils.executeDeferred(_go)
