using Cadence.Web.Data;
using Cadence.Web.Services;

// The UI is English regardless of the host machine's locale (dates, numbers, day names).
var culture = new System.Globalization.CultureInfo("en-US");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;

var builder = WebApplication.CreateBuilder(args);

// Render, Railway and similar hosts tell the app which port to bind through $PORT.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddControllersWithViews();

// Render terminates TLS in front of the app; trust its X-Forwarded-* headers so generated URLs (intake snippets,
// OAuth callbacks, og:image) use https. Only behind a proxy: Render sets RENDER=true, anywhere else opt in with
// ForwardedHeaders:Enabled, otherwise any client could claim https or spoof its IP.
var trustProxy = Environment.GetEnvironmentVariable("RENDER") is "true" || builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.Configure<RequestLocalizationOptions>(o => { o.DefaultRequestCulture = new("en-US"); o.SupportedCultures = [culture]; o.SupportedUICultures = [culture]; });
builder.Services.AddHttpContextAccessor();

// ── Data backend ──────────────────────────────────────────────────────────────
// "sqlite" (default): local file, migrations applied at startup.
// "d1": SQL is sent to the Cloudflare Worker, which runs it against the D1 binding.
var backend = builder.Configuration["Database:Backend"] ?? "sqlite";
if (backend.Equals("d1", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<ISqlExecutor, D1WorkerSqlExecutor>(http =>
    {
        http.BaseAddress = new Uri(builder.Configuration["Database:WorkerUrl"] ?? throw new InvalidOperationException("Database:WorkerUrl is required for the d1 backend."));
        http.DefaultRequestHeaders.Add("X-Internal-Token", builder.Configuration["Database:InternalToken"] ?? throw new InvalidOperationException("Database:InternalToken is required for the d1 backend."));
        http.Timeout = TimeSpan.FromSeconds(10);
    });
}
else
{
    var path = builder.Configuration["Database:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "cadence.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var cs = SqliteSqlExecutor.BuildConnectionString(path);
    builder.Services.AddSingleton<ISqlExecutor>(new SqliteSqlExecutor(cs));
    builder.Services.AddSingleton(sp => new MigrationRunner(cs, ResolveMigrationsDir(builder.Environment.ContentRootPath), sp.GetRequiredService<ILogger<MigrationRunner>>()));
}

builder.Services.AddScoped<MemberRepository>();
builder.Services.AddScoped<LeadRepository>();
builder.Services.AddScoped<SuggestionRepository>();
builder.Services.AddScoped<ActivityRepository>();
builder.Services.AddScoped<StandupRepository>();
builder.Services.AddScoped<WebhookRepository>();
builder.Services.AddScoped<LeadPipelineService>();
builder.Services.AddScoped<EnrichmentService>();
builder.Services.AddScoped<CurrentMember>();
builder.Services.AddScoped<SettingsService>();

// Suggestions: fast rules always; AI research once a provider and API key are connected in Settings (never from env vars).
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddHttpClient("openrouter", http => http.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<AiClient>();
builder.Services.AddScoped<IEnrichmentProvider, HeuristicEnrichmentProvider>();
builder.Services.AddScoped<IEnrichmentProvider, LlmEnrichmentProvider>();

// Intake, background work and outbound notifications.
builder.Services.AddSingleton<BackgroundJobs>();
builder.Services.AddHostedService<BackgroundJobWorker>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddHttpClient<OutboundSender>(http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddScoped<IntakeService>();

var app = builder.Build();

if (app.Services.GetService<MigrationRunner>() is { } runner)
    await runner.ApplyAsync();

// First, so error pages and everything after them see the real scheme and client address.
if (trustProxy) app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseStatusCodePagesWithReExecute("/error", "?code={0}");
}

app.UseRequestLocalization();
app.UseStaticFiles();
app.UseRouting();
app.MapControllers();
app.MapControllerRoute(name: "default", pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();

static string ResolveMigrationsDir(string contentRoot)
{
    // Published/container layout: migrations copied next to the app. Dev layout: repo-root/db/migrations.
    foreach (var candidate in new[] { Path.Combine(contentRoot, "db", "migrations"), Path.Combine(contentRoot, "..", "..", "db", "migrations") })
        if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
    throw new DirectoryNotFoundException("db/migrations not found.");
}

public partial class Program;
