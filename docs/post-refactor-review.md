# Post-refactor review — 2026-10-09

## Review result

対象は `feature/telemetry-liveness-hardening`。開始時のHEADは `ecce742937d8ef28b182a21ca14e80e957fb4be4`、未コミット変更なし。元のPoC `80758c21cab57a044c71e886d61221abad88c706` と比較して、35ファイル、3,042行追加・792行削除の変更を監査した。リポジトリ全体のWPF、通信、保存、OSC、テスト、README、設計資料も確認した。AGENTS.md / CONTRIBUTINGは見つからなかった。現在のチェックアウトに `.github` はないが、取得した `origin/dev` のCI・リリースworkflowを確認した。

最新取得で、元のリファクタリングHEADはすでに `origin/dev` の祖先と判明した。`origin/dev` はリリース準備の13コミット分先行している。元のソースとの差は主にREADME、ライセンス、リリース資料、CI、バージョン設定であり、今回の修正はその履歴を書き換えていない。

残存件数:

- **Blocker: 1（既存問題）** — OSC開始直後の停止でNullReferenceException。
- **Major: 1（既存問題・コード上確認）** — 手動接続とWindow終了処理の寿命管理が競合する。
- **Minor: 2** — 既存xUnit2020警告、promotion gate資料と最新devのリリース状態との不整合。
- **Note:** 既存のcode-behind中心の構成、process-wide保存キューと無制限channel、実機依存の測定セッション回復。

### 未修正Blocker: OSC開始・即停止

`src/ColmiRingVRCBridge/Services/OscOutputService.cs` のStartは、Task.Run内で可変フィールド `_cts.Token` を読む。StopAsyncはその実行前に `_cts = null` にできる。Task.RunのdelegateでNullReferenceExceptionが発生し、StopAsyncはOperationCanceledExceptionしか扱わないため例外が呼び出し元へ伝播する。WPFのasync voidイベントから呼び出すとクラッシュにつながる。

分離した診断プロジェクトで再現済み（1/1 FAIL）。同じコードが元のPoCにも存在し、今回追加したValidate処理が原因ではない。通常テストを削除・緩和して隠してはいない。

再現用ローカル成果物（Git対象外）: `artifacts/review/ExistingIssueProbe.csproj` / `ExistingIssueProbe.cs`。

```powershell
dotnet test .\artifacts\review\ExistingIssueProbe.csproj -c Release
```

必要な次作業は、OSCループのCancellationTokenをStart時に固定し、開始・停止の回帰テストを正規テスト群へ追加すること。既存問題は原則同時修正しないという今回の指示に従い、ここでは修正していない。

### 未修正Major: 手動接続と終了の競合

`MainWindow.xaml.cs` の `ConnectButton_Click` はasync voidで、手動ConnectAsyncのTask / CancellationTokenを終了処理へ渡していない。`Window_Closing` が待つのは自動再接続ループであり、手動接続の完了は待たずに同じBLE serviceをDisposeする。BLE device open / GATT discoveryが継続中なら、接続処理と切断・破棄が同じ可変フィールドを扱う。手動接続の継続処理にも `_closing` による中断判定がない。

この寿命管理は元のPoCにも存在する既存問題。コード上確認済みだが、実機でのclose-during-connectは未検証。次作業では手動操作のTask / CTSを追跡して、終了時にcancel / awaitした後でserviceを破棄する必要がある。今回の大規模なMVVM再設計には含めない。

## 修正内容

修正コミット: `554f1be56edd3be586d4e267419eaf2a12047e02` (`fix: address post-refactor review findings`)。

