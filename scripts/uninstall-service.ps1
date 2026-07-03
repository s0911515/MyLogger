# MyLogger サービスをアンインストールする (管理者権限で実行)

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$serviceName = 'MyLogger'
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "$serviceName サービスは登録されていません。"
    return
}

if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
sc.exe delete $serviceName

Write-Host "$serviceName サービスを削除しました。"
Write-Host "ログファイル (C:\ProgramData\MyLogger\logs) は残っています。不要なら手動で削除してください。"
