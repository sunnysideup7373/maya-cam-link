# Maya Cam Link

Maya のカメラを Unity の Cinemachine + Timeline にリアルタイムで連携するツールです。

## 動作環境

- Unity 2022.3 以降（推奨 Unity 6）/ URP
- Maya 2022 以降

## インストール

### Unity
1. **Window → Package Manager → ＋ → Install package from git URL…** に次を入力
   ```
   https://github.com/sunnysideup7373/maya-cam-link.git?path=/unity/com.sunny.mayacamlink
   ```
2. **Edit → Preferences → General → Interaction Mode** を **No Throttling** に変更
3. **Window → Maya Cam Link** を開く

#### プロジェクトを Git などで共有する場合
**Install package from disk** で入れると `Packages/manifest.json` に自分の PC のパス (`file:...`) が書かれ、
他の人がプロジェクトを開くとパッケージが見つからずエラーになります。
その場合は Maya Cam Link ウィンドウに警告が出るので、どちらかを押してください。

| ボタン | 内容 | 相手に必要なもの |
|---|---|---|
| **プロジェクトに埋め込む** | `Packages/com.sunny.mayacamlink` にコピーし、プロジェクトと一緒にコミット | なし |
| **GitHub から参照** | 上の git URL 参照に切り替え (`packages-lock.json` で全員同じバージョンに固定) | Git |

※ どちらの場合も Cinemachine と Timeline は Package Manager が自動で入れます。

### Maya
`maya/maya_cam_link_install.py` をビューポートに**ドラッグ & ドロップ**
→ 開いているシェルフにボタンが追加されます。

## 接続先 (IP) の設定

**Unity と Maya が同じ PC** → 設定不要です（`127.0.0.1:7788`）。

**Unity と Maya が別の PC**
1. Unity 側 PC でコマンドプロンプトから `ipconfig` を実行し、IPv4 アドレスを確認（例: `192.168.1.23`）
2. Unity：**Window → Maya Cam Link** → ■ で停止 → **設定** → **外部接続を許可** にチェック → ▶ で再開
3. Unity 側 PC のファイアウォールで TCP 7788 を許可（管理者 PowerShell）
   ```powershell
   New-NetFirewallRule -DisplayName "Maya Cam Link" -Direction Inbound -Protocol TCP -LocalPort 7788 -Action Allow -Profile Private
   ```
4. Maya：パネルの **設定** → ホストに Unity 側 PC の IP を入力 → **Unity に接続**

つながらない場合は、Maya 側 PC で次を実行して `TcpTestSucceeded : True` になるか確認してください。
```powershell
Test-NetConnection 192.168.1.23 -Port 7788
```
※ 通信は暗号化されません。社内・自宅など信頼できるネットワークで使用してください。

## 注意

- このフォルダを移動した場合は、Maya にもう一度ドロップしてください。
- Unity 起動後、一度 Unity のウィンドウをクリックしてから Maya で接続してください。
- カメラを初めて受信した時は、Unity の Maya Cam Link ウィンドウで Clip 名を決めて **作成** を押してください。以降は Maya の変更が同じ Clip に自動で反映されます。
- Timeline に入れる場合は、Unity の Maya Cam Link ウィンドウで **追加先** に Timeline を指定してください（未指定の間は AnimationClip だけ作成されます）。
