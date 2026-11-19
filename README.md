# ColmiRingVRCBridge

ColmiRingVRCBridge は、COLMI などの QRing 互換スマートリングから Bluetooth Low Energy（BLE）で心拍数を取得し、OSC で VRChat のアバターパラメーターに送信します。

## 対応機材と動作環境

### 動作確認済みの機材

- リング
  - COLMI R06
- Android HMD
  - PICO 4 Ultra

### インストール先

| 端末 | 動作要件 | 導入手順 |
| --- | --- | --- |
| Windows PC | Windows x64、BLE 対応 | [Windows 版](windows/README.md) |
| Android HMD | Android 10 以降、BLE 対応 | [Android 版](android/README.md) |

## 共通の OSC 設定

初期設定の送信先は `127.0.0.1:9000/UDP`、OSC アドレスは `/avatar/parameters/VRCOSC/Heartrate/Value`、OSC の型は `Int` です。VRChat で OSC を有効にし、アバターパラメーターの型をアプリの設定と揃えてください。

HMD 内の VRChat に送る場合は、初期設定の送信先を使います。HMD から PC 上の VRChat に送る場合は、同じネットワークに接続した PC の LAN IP アドレスを送信先に指定します。 [Android 版の OSC 設定](android/README.md#vrchat-osc)

## ライセンス

[MIT](LICENSE)
