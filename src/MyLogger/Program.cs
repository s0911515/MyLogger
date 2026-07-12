using MyLogger.Config;
using MyLogger.Logging;
using MyLogger.Monitors;
using MyLogger.Security;
using MyLogger.Util;

// appsettings.json は起動時のカレントディレクトリではなく exe と同じフォルダから読む
// (サービス実行時はカレントが C:\Windows\System32 になるため必須)
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Windows サービスとしてもコンソールアプリとしても動作する
builder.Services.AddWindowsService(options => options.ServiceName = "MyLogger");

builder.Services.Configure<MonitorOptions>(
    builder.Configuration.GetSection(MonitorOptions.SectionName));

// 監査ポリシーの自動設定 (3.1)・NTFS 権限保護 (5.2) は、他の監視・書き込み処理より前に
// 完了させる必要があるため最初に登録する (ジェネリックホストは登録順に StartAsync を await して起動する)。
builder.Services.AddHostedService<AuditPolicyConfigurator>();
builder.Services.AddHostedService<PermissionHardener>();

builder.Services.AddSingleton<ActivityLogger>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ActivityLogger>());
builder.Services.AddSingleton<ProcessUserResolver>();

builder.Services.AddHostedService<FileWatcherMonitor>();
builder.Services.AddHostedService<EtwFileIoMonitor>();
builder.Services.AddHostedService<SmbAuditMonitor>();
builder.Services.AddHostedService<LogonMonitor>();

// サービス実行時は Windows イベントログにも動作状況を出す
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "MyLogger";
});

var host = builder.Build();
host.Run();
