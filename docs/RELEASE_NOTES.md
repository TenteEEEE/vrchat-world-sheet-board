スプレッドシートの公開・管理者交代の操作を改善し、掲示板の更新状態表示を追加しました。

- スプレッドシートごとに全範囲を1つのURLで公開。ワールド内の閲覧権限は SheetBoard Source ごとに設定できます。
- 更新中の表示・ランプ点滅・更新ボタンの無効化に対応。手動更新の待機中もボタンに反映します。
- 設定シートに範囲ごとの状態を表示し、「選択範囲を掲示板に追加」を追加。管理者向け操作をまとめ、案内・エラー・失敗メールの説明を改善しました。
- GitHub連携の期限切れや管理者交代時の処理を改善しました。
- Source / Display の表示文言を Inspector から編集できます。

### 更新時の操作

既存のスプレッドシートでは同梱の `SheetBoard.gs` を更新し、**SheetBoard → 管理者用 → GitHub連携をやり直す** を一度実行してください。同じGitHubアカウントで連携すると既存のGist URLを引き続き使えます。

### インストール

VCC の一覧 `https://tenteeeee.github.io/vpm-repos/index.json` から **Sheet Board 0.1.1** を追加・更新してください。手動導入には添付の `.unitypackage` を使用できます。

Unity 2022.3 / VRChat Worlds SDK 3.8 以降。詳しい手順は [日本語 README](https://github.com/TenteEEEE/vrchat-world-sheet-board/blob/main/README.ja.md) を参照してください。

検証: Apps Script のモック回帰テスト60件、Unity 2022.3.22f1 / Worlds SDK 3.10.5 でのUdonSharpコンパイル、prefab配線・権限設定・更新状態表示・サンプル描画、配布アーカイブの内容とチェックサム。実Google/GitHubアカウントの連携とVRChat実機の通し試験は未実施です。