| ファイル | 原因分類と修正理由 |
| --- | --- |
| `Services/R06Session.cs` | 今回の変更起因。Task.RunでCTSフィールドを読む競合を修正。2つのpolling taskを両方awaitし、faultがあってもCTS、イベント購読、transportを解放。 |
| `Services/ColmiRingBleService.cs` | 今回の変更起因。device open直後のcancelをcleanup範囲へ移動。session破棄失敗でもGATT/deviceを解放。 |
| `Services/BatteryHistoryPersistenceQueue.cs` | 今回の変更起因。書き込み完了を待つFlush barrierを追加。診断保存失敗はDebug出力へ記録し、後続の書き込みを継続。 |
| `Services/BatteryHistoryStore.cs` | 今回の変更起因。snapshot作成とenqueueを同一lock内へ置き、並行callbackで古いsnapshotが後から保存される順序逆転を防止。 |
| `Services/DebugTelemetryLogger.cs` | 今回の変更起因。開始直後のCTS参照競合を修正。破棄後の再開を拒否し、writer故障時にchannelを閉じて無制限の蓄積を防止。テスト用出力先を指定可能にした。 |
| `MainWindow.DebugTelemetry.cs` | 今回の変更起因。Closedのasync voidからの未await排出を、Closingがawaitする終了処理へ移動。 |
| `MainWindow.xaml.cs` | 今回の変更により顕在化。終了処理の途中で例外が起きても残りの資源を解放し、BLE停止後に保存とDebugログを排出。排出中の再Closeもキャンセル。 |
| `Models/OscOutputOptions.cs` | 今回の変更起因。元のpublic positional record API（旧名の名前付き引数、Deconstruct、init / with）を復元。constructorでInt + Normalize255を拒否する意図を維持。 |
| `Services/OscOutputService.cs` | public record互換性復元に伴う検証。with / object initializerで作られた不正設定をStartで拒否。既存のCTS競合は別問題として残す。 |
| `tests/ColmiRingVRCBridge.Tests/*` | session即時破棄・fault後cleanup・handshake cancel、保存queueの順序・CSV形式・失敗後継続、record API・JSON・設定形式、Debugログ排出の最小回帰テストを追加。 |

修正前に、session即時破棄のNullReferenceExceptionと、polling subscriber fault後のtransport解放漏れを追加テストで再現した。修正後は成功した。新機能、通信コマンド、保存形式、stale閾値、1秒の再接続方針は変更していない。

## Architecture

- **責務分離:** R06Protocolはpacket意味解釈、R06Sessionはmeasurement/polling、GattColmiTransportはWindows GATT、ColmiRingBleServiceはscan/discovery/device寿命、各storeは保存、OscSenderはUDP encoding。分離は実際の呼び出し境界にも成立する。
- **依存方向:** Modelsとprotocol/sessionはWPF型やDispatcherへ依存しない。Windows固有I/Oはservice/transport内。CoreからViewへの逆依存、循環依存は見つからなかった。
- **所有権:** serviceがsessionとWindows device/serviceを所有し、sessionがtransportを破棄する。session異常時もcleanupが進むよう修正。保存queueはprocess-wideで、ClosingからFlushする。手動接続と終了の競合は上記Majorとして残る。
- **非同期処理:** BLE callbackからUIへはBeginInvoke。確立済みsessionのCTSは接続開始tokenと独立。新しいsession/loggerの可変CTS参照競合は修正。OSCの同型競合は既存Blocker。WinRTの強制timeoutは実装せずHIL未確認として残す。
- **エラー処理:** transient HR write failureでpollingが永久停止しない。保存失敗は測定へ波及させない既存方針を維持。fault後cleanupとキャンセル時STOPをテストした。未確認の成功やfallbackによる正常化は追加していない。
- **WPF / MVVM:** 完全なMVVMではない。MainWindowのpartial分割は同一クラスであり、接続調整・表示状態・設定組み立てがcode-behindに残る。これは既存のPoC構成。今回ViewModelへ通信I/Oを移す変更やModelからUIへの逆依存はない。グラフ・tooltip・UI dispatchをViewに置くことは妥当。全面的なMVVM化は別作業。
- **Unity Editor/Runtime:** 該当なし。
- **重複・不要コード:** 旧telemetry handler差し替えとtooltip workaroundは削除済み。互換性に必要なpublic record APIは復元。明確な不要shimや旧実装の並存は見つからなかった。
- **性能:** disk writeはBLE callback外。snapshot enqueueのlockは短いメモリ操作のみ。グラフは1秒timer / tooltip表示時であり、今回の修正で明確な性能劣化は見つからない。ベンチマーク未実施。無制限queueでI/Oが長期間停止する場合のメモリ上限はNote。

## Verification

Windows上で.NET 8.0.424を明示選択した最終検証。通常solutionのテストと、既存問題の分離probeを区別する。

