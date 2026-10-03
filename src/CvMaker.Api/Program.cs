using System.Threading.RateLimiting;
using CvMaker.Api;
using CvMaker.Api.Auth;
using CvMaker.Api.Data;
using CvMaker.Api.Endpoints;
using CvMaker.Api.Jobs;
using CvMaker.Api.Quota;
using CvMaker.Api.Rendering;
using CvMaker.Api.Services.Storage;
using CvMaker.Api.Templates;
using CvMaker.Core.Binding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ─────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ── Template catalogue (read once, on first use — see TemplateCatalog) ───────
builder.Services.AddSingleton(sp => new TemplateCatalog(
    builder.Configuration.GetValue("Templates:Root", "/app/templates")!,
    sp.GetRequiredService<ILogger<TemplateCatalog>>()));

// ── Domain services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<TemplateBinder>();

// ── Artifact storage (generated PDFs; see docs/ARCHITECTURE.md) ────────────────
// Singleton: the underlying BlobServiceClient is meant to be long-lived, and
// the connection-string branch's CreateIfNotExists() should run once at
// startup, not on every scope.
builder.Services.AddSingleton<IArtifactStorageService, AzureBlobArtifactStorageService>();

// ── Job queue and worker ─────────────────────────────────────────────────────
builder.Services.AddSingleton<IGenerationJobQueue, ChannelGenerationJobQueue>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddHostedService<GenerationWorker>();

// Enforces the 30-day retention window DocumentsEndpoints stamps onto every posting. Without
// it, PurgeAt was a promise written to a column and an index and never acted on.
builder.Services.AddHostedService<PostingPurgeService>();

// ── Renderer ─────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<RenderClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration.GetValue("Render:BaseUrl", "http://render:8080")!);
    // Comfortably above the renderer's own 30 s compile cap, so its structured
    // 422 wins the race and the failure is attributed to the document rather
    // than reported as an unreachable service.
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient<AgenticClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration.GetValue("Agentic:BaseUrl", "http://agentic:8080")!);
    // Up to four sequential model calls (analyse, compose, critique, and a
    // revision when the critic asks). Generous, and still bounded so a wedged
    // backend cannot hold a worker slot indefinitely.
    c.Timeout = TimeSpan.FromMinutes(5);
});

// ── Quota ────────────────────────────────────────────────────────────────────
// Owned here rather than delegated to the identity service: metering is
// cv-maker's concern, and charging for a new kind of action should not need a
// change to a service this repository does not own.
builder.Services.AddSingleton(new QuotaOptions
{
    FreeTailoredGenerations = builder.Configuration.GetValue("Quota:Free", 3),
    ProTailoredGenerations = builder.Configuration.GetValue("Quota:Pro", 100)
});
builder.Services.AddScoped<QuotaService>();

// ── Rate limiting ────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(o =>
{
    // Per user, not per IP: the scarce resources are compute and model tokens,
    // both of which are consumed per account. The monthly quota is a separate
    // gate (QuotaService).
    o.AddPolicy("generate", http => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: http.RateLimitPartitionKey(),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Authentication ───────────────────────────────────────────────────────────
builder.Services.AddCvMakerJwtAuth(builder.Configuration, builder.Environment);

// ── Health ───────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("db")
    // Storage is not an optional dependency for this service: without it every generation
    // reaches "uploading", fails with StorageFailure, and reports a cause two steps removed
    // from the actual problem — which is what a version mismatch with the local emulator
    // looked like for two minutes per attempt. Failing the health check turns that into a
    // stack that refuses to come up and says why.
    .AddCheck<ArtifactStorageHealthCheck>("storage");

builder.Services.AddProblemDetails();

// Registered in every environment: the deployed web app and API are different origins, so
// this is load-bearing in production rather than a local convenience. See CorsExtensions.
builder.Services.AddCvMakerCors(builder.Configuration, builder.Environment);

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);

var app = builder.Build();

// Schema is migrated at startup for the same reason black-hole-sim does:
// GenerationWorker queries GenerationJobs as soon as the host starts, and an
// unhandled exception in a BackgroundService stops the whole host, so a fresh
// database would crash-loop without this running first.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.Logger.LogInformation(
    "CvMaker.Api starting. Environment={Environment}, TokenValidation={TokenValidation}, CorsOrigins={CorsOrigins}",
    app.Environment.EnvironmentName,
    JwtExtensions.DescribeAuthMode(app.Configuration, app.Environment),
    CorsExtensions.DescribeCorsOrigins(app.Configuration, app.Environment));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Before authentication: a preflight OPTIONS carries no credentials, and a 401 on it would
// fail the actual request that follows.
app.UseCors(CorsExtensions.PolicyName);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The template catalogue is public — it is the marketing surface. Everything
// that touches a user's data requires a token, applied to the groups rather than
// per-endpoint so a newly added route inherits it. The one exception is a Development
// process with no token validation configured, where the dev-user header stands in; see
// JwtExtensions.AllowsDevIdentity for why configuring validation ends that regardless of
// the environment name.
app.MapTemplatesEndpoints();

var authenticated = new[]
{
    app.MapProfilesEndpoints(),
    app.MapDocumentsEndpoints(),
    app.MapJobsEndpoints(),
    app.MapMeEndpoints()
};

if (!JwtExtensions.AllowsDevIdentity(app.Configuration, app.Environment))
{
    foreach (var group in authenticated) group.RequireAuthorization();
}

app.MapHealthChecks("/health");
app.MapGet("/alive", () => Results.Ok(new { status = "alive" }));

app.Run();

/// <summary>Exposed so integration tests can drive the API in-process.</summary>
public partial class Program;
