using YMusicLite.Components;
using YMusicLite.Services;
using MudBlazor.Services;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.IO;

// Configure Serilog as the ONLY logging provider
// Place log files under the application base directory (bin output) to avoid triggering dotnet watch browser refresh loops.
var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "YMusicLite")
    .WriteTo.Console(theme: AnsiConsoleTheme.Sixteen, outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDirectory, "ymusic-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 10,
        shared: true,
        buffered: false,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Remove all default logging providers so Serilog is the single provider
builder.Logging.ClearProviders();

// Add YAML configuration support
builder.Configuration.AddYamlFile("config.yaml", optional: true, reloadOnChange: true);

// Plug Serilog into the host
builder.Host.UseSerilog();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add application services
builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
builder.Services.AddScoped<IYouTubeService, YouTubeService>();
builder.Services.AddScoped<IGoogleAuthService, GoogleAuthService>();
builder.Services.AddScoped<IDownloadService, DownloadService>();
builder.Services.AddScoped<ISyncService, SyncService>();
builder.Services.AddSingleton<SchedulingService>();
builder.Services.AddSingleton<ISchedulingService>(provider => provider.GetRequiredService<SchedulingService>());
builder.Services.AddHostedService<SchedulingService>();
builder.Services.AddSingleton<IMetricsService, MetricsService>();

// Add HTTP client for potential OAuth usage
builder.Services.AddHttpClient();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

try
{
    Log.Information("Starting YMusicLite application");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
