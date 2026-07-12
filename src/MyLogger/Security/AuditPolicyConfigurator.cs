using System.Diagnostics;

namespace MyLogger.Security;

/// <summary>
/// アプリケーション起動時に、監視に必要な Windows 監査ポリシーを強制的に有効化する
/// (要件定義書 3.1)。他の HostedService より先に登録し、監視開始前に必ず完了させる。
///
/// "auditpol /get" の出力は OS の表示言語に応じて localize されるため
/// (例: 英語 "Success and Failure" / 日本語 "成功および失敗")、出力文字列を解析して
/// 現在の設定が十分かを判定する方式は言語環境によって誤判定する。
/// そのため現在値の確認は行わず、"auditpol /set" を毎回冪等に実行し、
/// そのプロセス終了コードのみで成否を判断する(既に有効な場合も無害な no-op)。
/// </summary>
public sealed class AuditPolicyConfigurator : IHostedService
{
    private readonly ILogger<AuditPolicyConfigurator> _logger;

    // サブカテゴリ名は OS の言語に依存するため GUID で指定する。
    private static readonly (string Guid, string Label)[] RequiredSubcategories =
    {
        ("{0CCE9224-69AE-11D9-BED3-505054503030}", "ファイル共有"),
        ("{0CCE9244-69AE-11D9-BED3-505054503030}", "詳細なファイル共有"),
        ("{0CCE9215-69AE-11D9-BED3-505054503030}", "ログオン"),
        ("{0CCE9216-69AE-11D9-BED3-505054503030}", "ログオフ"),
    };

    public AuditPolicyConfigurator(ILogger<AuditPolicyConfigurator> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (guid, label) in RequiredSubcategories)
        {
            try
            {
                EnsureEnabled(guid, label);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "監査ポリシー「{Label}」の設定に失敗しました。管理者権限で実行されているか確認してください", label);
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void EnsureEnabled(string subcategoryGuid, string label)
    {
        var exitCode = RunAuditPol($"/set /subcategory:\"{subcategoryGuid}\" /success:enable /failure:enable");
        if (exitCode == 0)
        {
            _logger.LogInformation("監査ポリシー「{Label}」が有効であることを確認しました(必要に応じて強制設定済み)", label);
        }
        else
        {
            _logger.LogError("監査ポリシー「{Label}」の有効化に失敗しました (ExitCode={ExitCode})", label, exitCode);
        }
    }

    private static int RunAuditPol(string arguments)
    {
        var psi = new ProcessStartInfo("auditpol.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("auditpol.exe を起動できませんでした");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return process.ExitCode;
    }
}
