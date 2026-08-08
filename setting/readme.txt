■ キャッシュサーバー接続設定について

jkcnsl.json に "cache_server_url" を設定することで、ニコニコ実況（Lコマンド）の
接続をキャッシュサーバー経由に切り替えることができます。


■ jkcnsl.json の設定例

{
  "cache_server_url": "wss://your-vps.example.com",
  "cache_commentable": false
}

cache_server_url を設定しない場合、または空欄の場合は従来通りニコニコ実況に
直接接続します。


■ cache_commentable（コメント投稿の有効化）

キャッシュサーバー経由接続時にコメント投稿を有効にするかどうかを指定します。

  false（デフォルト）: コメント投稿を行わない（閲覧専用）
  true              : コメント投稿を有効にする

true に設定すると、jkcnsl はキャッシュサーバーへの接続時に投稿権限をリクエスト
します。キャッシュサーバーが対応している場合、NX-Jikkyo などの避難所へのコメント
投稿がキャッシュサーバー経由で行われます。

実行中に S コマンドで変更することもできます:

  Scache_commentable true    ← 有効化
  Scache_commentable false   ← 無効化


■ jkcnsl の起動方法

【方法1】jkcnsl.json を jkcnsl.exe と同じフォルダに置く

  jkcnsl.exe と同じ場所に jkcnsl.json を配置するだけで自動的に読み込まれます。
  起動方法は従来と変わりません。

    jkcnsl

  NicoJK から起動する場合も NicoJK.ini の変更は不要です。

    execJKcnsl=jkcnsl.exe


【方法2】setting フォルダを設定ディレクトリとして指定する

  このフォルダ（setting）を設定ディレクトリとして -d オプションで指定します。

    jkcnsl -d setting

  NicoJK から起動する場合は NicoJK.ini の execJKcnsl に引数を追加してください。

    execJKcnsl=jkcnsl.exe -d setting


■ NX-Jikkyo（避難所）をキャッシュサーバー経由にする場合

NicoJK.ini の refugeUri をキャッシュサーバーのアドレスに変更してください。
（jkcnsl の改造は不要です）

  refugeUri=wss://your-vps.example.com/watch/{jkID}


■ キャッシュサーバー側の設定（appsettings.json）

接続するチャンネルをキャッシュサーバーの appsettings.json に登録してください。

  Lコマンド（ニコニコ実況）経由の場合:
    "ch1":   "nicovideo:ch1"
    "ch4":   "nicovideo:ch4"
    "ch101": "nicovideo:ch101"

  Rコマンド（NX-Jikkyo）経由の場合:
    "jk1": "wss://nx-jikkyo.tsukumijima.net/api/v1/channels/jk1/ws/watch"
    "jk4": "wss://nx-jikkyo.tsukumijima.net/api/v1/channels/jk4/ws/watch"
