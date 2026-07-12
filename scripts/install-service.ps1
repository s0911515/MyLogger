# MyLogger を Windows サービスとしてビルド・インストールする (管理者権限で実行)

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$serviceName = 'MyLogger'
$repoRoot    = Split-Path -Parent $PSScriptRoot
$project     = Join-Path $repoRoot 'src\MyLogger\MyLogger.csproj'
$publishDir  = Join-Path $repoRoot 'publish'
$exePath     = Join-Path $publishDir 'MyLogger.exe'

# 既存サービスがあれば停止して削除
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "既存の $serviceName サービスを停止・削除します..."
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "ビルド (publish) 中..."
dotnet publish $project -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish に失敗しました" }

Write-Host "サービスを登録します: $exePath"
New-Service -Name $serviceName `
    -BinaryPathName "`"$exePath`"" `
    -DisplayName 'MyLogger ファイル操作監視' `
    -Description '情報漏洩対策のためファイル操作 (ローカル / ネットワーク共有 / USB) をログに記録します' `
    -StartupType Automatic | Out-Null

# 異常終了時の自動再起動を設定
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# アプリ動作ログ用のイベントソースを登録
if (-not [System.Diagnostics.EventLog]::SourceExists('MyLogger')) {
    New-EventLog -LogName Application -Source 'MyLogger'
}

Start-Service -Name $serviceName
Get-Service -Name $serviceName

Write-Host ""
Write-Host "インストール完了。DB 出力先: C:\ProgramData\MyLogger\data\activity.db" -ForegroundColor Green
Write-Host "監査ポリシーの有効化と NTFS 権限保護はサービス起動時にアプリ自身が自動で行います。" -ForegroundColor Yellow
