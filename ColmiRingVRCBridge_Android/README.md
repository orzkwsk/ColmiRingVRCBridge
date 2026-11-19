# ColmiRing VRC Bridge for Android

PICO 4 Ultra 上で QRing 互換スマートリングから BLE で心拍数を取得し、VRChat に OSC で送信します。初期設定のまま PICO 内の VRChat に送信できる構成です。PCVR では送信先を PC の LAN IP に変更できます。

## インストール

USB デバッグを有効にして PICO を ADB に接続し、次を実行します。

```powershell
adb devices
adb install -r .\app\build\outputs\apk\debug\app-debug.apk
```

PICO のアプリ一覧から **ColmiRing VRC Bridge** を開き、初回の操作時に「付近のデバイス」を許可します。Android 10 / 11 では、BLE 検索に位置情報の許可と端末側の位置情報の有効化が必要です。通知を許可すると、通知からブリッジを停止できます。

## PICO 単体で使う

1. VRChat のアクションメニューで **Options → OSC → Enabled** を有効にします。アバターの Expression Parameters にある心拍パラメーター名と型を確認します。
2. ブリッジの「パラメーター設定」をアバターに合わせます。初期値は `VRCOSC/Heartrate/Value`、型は `Int` です。OSC の送信先は `127.0.0.1:9000` です。 [VRChat OSC](https://docs.vrchat.com/docs/osc-overview)
3. 「経路テスト」を押すと、リングなしで 75 と 90 BPM を 3 秒ごとに交互に送ります。VRChat に切り替え、値を確認してから「送信停止」を押します。VRChat に届かない場合は、同じ OSC メッセージを PC から PICO の LAN IP の UDP 9000 に送り、受信経路を切り分けます。
4. リングを装着し、「検索」から対象を選んで「接続」を押します。候補に表示されない場合は「すべての BLE デバイスを表示」をオンにして再検索します。スマホの QRing アプリがリングに接続している場合は、スマホ側の接続を切ってから試します。
5. 心拍が表示されたら「送信開始」を押して VRChat に切り替えます。リング接続と OSC 送信は Android のフォアグラウンドサービスで継続します。終了するときはアプリ内か通知の「ブリッジを停止」を押します。 [Android のバックグラウンド BLE](https://developer.android.com/develop/connectivity/bluetooth/ble/background)

## Windows 版から引き継いだ仕様

| 項目 | 初期値・動作 |
| --- | --- |
| OSC 送信先 | `127.0.0.1:9000/UDP` |
| パラメーター名 | `VRCOSC/Heartrate/Value` |
| OSC の型とスケーリング | `Int` / Raw BPM。`Float` では Raw BPM と BPM ÷ 255 を選択可能 |
| OSC 送信間隔 | 1 秒 |
| 固定 BPM | 50、固定出力はオフ |
| 心拍途絶時 | 指定値 `0`。最終値の保持と固定 BPM への切り替えも選択可能 |
| 心拍の有効期間 | 最終受信から 5 秒 |
| 自動再接続 | オン。最後に接続したリングを記憶し、起動時やリンク切断時に再試行。手動切断で一時停止 |
| 心拍・バッテリー | 心拍の 60 秒グラフと最小値・最大値。最後の 24 時間のバッテリー残量と充電履歴をメモリに保持 |

固定出力と途絶時の設定は送信中に変更できます。「送信停止」と「ブリッジを停止」では、途絶時の設定に従う値があれば最後に 1 回送ります。Android がプロセスを強制停止した場合は、計測の停止コマンドと最終 OSC メッセージの送信を保証できません。

BLE UART は現行 Windows 版と同じ連続心拍計測タイプ `0x06` を使用します。TX の通知購読後に `69 06 01`、500 ms 後に `69 06 03` を送信し、心拍を 1 秒間隔、バッテリーを 60 秒間隔で取得します。「リング詳細」で型番・ファームウェア・使用した UUID・通知数・初回 BPM までの時間を確認できます。

## ビルド

JDK 17、Android SDK Platform 36 と Build Tools 35.0.0 を使用します。Android Studio でこのフォルダを開くか、`ANDROID_HOME` または `local.properties` の `sdk.dir` を設定して次を実行します。対応する最低 OS は Android 10（API 29）です。

```powershell
.\gradlew.bat :app:assembleDebug :app:lintDebug
```

デバッグ APK は `app/build/outputs/apk/debug/app-debug.apk` に生成されます。

## License

[MIT](LICENSE)
