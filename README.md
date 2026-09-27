# Echo Show Remote

WebRTC peerjsとexeの連携はめちゃムズイ。AIの協力により何とか実現
-https://polandballhub-operator.github.io/WebRTCRemoteDesktop/
clientをwindowsにいんすとーるして

## 構成

- `web/` — Echo Showのブラウザで開くHTML / CSS / JavaScript
- `windows-client/` — .NET 8 Windows Forms PCクライアント。画面取得、PeerJS、WebRTC、OS入力、ファイル送信を担当
- WebRTCの映像・操作データはPCとブラウザの間でP2P送信します。PeerJS Cloudは接続シグナリングに使い、映像データを中継しません。

## 起動手順

### 1. Windows PCクライアント

Windows 10/11のPCに以下を用意します。

- .NET 8 SDK
- Microsoft Edge WebView2 Runtime
- インターネット接続

`windows-client/EchoRemote.WinForms.csproj` をVisual Studioで開くか、次を実行します。

```powershell
cd windows-client
dotnet restore
dotnet build -c Release
```

初回起動時にWindowsの管理者権限確認が表示されます。画面サイズにかかわらずPCクライアントは明示的なUAC同意後に管理者権限で動作します。アプリ画面で**接続パスワード**を設定し、**PeerJSを起動**します。画面に表示されるPeerJSコードを控えてください。コードを空欄で起動した場合、PeerJSがランダムコードを割り当て、画面に表示します。

設定はユーザーの `%APPDATA%\EchoRemote\settings.json` に保存します。パスワードもこのローカル設定ファイルに保存されるため、WindowsユーザーアカウントとPCを保護してください。

### 2. ブラウザUI

`web/` をHTTPSで配信し、Echo ShowのSilkブラウザからそのHTTPS URLを開きます。開発PCからは次のように静的配信できます。

```bash
cd web
python3 -m http.server 8080 --bind 0.0.0.0
```

通常のWebRTC利用ではHTTPSが必要です（localhostでの開発を除く）。本番ではご自身のHTTPS対応静的ホスティングへ `web/` の3ファイルを置いてください。外部依存はPeerJSクライアント（jsDelivr CDN）のみです。

### 3. Echo Showから接続

1. `Add My PC` でPC名、PCクライアントに表示されたPeerJSコード、同じ接続パスワードを登録します。
2. `Connect PC` から登録PCを選択します。接続・認証後、PCの画面が画面いっぱいに表示されます。
3. 画面をタッチ／ドラッグするとマウス入力を送ります。右下の **A** から文字を入力して送信できます。
4. 下向き矢印またはファイルアイコンを押すと、PC側でファイル選択画面が開きます。選んだファイルはブラウザ端末内のIndexedDBに保存され、`Get file from Windows` から閲覧・削除できます。
5. `Settings` の「配信解像度」で横幅の上限を640〜3840 pxから選べます。既定値は1280 pxで、変更は接続中のPCへすぐ反映されます。
6. 全画面ボタンはブラウザの全画面表示を要求します。ブラウザ設定やEcho Showのバージョンにより動作しない場合は、Silkの全画面表示機能を利用してください。

## Windowsの画面モードについて

設定にある「複製／拡張」は**Windowsの実在するディスプレイ**を対象にします。

- **複製モード** — Windowsの標準ディスプレイ切替を要求し、メイン画面を配信
- **拡張モード** — Windowsの拡張表示を要求し、接続されている第2画面を配信

**Echo Show自体をWindowsの仮想モニターにはしません。** 仮想ディスプレイドライバーやWDK依存を追加しない、.NET 8 WinForms構成です。拡張モードで第2画面が見つからなければ、メイン画面を配信したまま案内を表示します。Echo Showを本当に仮想ディスプレイとして追加するには、別途Windows IDDドライバーを開発・署名・導入する工程が必要です。

## ファイル形式と保存先

TXT / MD / CSV / JSON / XMLはテキストプレビュー、MP4 / WebMは動画、MP3 / WAV / M4A / OGGは音声、PNG / JPEG / GIF / WebP / BMPは画像、PDFはブラウザ内蔵ビューアーでの表示を試みます。ブラウザが未対応の形式はダウンロードしてください。1ファイルの転送上限は512 MiBです。IndexedDBの容量上限はブラウザ・端末ごとに異なります。大きな動画などは空き容量にご注意ください。

## セキュリティ・運用上の注意

- **初回接続前に十分長いランダムな接続パスワードを設定**し、PeerJSコードとともに信頼できるEcho Showだけに登録します。短い数字だけのPINは避けてください。
- 接続時はPeerJS経由で接続先を探し、パスワード照合後に映像・OS入力を許可します。WebRTCの通信はDTLSで保護されますが、PeerJS Cloudの可用性やネットワーク機器のNAT設定によりP2P接続できない環境があります。TURN中継は同梱していません。
- Echo ShowのPC登録パスワードはブラウザのlocalStorage、受信ファイルはIndexedDBに保存します。共有・公共端末では利用せず、不要になったPC登録とファイルを削除してください。
- PCクライアントは管理者権限で入力を送ります。本人が管理するPCだけに使用し、接続パスワードを共有しないでください。
- Windowsのロック画面、UACセキュアデスクトップ、保護された動画などはキャプチャ・操作できません。管理者権限はこれらの保護を回避するものではありません。
- 画面配信は約5fps、既定の最大幅1280pxです。解像度を最大3840pxまで上げられますが、元のWindows画面より高解像度にはならず、高く設定するほど通信量とPC負荷が増えます。遅延・画質はPCとネットワーク状況に依存します。
- PeerJSコードは公開サービス上の接続先IDです。推測されやすい固定IDより、PC側のランダムIDを推奨します。

## 第三者コンポーネント

- [PeerJS](https://github.com/peers/peerjs) — MIT License。ブラウザとWebView2内からCDNで読み込んで使用します。PeerJSの配布条件に従い、再配布物を作る際はライセンスと著作権表示を同梱してください。
- [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) — MicrosoftのNuGetパッケージ。Microsoftの該当利用条件に従います。

このリポジトリにReact、React Native、Tailwind CSS、TypeScript、JSXは含まれていません。
