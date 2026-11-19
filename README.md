# Maya Cam Link

Maya のカメラを Unity の Cinemachine + Timeline にリアルタイムで連携するツールです。
操作はすべて Maya 側で行えます。

**動作環境**：Unity 2022.3 以降（推奨 Unity 6）/ URP ・ Maya 2022 以降

## インストール

**Unity**
1. **Window → Package Manager → ＋ → Install package from git URL…** に入力
   ```
   https://github.com/sunnysideup7373/maya-cam-link.git?path=/unity/com.sunny.mayacamlink
   ```
2. **Edit → Preferences → General → Interaction Mode** を **No Throttling** に変更

**Maya**
1. このリポジトリをダウンロード（**Code → Download ZIP**）
2. `maya/maya_cam_link_install.py` をビューポートにドラッグ & ドロップ → シェルフにボタンが追加

## 使い方

1. Unity：**Window → Maya Cam Link** を開く
2. Maya：シェルフのボタン → カメラを選択 → **Unity に接続**
3. Unity：初回だけ **Clip 名** を入力して **作成**（以降は自動で上書き）
4. Unity：**追加先** で Timeline を指定（**選択中** / **新規…**）

あとは Maya でカメラを動かすだけで、Unity の画面と Timeline が追従します。

**Maya パネルの主な機能**
| | |
|---|---|
| ライブ / 時間同期 / キー同期 | 動き・再生位置・キーを Unity に自動反映 |
| 被写界深度 | F値・ピント・ボケ強度を調整（**選択に合わせる** でピント合わせ） |
| キーを打つ | 現在フレームに被写界深度のキー |
| JSON 書き出し | ファイル経由で渡す（Unity の **⇩ JSON** で読み込み） |

## Unity と Maya が別の PC の場合

1. Unity：**設定** → **外部接続を許可** をオン
2. Unity 側 PC のファイアウォールで TCP 7788 を許可（管理者 PowerShell）
   ```powershell
   New-NetFirewallRule -DisplayName "Maya Cam Link" -Direction Inbound -Protocol TCP -LocalPort 7788 -Action Allow -Profile Private
   ```
3. Maya：**設定** → ホストに Unity 側 PC の IP（`ipconfig` で確認）を入力

※ 通信は暗号化されません。信頼できるネットワークで使用してください。

## 注意

- **プロジェクトを共有する時**：ディスクから入れたパッケージは他の PC で見つかりません。Maya Cam Link ウィンドウの警告から **プロジェクトに埋め込む** を押してください。
- Maya のフォルダを移動したら、もう一度ドロップしてください。
- URP では F値 f/1 より強くぼかせません。**ボケ強度** を上げてください。
