Google スプレッドシートの表を、ワールドを再アップロードせずに VRChat の掲示板へ反映する初版です。

- シートの色・文字書式・セル結合・列幅・行高・配置・折り返しを反映。
- ライト／ダークの掲示板 prefab、手動更新、近接時のみの取得に対応。
- Apps Script の GitHub 連携、自動公開、生存通知、失敗通知、管理者交代に対応。
- 公開データは GitHub Gist を経由し、来訪者の Allow Untrusted URLs 設定は不要。

### インストール

VCC の Settings → Packages → Add Repository に次の URL を追加し、Worlds プロジェクトへ **Sheet Board 0.1.0** を追加してください。

https://tenteeeee.github.io/vpm-repos/index.json

手動導入には添付の `.unitypackage` を使用できます。VPM の `.zip` と同時に導入する必要はありません。

Unity 2022.3 / VRChat Worlds SDK 3.8 以降が必要です。手順は [日本語 README](https://github.com/TenteEEEE/vrchat-world-sheet-board/blob/main/README.ja.md) を参照してください。

公開範囲は URL を知っている人が閲覧でき、Gist に履歴が残ります。個人情報を含めず、スプレッドシートの編集者は信頼できる人に限定してください。

検証: Unity 2022.3.22f1 / Worlds SDK 3.10.5 でのパッケージ導入・UdonSharp コンパイル・prefab 配線・サンプル描画、Apps Script のモック回帰テスト。実アカウントでの Google/GitHub 連携と VRChat 実機の通し試験は未実施です。
