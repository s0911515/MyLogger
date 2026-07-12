Get-WinEvent -LogName "Microsoft-Windows-Sysmon/Operational" | 
    Where-Object { $_.Id -eq 11 -or $_.Id -eq 23 } | 
    ForEach-Object {
        $xml = [xml]$_.ToXml()
        $data = $xml.Event.EventData.Data
        $path = ($data | Where-Object { $_.Name -eq "TargetFilename" }).'#text'
        
        $time = $_.TimeCreated
        $user = ($data | Where-Object { $_.Name -eq "User" }).'#text'
        $prog = Split-Path ($data | Where-Object { $_.Name -eq "Image" }).'#text' -Leaf
        
        # 完全削除の場合（Explorerに限らずCLI/スクリプト経由の削除も含む）
        if ($_.Id -eq 23) {
            [PSCustomObject]@{
                "日時(JST)"  = $time
                "イベント"   = "完全削除(ゴミ箱を経由しない削除)"
                "操作ユーザー" = $user
                "操作プログラム" = $prog
                "対象ファイル/パス" = $path
            }
            return
        }

        # ゴミ箱への移動（通常削除）の場合
        if ($path -like "*`$Recycle.Bin*") {
            $iPath = $path
            if ($path -like "*\`$R*") {
                $parent = Split-Path $path -Parent
                $leaf = Split-Path $path -Leaf
                $iPath = Join-Path $parent ($leaf -replace "^\`$R", "`$I")
            }

            $originalPath = ""
            if (Test-Path $iPath) {
                try {
                    $bytes = [System.IO.File]::ReadAllBytes($iPath)
                    if ($bytes.Length -gt 28) {
                        $rawString = [System.Text.Encoding]::Unicode.GetString($bytes, 28, ($bytes.Length - 28))
                        $originalPath = if ($rawString.Contains([char]0)) { $rawString.Substring(0, $rawString.IndexOf([char]0)) } else { $rawString.Trim() }
                    }
                } catch { $originalPath = "" }
            }

            # 正しいWindowsのパスの形になっていないノイズ（一時作成ログなど）は一切出力しない
            if ([string]::IsNullOrEmpty($originalPath) -or $originalPath -notmatch "^[a-zA-Z]:\\") {
                return
            }

            [PSCustomObject]@{
                "日時(JST)"  = $time
                "イベント"   = "通常削除(ゴミ箱移動)"
                "操作ユーザー" = $user
                "操作プログラム" = $prog
                "対象ファイル/パス" = "$originalPath ➔ (ゴミ箱へ)"
            }
            return
        }

        # 通常の作成・コピーの場合
        [PSCustomObject]@{
            "日時(JST)"  = $time
            "イベント"   = "作成/コピー"
            "操作ユーザー" = $user
            "操作プログラム" = $prog
            "対象ファイル/パス" = $path
        }
    } | Format-Table -AutoSize