<!-- version: 0.6.0 -->
Sheet Board は Utsushi（うつし）に名前を変えました。機能は 0.5.0 と同じです。

- パッケージ ID は `com.tentee.vrc-utsushi`、リポジトリは `TenteEEEE/vrchat-world-utsushi` です（旧URLは転送されます）。
- VCC で Utsushi を追加すると旧 Sheet Board は自動で外れ、シーンの掲示板・データ元はそのまま動きます（スクリプトの GUID が同じです）。
- Unity のメニューは `Tools/TenteEEEE/Utsushi/…`、prefab は `Utsushi Display` などに変わります。

### 更新時の注意

- シートのスクリプトは貼り直さなくても動きます。貼り直す場合は `Utsushi.gs` を使ってください。貼り直すとメニュー名が「Utsushi」に、設定タブ名が「Utsushi設定」に自動で変わります。掲示板URL（`…/sheetboard.json`）は変わりません。
- unitypackage で入れていた場合は `Assets/SheetBoard` を消してから入れてください。

### インストール

VCC の一覧 [TenteEEEE VPM Repository](https://tenteeeee.github.io/vpm-repos/index.json) から **Utsushi 0.6.0** を追加してください。手動で導入する場合は、添付の `.unitypackage` を使えます。

詳しい手順は [日本語 README](https://github.com/TenteEEEE/vrchat-world-utsushi/blob/main/README.ja.md) を参照してください。
