# Jampanion 日本語説明書

Jampanion は、コード譜に合わせてピアノ・ベース・ドラムの伴奏を自動演奏する、ジャムセッション練習用のデスクトップアプリです。演奏の盛り上がりに応じて伴奏を変化させたり、ソロからテーマへ戻るタイミングを手動または自動で管理したりできます。

この説明書は、公開版 v0.7.8 の画面と機能を基準にしています。

## 1. 起動

### Windows

1. `Jampanion-Windows-x64.zip` を展開します。
2. 展開したフォルダーの `Jampanion.exe` を起動します。
3. 音が出ない場合は、Settings の MIDI Output と Mix を確認します。

Windows では外部 MIDI 機器のほか、内蔵の Trio 出力（ピアノ・ベース・ドラム）を使用できます。

### macOS

- Apple Silicon（Mシリーズ）: `Jampanion-macOS-arm64.zip`
- Intel Mac: `Jampanion-macOS-x64.zip`

ZIPを展開し、`Jampanion.app` を起動します。macOS が起動を止めた場合は、「システム設定」→「プライバシーとセキュリティ」→「このまま開く（Open Anyway）」を選びます。内蔵音源は CoreAudio を使用します。

外部 MIDI 機器を使う場合は、機器を接続して macOS の MIDI 設定でも認識されていることを確認してください。

## 2. 画面の見方

### 上部

- 曲名: 現在選択している曲。
- Arrangement Stage: `Theme`、`Solo / building`、`Solo / peak` など、伴奏の段階。
- 状態表示: カウントイン、演奏中、停止、テーマ戻りの状態など。
- Theme Return: テーマへ戻る方法を `Manual` / `Auto` から選択。
- Energy: Auto 判定に使う `Reference`、`Current`、Return limit を表示。
- `Start session`: カウントイン後に演奏を開始。
- `Stop`: 演奏を停止。
- `Panic`: 演奏を停止し、MIDIノートの消音を送信。音が残ったときに使用。
- 歯車ボタン: Settings を開く。

演奏中は Start session ボタンが `Back to head` になります。演奏中のスタイル変更は、現在のスタイルを `Playing`、次に適用するスタイルを `Queued` として表示します。

### 左側

- Song: 曲の検索、テンポ、スタイル、キー、臨時記号。
- Mix: Piano、Bass、Drums のオン・オフと音量、MIDI thru。
- Chord Sheet: コード譜の保存と表示倍率。

### 右側

コード譜が表示されます。演奏中の小節・コードはハイライトされ、必要に応じて自動スクロールします。リハーサルマーク、ループ記号、Coda、Ending も表示されます。

## 3. 基本操作

### 曲を選ぶ

1. Song の検索欄をクリックします。
2. 曲名の一部を入力します。
3. 候補をクリックして選択します。
4. 別の曲を探すときは、検索欄をもう一度クリックして入力を開始します。

曲を選んだだけでは演奏は始まりません。演奏中は曲を変更できないよう検索欄が無効になります。曲を変える場合は先に `Stop` を押してください。

### 演奏を開始・停止する

- `Start session` を押すと、カウントイン後に演奏が始まります。
- 検索欄などのテキスト入力中でなければ、Spaceキーでも開始できます。
- 演奏中に Spaceキーを押すと、`Back to head` と同じくテーマ戻りを予約します。
- `Back to head` はその場で譜面を飛ばすのではなく、音楽的に自然な区切りでテーマへ戻ります。通常は次のコーラスの頭です。
- すぐに止める場合は `Stop` を押します。Spaceキーは停止には使いません。
- 音が残った場合は `Panic` を押します。

テンポは停止中・演奏中ともに変更できます。範囲は 40～300 BPM、5 BPM刻みです。

### スタイル・キーを変える

Song の Style、Key、Accidentals を選択します。

- 4/4: Swing、Ballad、Bossa Nova、Latin
- 3/4: Jazz Waltz

演奏中は Style の変更を次の適切な区切りから適用します。Key の変更は停止中に行ってください。Accidentals はコード表記の♯／♭を切り替える表示設定です。

設定を曲ファイルへ保存する場合は、Song の `Save` を押します。保存対象がない内蔵曲では Save は使用できません。

### Mixを調整する

Piano、Bass、Drums のチェックを外すと、そのパートをミュートできます。各スライダーで音量を調整できます。

`MIDI thru` をオンにすると、選択した MIDI 入力を Ch.1 の Vibraphone 音源へ送ります。入力音をそのまま鳴らしたくない場合はオフにしてください。

### コード譜を拡大・縮小する

Chord Sheet 上部の Scale スライダーで、コード譜を 60～150% の10段階に変更できます。表示倍率を変えても、4小節単位の横配置は維持されます。

## 4. コード譜を編集する

編集できるのは、曲ライブラリにある `.cho`、`.chordpro`、`.chopro` ファイルです。内蔵曲や演奏中の曲は編集できません。

1. 曲を選び、`Stop` で停止します。
2. コードをダブルクリックして編集します。空欄で確定すると、そのコード区間を削除できます。
3. リハーサルマークの領域をダブルクリックして、追加・変更・削除します。
4. リハーサルマークを右クリックすると、そのセクションだけのスタイルを選べます。
5. 編集後、Chord Sheet の `Save` を押して `.cho` ファイルへ保存します。

