using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using Serilog.Formatting.Compact;
using SPOMover.Core.Hosting;
using SPOMover.Core.Localization;
using SPOMover.Engine;
using SPOMover.Spo;
using SPOMover.State;

namespace SPOMover.Host;

/// <summary>
/// The engine with its state and the local REST API (for the CLI and scheduled tasks). The desktop app runs it
/// inside its own process; the same core can run without a window (spo-mover-host) for tests and automation.
/// </summary>
public static class HostApp
{
    /// <param name="configureServices">Lets tools replace services, e.g. the demo with the fake Graph.</param>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // The desktop app may be started from any folder (shortcut, Explorer).
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Data folder: state database, logs, temporary files. Default — %LOCALAPPDATA%\SPOMover.
        string dataDir = DataDirectory(builder.Configuration["data"]);
        SPOMover.Core.Localization.L.Load(dataDir);

        // By default the API is reachable only from this machine.
        if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
            builder.WebHost.UseUrls("http://localhost:5090");

        // REP-4: structured log in JSON lines plus the console.
        builder.Host.UseSerilog((context, services, log) => log
            .ReadFrom.Configuration(context.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(dataDir, "logs", "host-.jsonl"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 60));
        // STA-4: on exit the run service waits up to 30 s for current files, then interrupts them.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });
        builder.Services.AddSingleton(new StateStore(Path.Combine(dataDir, "state.db")));
        builder.Services.AddSingleton<ISpoAppFactory>(sp => new SpoAppFactory(sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton(new EngineOptions());
        builder.Services.AddSingleton<RunService>();
        builder.Services.AddSingleton<SpoBrowser>();
        builder.Services.AddSingleton<Ui.Services.SettingsService>();
        builder.Services.AddSingleton<Ui.Services.StatusService>();
        builder.Services.AddHostedService<RunHostService>();

        var access = new ApiAccess(dataDir);
        builder.Services.AddSingleton(access);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseWhen(c => c.Request.Path.StartsWithSegments("/api"), api =>
        {
            api.UseExceptionHandler(e => e.Run(ApiErrors.Handle));
            api.Use((context, next) => access.Allows(context.Request) ? next(context) : ApiAccess.Reject(context));
        });
        app.MapApi();
        // The address is known only once the server listens (it may be a free port picked by the OS).
        app.Lifetime.ApplicationStarted.Register(() => access.Publish(app.Services));
        app.Lifetime.ApplicationStopping.Register(access.Withdraw);
        return app;
    }

    public static string DataDirectory(string? configured)
    {
        string dir = ApiEndpoint.DataDirectory(configured);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>
/// Multi-user machines: the API listens on localhost, which every account of the machine can reach, so each request must
/// carry the key of this instance. The key is in api.json in the data folder (the user's profile by default), which other
/// accounts cannot read.
/// </summary>
public sealed class ApiAccess(string dataDir)
{
    public string Key { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public string? Url { get; private set; }

    public bool Allows(HttpRequest request) =>
        request.Headers.TryGetValue(ApiEndpoint.KeyHeader, out var value)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(value.ToString()), System.Text.Encoding.ASCII.GetBytes(Key));

    public static Task Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new
        {
            error = L.T("No access key for this SPOMover instance: it was started by another user or on another data folder. Start the app under your own account (the CLI will find it) or specify the same --data as the app."),
        });
    }

    internal void Publish(IServiceProvider services)
    {
        var addresses = services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        Url = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? addresses?.FirstOrDefault();
        if (Url is null)
            return; // in-memory test server: nothing to publish
        new ApiEndpoint(Url, Key, Environment.ProcessId).Write(dataDir);
    }

    internal void Withdraw() => ApiEndpoint.Delete(dataDir, Environment.ProcessId);
}
