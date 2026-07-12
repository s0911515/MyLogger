using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using MyLogger.Config;

namespace MyLogger.Security;

/// <summary>
/// DB ファイル / 設定ファイルが一般ユーザーによって削除・改ざんされないよう、
/// NTFS アクセス権を SYSTEM と Administrators のみに制限する (要件定義書 5.2)。
/// 起動の度に強制的に再適用する (冪等)。
/// </summary>
public sealed class PermissionHardener : IHostedService
{
    private readonly MonitorOptions _options;
    private readonly ILogger<PermissionHardener> _logger;

    public PermissionHardener(IOptions<MonitorOptions> options, ILogger<PermissionHardener> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dataDir = Environment.ExpandEnvironmentVariables(_options.DataDirectory);
        try
        {
            Directory.CreateDirectory(dataDir);
            HardenDirectory(dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "データディレクトリの ACL 設定に失敗しました: {Dir}", dataDir);
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (File.Exists(configPath))
            {
                HardenFile(configPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定ファイルの ACL 設定に失敗しました: {Path}", configPath);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void HardenDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        AddFullControl(security,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
        info.SetAccessControl(security);
        _logger.LogInformation("ディレクトリの ACL を SYSTEM/Administrators のみに制限しました: {Path}", path);
    }

    private void HardenFile(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        AddFullControl(security, InheritanceFlags.None, PropagationFlags.None);
        info.SetAccessControl(security);
        _logger.LogInformation("ファイルの ACL を SYSTEM/Administrators のみに制限しました: {Path}", path);
    }

    private static void AddFullControl(FileSystemSecurity security, InheritanceFlags inheritance, PropagationFlags propagation)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, inheritance, propagation, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, inheritance, propagation, AccessControlType.Allow));
    }
}
