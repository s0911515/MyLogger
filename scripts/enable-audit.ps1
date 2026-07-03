# 共有フォルダアクセスの監査ポリシーを有効化する (管理者権限で実行)
# MyLogger の SmbAuditMonitor (ネットワークからのアクセス記録) に必要。
# サブカテゴリ名は OS の言語に依存するため GUID で指定している。

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

# ファイル共有 (イベント ID 5140, 5142-5144)
auditpol /set /subcategory:"{0CCE9224-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable

# 詳細なファイル共有 (イベント ID 5145: 共有内の個々のファイルへのアクセス)
# 注意: アクセスが多い環境ではセキュリティログの量が大幅に増える
auditpol /set /subcategory:"{0CCE9244-69AE-11D9-BED3-505054503030}" /success:enable /failure:enable

Write-Host ""
Write-Host "現在の設定:"
auditpol /get /subcategory:"{0CCE9224-69AE-11D9-BED3-505054503030}"
auditpol /get /subcategory:"{0CCE9244-69AE-11D9-BED3-505054503030}"

Write-Host ""
Write-Host "監査ポリシーを有効化しました。" -ForegroundColor Green
Write-Host "セキュリティログの最大サイズ拡張も推奨します。例:"
Write-Host '  wevtutil sl Security /ms:1073741824   # 1GB'
