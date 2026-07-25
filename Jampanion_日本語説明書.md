# Jampanion 日本語説明書

Jampanion は、コード譜に合わせてピアノ・ベース・ドラムの伴奏を自動演奏する、ジャムセッション練習用のデスクトップアプリです。演奏の盛り上がりに応じて伴奏を変化させたり、ソロからテーマへ戻るタイミングを手動または自動で管理したりできます。

この説明書は、公開版 v0.8.1 の画面と機能を基準にしています。

## 1. 起動

### Windows

1. `Jampanion-Windows-x64.zip` を展開します。
2. 展開先の `Jampanion.exe` を起動します。
3. 初期状態では、内蔵の Trio 出力でピアノ、ベース、ドラムを鳴らします。
4. 手動で選択した外部 MIDI 出力は、次回起動時にも利用可能であれば復元されます。

### macOS

- Apple Silicon（Mシリーズ）: `Jampanion-macOS-arm64.zip`
- Intel Mac: `Jampanion-macOS-x64.zip`

ZIPを展開し、`Jampanion.app` を起動します。macOS が起動を止めた場合は、「システム設定」→「プライバシーとセキュリティ」→「このまま開く（Open Anyway）」を選びます。内蔵音源は CoreAudio を使用します。

外部 MIDI 機器を使う場合は、機器を接続して macOS の MIDI 設定でも認識されていることを確認してください。

## 2. 画面の見方

### 上部

- 曲名: 現在選択されている曲です。
- Arrangement Stage: `Theme`、`Solo / building`、`Solo / peak` など、現在の伴奏段階です。表示と演奏の段階は連動しており、段階の切り替えは急に変わらないよう徐々に行われます。
- 状況表示: カウントイン、再生中、停止、テーマ戻りの予約や確定などを表示します。
- `Theme Return`: テーマ戻りの方式を `Manual` と `Auto` で切り替えます。初期状態は `Manual` です。
- `Reference`: そのコーラスで最初に演奏が始まった時点から、最後の2小節より前までの Short energy の平均です。赤い線は現在のテーマ戻り判定限界です。
- `Current`: 直近2小節の Short energy の平均です。演奏中に更新されます。
- `Start session` / `Back to head`: 停止中は演奏開始、再生中はテーマ戻りの予約です。
- `Stop`: 演奏を停止します。
- `Panic`: 演奏を停止し、MIDIノートの消音を送ります。音が残った場合に使います。
- 歯車ボタン: Settingsを開きます。

### 左側

- `Song`: 曲の検索と、テンポ、スタイル、キー、臨時記号、曲の長さを設定します。
- `Mix`: Piano、Bass、Drums のオン・オフと音量を個別に調整します。
- MIDI、Windows Audio、Song LibraryはSettingsで設定します。

### 右側

コード譜が表示されます。演奏中の小節・コードはハイライトされ、必要に応じて自動スクロールします。リハーサルマーク、ループ記号、Coda、Ending も表示されます。

## 3. 基本操作

### 曲を選ぶ

1. `Song` の入力欄をクリックします。
2. 曲名の一部を入力します。候補は入力に合わせて絞り込まれます。
3. 候補をクリックして選択します。選択された曲名は入力欄に表示されます。
4. 新しい曲を探すときは、選択済みの入力欄をもう一度クリックします。入力欄が空になり、そのまま次の検索を入力できます。

検索候補をクリックしただけで演奏が始まることはありません。再生中に曲を変更する場合は、先に `Stop` を押してください。

曲を選んだだけでは演奏は始まりません。演奏中は曲を変更できないよう検索欄が無効になります。曲を変える場合は先に `Stop` を押してください。

- `Start session` を押すとカウントインの後に演奏が始まります。
- Spaceキーでも同じように開始できます。
- 再生中に Spaceキーを押すと、`Back to head` と同じく、次の適切なコーラスの頭からテーマへ戻る予約になります。
- 演奏をすぐ止める場合は `Stop` を使います。Spaceキーは停止操作には使いません。
- `Back to head` は、押した瞬間にコード譜を頭へ飛ばすのではなく、音楽的な区切りでテーマへ戻ります。基本的には次のコーラスの頭です。コーラスの最初の2小節以内だけは、そのコーラスの終わりで戻れる場合があります。

テンポは再生中でも変更できます。`Tempo` の値は5 BPM刻みで、40から300 BPMまで設定できます。

テンポは停止中・演奏中ともに変更できます。範囲は 40～300 BPM、5 BPM刻みです。

コード譜上部の `Scale` スライダーで10段階に変更できます。4小節の横幅は維持したまま、コード名の文字とセルの高さが変わります。小さくすると縦に多くの小節を表示でき、長いコード名も見やすくなります。

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

初期設定です。演奏者が `Back to head` または Spaceキーで戻りを予約します。ソロの途中で勝手にテーマへ戻ることはありません。

### Auto

