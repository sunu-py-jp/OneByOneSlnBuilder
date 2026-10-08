param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo "artifacts/OneByOneSlnBuilder-$Runtime"
$zip = "$out.zip"
dotnet publish (Join-Path $repo "src/OneByOneSlnBuilder.App/OneByOneSlnBuilder.App.csproj") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o $out
if ($LASTEXITCODE -ne 0) { throw "アプリの発行に失敗しました。" }
if (!(Test-Path (Join-Path $out "OneByOneSlnBuilder.Logger.dll"))) { throw "診断ロガーが含まれていません。" }
Copy-Item (Join-Path $repo "README.md") (Join-Path $out "README.md") -Force
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -Force
Write-Host "配布用ZIP: $zip"