編集中は Enter で確定、Escape でキャンセルできます。保存前の変更はメモリ上の編集内容です。外部で `.cho` ファイルを変更した場合は、先に Refresh library で読み直してください。

## 5. Theme Return と Energy

### Manual

初期設定です。演奏者が `Back to head` または Spaceキーを押すと、次の自然な区切りでテーマへ戻ります。自動では戻りません。

### Auto

MIDI入力の音数・ベロシティ・動きなどから演奏のエネルギーを推定し、ソロが落ち着いたと判断したときにテーマ戻りを予約します。Auto は実験的な機能なので、確実に戻したい場合は Manual を使用してください。

画面の表示は次の意味です。

- Reference: そのコーラスの基準となるエネルギー。
- Current: 直近のエネルギー。演奏中に更新されます。
- Return limit: Current が下回るとテーマ戻りの候補になる境界。
- Cancellation marker: 戻りを取り消すための判定位置。

Theme Return のスライダーで感度を調整します。感度を上げるほど、より小さなエネルギー低下でテーマへ戻りやすくなります。Auto の判定は音楽的な推定なので、意図どおりにならない場合は Manual に切り替えてください。

## 6. Settings

上部右側の歯車ボタンを押して開きます。設定項目は次の順です。

### MIDI

- Input: エネルギー分析や MIDI thru に使う入力ポート。
- Output: 内蔵音源または外部 MIDI 機器。
- `Refresh devices`: 接続・切断した MIDI 機器を再検索。

### Audio（Windowsのみ）

- Audio backend: `Automatic`、`ASIO`、`WinMM`。
- ASIO driver and output device: 使用する ASIO ドライバー。
- ASIO output channels: 出力チャンネル。
- Sample rate / Buffer: サンプルレートとバッファサイズ。

`Automatic` は ASIO を優先し、使用できない場合は WinMM に切り替えます。Audio 設定を変更すると、内蔵音源が再起動します。

macOS は CoreAudio を使用するため、Windows Audio の項目は表示されません。

### Song library

- Folder: 曲ファイルを保存するフォルダー。
- `Import iReal Pro`: `.html`、`.htm`、`.txt` の iReal Pro ファイルを読み込みます。
- `Refresh library`: フォルダー内の曲一覧を再読み込み。
- `Choose folder`: 曲フォルダーを変更。

初期フォルダーは次の場所です。

```text
Documents/Jampanion/Songs
```


## 7. 曲ファイル（ChordPro）

曲ファイルはプレーンテキストの ChordPro 形式です。最小限の例:

```text
{title: Autumn Leaves}
{key: Gm}
{time: 4/4}
{style: Swing}
{tempo: 120}
{start_of_grid}
A | Am7 . . . | D7 . . . | Gmaj7 . . . | Cmaj7 . . . |
  | Fmaj7 . . . | Bm7b5 . . . | E7b9 . . . | Am7 . . . |
{end_of_grid}
```

- 対応する拍子は 4/4 と 3/4 です。3/4 は Jazz Waltz として演奏されます。
- 伴奏エンジンは4小節以上のコード譜を必要とします。
- `.`、`/` は直前のコードの継続です。
- `N.C.` はピアノとベースを鳴らさない区間です。ドラムはスタイルに応じて続きます。
- 行頭の `A`、`B`、`A1` などはリハーサルマークになります。
- `Intro`、`Verse`、反復記号、Coda、Ending に対応しています。
- セクション別のスタイルは、例として `{x-jampanion-section-style: A|BossaNova}` のように指定できます。

読み込める拡張子は `.cho`、`.chordpro`、`.chopro` です。iReal Pro から取り込んだ曲は、対応する範囲で ChordPro に変換されます。変換後は、Intro／Verse、反復、D.C.／D.S.、Coda、Ending の順序を譜面で確認してください。

## 8. 内蔵曲

初回起動時に、次の18曲が曲ライブラリへコピーされます。

Autumn Leaves、All The Things You Are、Beautiful Love、Bye Bye Blackbird、Candy、Confirmation、Days Of Wine And Roses、Girl From Ipanema、I Love You、I'll Close My Eyes、It Could Happen To You、Just Friends、On Green Dolphin Street、Softly, As In A Morning Sunrise、Someday My Prince Will Come、Stella By Starlight、There Is No Greater Love、There Will Never Be Another You。

## 9. 音が出ないとき

1. Settings → MIDI → Output で、実際に存在する出力を選びます。
2. Windows は Audio backend、macOS は CoreAudio と MIDI 設定を確認します。
3. Mix の Piano、Bass、Drums がオンになっているか確認します。
4. 外部機器を使っている場合は、まず内蔵 Trio 出力で試します。
5. MIDI入力が原因か確認する場合は、Input を `(no MIDI input)` にして比較します。MIDI入力がなくても伴奏は再生できます。
6. 音が残った場合は `Panic` を押します。

曲変更・キー変更・コード譜編集ができない場合は、演奏中でないことを確認してください。曲変更とコード譜編集は演奏中には行えません。
