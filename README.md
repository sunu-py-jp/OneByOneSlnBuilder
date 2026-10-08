# OneByOne Sln Builder

指定フォルダ以下の **すべての `.sln` を順番にReleaseリビルド**し、OneByOneに取り込む診断JSONLを作るWindowsデスクトップアプリです。C# / VB.NET、.NET Frameworkのプロジェクトを含め、インストール済みのVisual Studio / Build ToolsのMSBuildを使います。

## 使い方

1. 配布ZIPをフォルダごと展開し、`OneByOneSlnBuilder.exe` を起動します。
2. **対象フォルダ**に、OneByOneで指定する対象フォルダと同じ場所を選びます。診断の相対パスの基準になります。
3. **出力フォルダ**を選びます。ソース管理を汚さないよう、対象フォルダの外を推奨します。
4. 自動検出された **MSBuild.exe** を確認します。必要なら別の実行ファイルを選択できます。
5. **Releaseビルド開始**を押します。完了後、結果フォルダの `diagnostics.jsonl` をOneByOneに取り込みます。

警告も標準で出力します。エラーだけ必要なら「警告を含める」をオフにしてください。プラットフォームは空欄で各ソリューションの既定値を使い、必要な場合だけ `Any CPU` / `x86` / `x64` などを指定します。`Release` 構成が存在しないソリューションは失敗として記録します。

## 実行環境

- Windows 10 / 11 x64
- Visual Studio 2022 または Build Tools 2022（MSBuildと、対象プロジェクトに必要なワークロードを導入）
- 対象に応じたSDK・参照アセンブリ・.NET Framework Developer Pack。VB.NET / .NET Framework 4.8なら4.8の開発用資材が必要です。
- 配布ZIPは.NETランタイムを同梱するため、本アプリ用の.NETの別途インストールは不要です。対象ソリューションの依存環境は必要です。

## 処理と出力

- サブフォルダ・隠しフォルダも検索し、拡張子の大文字小文字を区別せず `.sln` を処理します。リンク／ジャンクションは辿りません。`.slnx` は対象外です。
- ソリューション間は直列です。1件失敗しても残りを続行します。
- 毎回 `Rebuild` して、増分ビルドで診断が出なくなることを防ぎます。通常のリビルドと同様に対象プロジェクトの `bin` / `obj` が更新され、ビルドイベントも実行されます。
- MSBuildの構造化イベントを取得するため、日本語のエラーメッセージや空白を含むパスにも対応します。
- 標準でNuGet復元も実施します。既に復元済みで不要ならオフにできます。
- 出力先には実行ごとの新しいサブフォルダを作り、過去の診断を上書きしません。

```text
出力フォルダ/
  20261008-120000-xxxxxxxx/
    diagnostics.jsonl           OneByOneに取り込むファイル（診断がある場合）
    summary.json                全ソリューションの成否・件数・ログの対応
    run.json                    実行条件とソリューション一覧
    logs/
      0001.log                  標準出力・標準エラー
      0001.events.jsonl         MSBuildから取得した元の診断
      0001.unmapped.jsonl       JSONLに変換できなかった診断と理由
```

`diagnostics.jsonl` はUTF-8・BOMなしで、1行に1診断です。

```jsonl
{"file":"src/Orders/OrderWriter.vb","message":"型 'LegacyStore' は定義されていません。","line":42,"code":"BC30002"}
```

`file` / `message` は必須、`line` / `code` は値があるときだけ出力します。`file` は対象フォルダからの相対パスで、区切りは `/` です。ソリューションをまたぐ完全一致の診断を重複排除します。重大度・ソリューション名はOneByOneの固定形式に含まれないため、元の診断ログと結果一覧で確認します。

**診断0件では `diagnostics.jsonl` を作りません。** OneByOneに古い診断が取り込まれている場合は「解除」してください。ビルド失敗や対象外診断がある場合、0件でもビルド成功とは限りません。画面と `summary.json` を確認してください。

ファイル未特定、対象フォルダ外、存在しないファイル、OneByOneが扱えないパスは診断JSONLに入れず、理由付きで `unmapped.jsonl` に残します。最大32 MiBを超えた場合は出力エラーにし、元のログを保持します。対象を分割するか、警告を除いて再実行してください。

中止時はプロセスツリーを停止し、取得済みの診断を `diagnostics.partial.jsonl` に保存します。未実行ソリューションも結果一覧に残り、通常の `diagnostics.jsonl` は作りません。

## ビルド・配布

開発には.NET 8以降のSDKが必要です。GUIは.NET 8、MSBuild用ロガーは.NET Standard 2.0です。

```powershell
dotnet build OneByOneSlnBuilder.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts/package.ps1
```

`artifacts/OneByOneSlnBuilder-win-x64.zip` を配布できます。`.exe` だけを取り出さず、ZIP内のファイル一式を使用してください。アプリ配置先にはMSBuildのロガー指定で特別な意味を持つ `;`・`,`・`"` を使用しないでください。

## 検証

テストハーネスは.NET 10 SDKで実行します（GUIの実行要件とは別です）。外部テストフレームワークは不要です。

```powershell
dotnet run --project tests/OneByOneSlnBuilder.Tests -c Release
```

パス解決・固定JSONL形式・複数ソリューションの継続処理・重複除去・中止処理に加え、実際のMSBuildイベントによるロガー連携を検証します。GitHub ActionsではWindowsとmacOSでテストし、Windows版アプリのビルドと配布ZIP作成も行います。

実際の製品ソリューションのビルドとGUIの操作確認は、対象の開発環境を備えたWindowsで行ってください。

## 構成

| プロジェクト | 役割 |
|---|---|
| `src/OneByOneSlnBuilder.App` | Windows Formsの操作画面 |
| `src/OneByOneSlnBuilder.Core` | 検索・MSBuild実行・JSONL変換 |
| `src/OneByOneSlnBuilder.Logger` | MSBuildのエラー／警告イベント収集 |
| `tests/OneByOneSlnBuilder.Tests` | 自動テストとMSBuild連携検証 |

実行スイッチは[MSBuild公式リファレンス](https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-command-line-reference)、MSBuildの検出は[vswhereの公式例](https://github.com/microsoft/vswhere/wiki/Find-MSBuild)、イベント取得は[公式ロガー仕様](https://learn.microsoft.com/en-us/visualstudio/msbuild/build-loggers)に基づきます。
