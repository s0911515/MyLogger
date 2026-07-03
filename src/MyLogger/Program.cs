using MyLogger.Config;
using MyLogger.Logging;
using MyLogger.Monitors;

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

builder.Services.AddSingleton<ActivityLogger>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ActivityLogger>());
builder.Services.AddHostedService<FileWatcherMonitor>();
builder.Services.AddHostedService<EtwFileIoMonitor>();
builder.Services.AddHostedService<SmbAuditMonitor>();

// サービス実行時は Windows イベントログにも動作状況を出す
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "MyLogger";
});

var host = builder.Build();
host.Run();
