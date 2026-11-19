# ColmiRingVRCBridge for Android

対応するリングと動作確認済みの HMD は、[対応機材と動作環境](../README.md#対応機材と動作環境)を参照してください。

## インストールと接続

1. リリースページの **Assets** から Android 版の APK をダウンロードします。 [GitHub Releases](https://github.com/orzkwsk/ColmiRingVRCBridge/releases)
2. APK を Android HMD に保存し、ファイル管理アプリで開きます。画面の案内に従ってインストールします。
3. 端末のアプリ一覧から **ColmiRing VRC Bridge** を開き、初回に求められる権限を許可します。
4. リングを装着し、「検索」で対象を選んで「接続」を押します。

Android 10 / 11 でリングを検索するには、位置情報の権限を許可し、端末の位置情報を有効にしてください。スマホの QRing アプリがリングに接続している場合は、スマホ側の接続を切ってから検索してください。

## VRChat OSC

VRChat 側で OSC を有効にします。アプリで送信先とアバターパラメーターを設定し、「送信開始」を押します。パラメーター名と型は VRChat 側と揃えてください。 [VRChat OSC](https://docs.vrchat.com/docs/osc-overview)

| 設定項目 | 初期値 |
| --- | --- |
| 送信先 | `127.0.0.1:9000` |
| パラメーター名 | `VRCOSC/Heartrate/Value` |
| OSC アドレス | `/avatar/parameters/VRCOSC/Heartrate/Value` |
| 型とスケーリング | `Int` / `Raw BPM` |
| 送信間隔 | 1 秒 |
| 固定 BPM | 50 |
| 心拍が途切れたとき | 指定値に固定 / `0` |

同じ端末で VRChat を使う場合は、初期設定の送信先を使います。PC 上の VRChat に送る場合は、Android 端末と PC を同じネットワークに接続し、送信先に **PC の LAN IP アドレス**を入力します。「Wi-Fiから補完」を使った場合は、PC のアドレスに合わせて最後の数字を入力します。

VRChat に切り替えた後も、リング接続と OSC 送信は続きます。OSC を止めるときは「送信停止」、リングの計測を止めるときは「切断」を押します。

## ローカル設定と履歴

最後に接続したリングと OSC 設定は端末内に保存されます。「自動再接続」がオンの場合、起動時や接続が切れたときに再接続を試みます。バッテリー履歴はアプリの動作中に保持されます。

## License

[MIT](../LICENSE)
