掲示板の列幅と文字サイズを表の領域に合わせて自動調整するようにしました。

- 見出しやセルの文字が列からはみ出したり、最下行が欠けたりしないよう、列幅と文字サイズを自動で決めます。
- 列幅は文字が収まる最低幅を確保し、余りをシートの列幅比で配ります。設定タブ F 列で列幅の比を手で指定している場合は、その比を保ったまま必要に応じて文字を縮小します。
- Display に **列幅を自動調整**（`autoFitColumns`）を追加しました。既定は ON です。

### 更新時の注意

既定で自動調整が有効になるため、更新後は既存の掲示板のレイアウトが変わります。従来の列幅のままにしたい場合は、各 Display の **列幅を自動調整** を OFF にしてください。Apps Script（`SheetBoard.gs`）の更新は不要です。

### インストール

VCC の一覧 `https://tenteeeee.github.io/vpm-repos/index.json` から **Sheet Board 0.2.0** を追加・更新してください。手動導入には添付の `.unitypackage` を使用できます。

Unity 2022.3 / VRChat Worlds SDK 3.8 以降。詳しい手順は [日本語 README](https://github.com/TenteEEEE/vrchat-world-sheet-board/blob/main/README.ja.md) を参照してください。

検証: Apps Script のモック回帰テスト60件、Unity 2022.3.22f1 / Worlds SDK 3.10.5 でのUdonSharpコンパイル、prefab配線・権限設定・更新状態表示・サンプル描画と全グリフのはみ出し検査、配布アーカイブの内容とチェックサム。実Google/GitHubアカウントの連携とVRChat実機の通し試験は未実施です。