| 項目 | 結果 / 分類 |
| --- | --- |
| Release build | PASS、0 errors、既存xUnit2020 warning 1件（自動検証済み） |
| Debug build | PASS、同じ既存warning 1件（dotnet test内のbuild、自動検証済み） |
| Release tests | **28/28 PASS**。既存19件 + 追加9件（自動検証済み） |
| Debug tests | **29/29 PASS**。上記 + Debug loggerの追加1件（自動検証済み） |
| Session failure/cancel paths | 即時Dispose 100回、subscriber fault後の解放、handshake cancel後のSTOP・unsubscribe（自動検証済み） |
| Persistence regression | FIFO snapshot上書き、Flush後のCSV、temporary file移動、失敗後の書き込み継続（自動検証済み） |
| Public API / serialization | 旧名の名前付き引数・分解代入・with双方の代入順、JSON round-trip、旧connection.jsonのfield/default（自動検証済み） |
| Debug logger shutdown | 20回の開始・即破棄、各10イベント排出、停止record、再開拒否（自動検証済み） |
| Existing OSC issue probe | **1/1 FAIL: NullReferenceException再現**（自動検証済み、未修正既存Blocker） |
| WinRT device-open cancel / session-fault cleanup | cleanup範囲を追跡（コード上確認済み、実機未確認） |
| Packet / OSC wire / defaults | 元のpacket command・payload・checksum、OSC encoding、endpoint/intervalと比較（コード上確認済み）。実UDP/VRChat疎通は未実施。 |
| GUI smoke | **NOT TESTED**。実際の操作・描画・hover・急速ON/OFF・終了操作は人間による確認が必要。 |
| R06 / BLE HIL | **NOT TESTED**。今回実機確認済みと呼べる項目はない。 |
| git diff --check | PASS |
| Latest dev conflict simulation | `git merge-tree --write-tree origin/dev HEAD` exit 0、競合なし。実merge・checkout・pushなし。 |

最終build/testを.NET 8で実行するための `artifacts/review/global.json` はローカル検証用のみで、製品のSDK設定は変更していない。CIやGitHub Release workflowのリモート実行、self-contained publishは今回実行していない。

## Remaining risks

1. 未修正OSC Blockerと、手動接続終了のMajorを別作業で解消する必要がある。
2. GUI、R06 remove/re-wear、CCCD/GATT再初期化、BLE reopen、Windowsごとのtimeout/cancel挙動、接続中closeは未確認。
3. 5秒HR / 3分batteryのstale閾値は今回変更していない。低HR候補2..4や0x9E dialectは観測目的のままで、推測によるparser/poll変更はしていない。
4. 最新devのREADMEはstale時「送信停止」を0.1.x preview契約としている。一方、旧action-listのpromotion gateはその決定やbuild/testが未完了表記のまま。運用資料の整合はMinorとして残す。
5. code-behind中心の状態管理、保存例外のユーザー通知、bounded/coalescing queueは将来の設計課題。今回は再設計しない。

## Merge decision

**MERGE BLOCKED**。

通常solutionの自動検証は成功したが、独立したfailure-path probeで未修正の既存クラッシュを確認した。未修正Majorもあるため、ユーザー指定の「Blocker 0 / 未修正Major 0」を満たさない。HIL未確認だけを理由にブロックしているわけではない。今回の修正起因で新たに残ったBlocker / Majorはない。

リファクタリング本体は最新devに既に取り込まれていた。追加修正後のfeatureとdevは分岐し、`dev...feature` は13対1（この報告commit追加前）。現状のfeatureを直接devへfast-forwardすることはできない。競合simulationは成功した。問題解消後は、履歴を書き換えない通常mergeでdevのリリース準備変更と修正を統合するのが適切。別案としてfeatureを最新devへ通常mergeして再検証した後ならdevをfast-forwardできる。rebaseやforce pushは不要。

今回dev/mainへのmergeおよびpushは実施していない。

## Git state

- branch: `feature/telemetry-liveness-hardening`
- code fix commit: `554f1be56edd3be586d4e267419eaf2a12047e02`
- local `dev`: 未作成（変更なし）
- `origin/dev`: `49fe23be84c8700d21d626a0bd9044cdf1722814`
- 元のfeature remote: `ecce742937d8ef28b182a21ca14e80e957fb4be4`
- 本報告を追加commitとして保存し、最終HEADとworking tree状態をチャットの最終報告に記載する。
- `artifacts/review` は既存gitignoreに従うローカル再現用成果物。未追跡の製品変更は残さない。