MIDI入力の音数・ベロシティ・動きなどから演奏のエネルギーを推定し、ソロが落ち着いたと判断したときにテーマ戻りを予約します。Auto は実験的な機能なので、確実に戻したい場合は Manual を使用してください。

画面の表示は次の意味です。

- Reference: そのコーラスの基準となるエネルギー。
- Current: 直近のエネルギー。演奏中に更新されます。
- Return limit: Current が下回るとテーマ戻りの候補になる境界。
- Cancellation marker: 戻りを取り消すための判定位置。

Theme Return のスライダーで感度を調整します。感度を上げるほど、より小さなエネルギー低下でテーマへ戻りやすくなります。Auto の判定は音楽的な推定なので、意図どおりにならない場合は Manual に切り替えてください。

## 6. Settings

## 6. Settings

上部右側の歯車ボタンを押して開きます。

### MIDI

### MIDI

`Input` で演奏に使う入力ポートを選びます。入力は主にエネルギー分析に使われ、ベースや伴奏を空白にして演奏を崩すためのものではありません。MIDI入力が無い場合も、内蔵伴奏は通常通り再生できます。

### Audio（Windowsのみ）

`Output` で音源または外部MIDI機器を選びます。初期状態では内蔵 Trio が選択されます。手動で選んだ入力・出力ポートは保存され、次回起動時に同じ名前のポートが存在すれば自動的に選ばれます。

ポートを接続・切断した後は `Refresh devices` を押してください。再生中でもポート変更は反映されます。音が出ない場合は、出力ポート、各パートのオン・オフ、音量、OS側の音源状態を順番に確認します。

macOS は CoreAudio を使用するため、Windows Audio の項目は表示されません。

`MIDI thru` をオンにすると、入力された演奏をCh.1のVibraphoneへ送ります。伴奏を確認するときは、まずオフにしておくと分かりやすいです。

### Windows Audio

Windowsでは、Audio backend、ASIO driver、output channels、sample rate、bufferを設定できます。macOSではCoreAudioを使用するため、この項目は表示されません。

### Song Library

曲フォルダの変更、iReal Proファイルのインポート、曲一覧の更新を行います。操作結果やエラーはSettings下部のステータス欄に表示されます。演奏中はSong Libraryの操作は無効になります。

- Folder: 曲ファイルを保存するフォルダー。
- `Import iReal Pro`: `.html`、`.htm`、`.txt` の iReal Pro ファイルを読み込みます。
- `Refresh library`: フォルダー内の曲一覧を再読み込み。
- `Choose folder`: 曲フォルダーを変更。

初期フォルダーは次の場所です。

```text
Documents/Jampanion/Songs
```

別の場所を使う場合は、Settingsの`Song Library`にある`Choose folder`でフォルダを指定してください。曲ファイルはプレーンテキストの `.cho` です。

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

Settingsの`Song Library`にある`Import iReal Pro`から、`.html`、`.htm`、`.txt`のiReal Proファイルを選択します。変換後は、通常の曲と同じく曲一覧から検索して選択できます。変換された `.cho` ファイルは、現在の曲フォルダに保存されます。

## 8. 内蔵曲

初回起動時に、次の18曲が曲ライブラリへコピーされます。

Autumn Leaves、All The Things You Are、Beautiful Love、Bye Bye Blackbird、Candy、Confirmation、Days Of Wine And Roses、Girl From Ipanema、I Love You、I'll Close My Eyes、It Could Happen To You、Just Friends、On Green Dolphin Street、Softly, As In A Morning Sunrise、Someday My Prince Will Come、Stella By Starlight、There Is No Greater Love、There Will Never Be Another You。

## 8. 音が出ない・演奏が不安定なとき

1. Settingsの`MIDI`にある`Output`で、実際に存在するポートを選んでいるか確認します。
2. Windowsでは音源の音量、macOSではCoreAudioとMIDI設定を確認します。
3. `Mix` の Piano、Bass、Drums がオンになっているか確認します。
4. 音が残った場合は `Panic` を押します。
5. 外部機器で問題がある場合、まず内蔵Trio出力で同じ曲・テンポを試します。
6. MIDI入力が原因かを切り分ける場合は、入力を `(no MIDI input)` にして比較します。

テンポ変更、曲変更、スタイル変更、キー変更などで挙動が不自然になった場合は、いったん `Stop` を押してから設定を変更し、再度開始してください。

## 9. 同梱曲

公開版には次の18曲が内蔵されています。

Autumn Leaves、All The Things You Are、Beautiful Love、Bye Bye Blackbird、Candy、Confirmation、The Days Of Wine And Roses、Girl From Ipanema、I Love You、I'll Close My Eyes、It Could Happen To You、Just Friends、On Green Dolphin Street、Softly, As In A Morning Sunrise、Someday My Prince Will Come、Stella By Starlight、There Is No Greater Love、There Will Never Be Another You。

