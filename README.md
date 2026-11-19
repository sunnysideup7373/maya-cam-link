# Maya Cam Link

Maya のカメラを Unity の Cinemachine + Timeline にリアルタイムで連携するツールです。

- Maya のカメラ (位置・回転・焦点距離・フィルム・被写界深度) をそのまま Unity に反映
- キーは AnimationClip として Timeline に入り、カーブで Maya と同じ値を確認できる
- 操作はすべて Maya 側で完結

## 動作環境

- Unity 2022.3 以降（推奨 Unity 6）/ URP
- Maya 2022 以降

## インストール

### Unity
1. **Window → Package Manager → ＋ → Install package from git URL…** に次を入力
   ```
   https://github.com/sunnysideup7373/maya-cam-link.git?path=/unity/com.sunny.mayacamlink
   ```
   Cinemachine と Timeline は自動で入ります。
2. **Edit → Preferences → General → Interaction Mode** を **No Throttling** に変更
3. **Window → Maya Cam Link** を開く

更新は **Window → Package Manager → Maya Cam Link → Update**。

#### プロジェクトを Git などで共有する場合
**Install package from disk** で入れると `Packages/manifest.json` に自分の PC のパス (`file:...`) が書かれ、
他の人がプロジェクトを開くとパッケージが見つからずエラーになります。
その場合は Maya Cam Link ウィンドウに警告が出るので、どちらかを押してください。

| ボタン | 内容 | 相手に必要なもの |
|---|---|---|
| **プロジェクトに埋め込む** | `Packages/com.sunny.mayacamlink` にコピーし、プロジェクトと一緒にコミット | なし |
| **GitHub から参照** | 上の git URL 参照に切り替え (`packages-lock.json` で全員同じバージョンに固定) | Git |

### Maya
1. このリポジトリをダウンロード（**Code → Download ZIP** を展開、または `git clone`）
2. `maya/maya_cam_link_install.py` を Maya のビューポートに**ドラッグ & ドロップ**
3. 開いているシェルフに **Maya Cam Link** ボタンが追加されます

※ フォルダを移動した場合は、もう一度ドロップしてください。

## 使い方

### 1. つなぐ
1. Unity：**Window → Maya Cam Link** を開く（● が灰色 = 待機中）
2. Maya：シェルフの **Maya Cam Link** ボタン → **カメラ** を選択 → **Unity に接続**
3. Unity の ● が緑になり、Maya のカメラが Unity に表示されます

### 2. 初回だけ: Clip を作成
1. Unity の Maya Cam Link ウィンドウに「◆ カメラ名 … 未作成」が出ます
2. **Clip 名** を入力して **作成**
3. 以降は Maya でキーを変えると、同じ Clip に自動で上書きされます

### 3. Timeline に入れる
- **追加先** の **選択中**：Hierarchy で選んだ Timeline に「Maya: カメラ名」トラックを追加（他のトラックはそのまま）
- **追加先** の **新規…**：新しい Timeline を作成
- Timeline 上でクリップを移動しても、Maya の再生・スクラブに合わせて再生ヘッドが同期します

### 4. Maya で作業する
| Maya のパネル | 内容 |
|---|---|
| **ライブ** | カメラの動きをリアルタイムに Unity へ反映 |
| **時間同期** | Maya の時間スライダーに Unity の Timeline を合わせる |
| **キー同期** | キーを編集したら自動で Unity の Clip を更新 |
| **キーを今すぐ送信** | 全フレームを手動で送信 |
| **被写界深度** | ON にして **F値** / **ピント** / **ボケ強度** を調整 |
| **選択に合わせる** | 選択中のオブジェクトにピントを合わせる |
| **キーを打つ** | 現在フレームに被写界深度のキーを打つ |
| **JSON 書き出し** | ファイルに書き出す（Unity の **⇩ JSON** で読み込み。接続できない時用） |

### 5. Unity のウィンドウ
| ボタン | 内容 |
|---|---|
| **▤ Timeline** | 追加先の Timeline を開く |
| **↩ 解除** | Maya のライブ表示をやめて、Timeline の再生に戻す |
| **◎ セットアップ** | Main Camera に CinemachineBrain・ポストプロセスを設定 |
| **表示** | Clip の保存先フォルダを開く |

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

- Unity 起動後、一度 Unity のウィンドウをクリックしてから Maya で接続してください。
- Timeline の **追加先** が未指定の間は、AnimationClip だけ作成されます。
- Unity の URP では F値 の下限が f/1 のため、それより強くぼかしたい時は **ボケ強度** を上げてください。
