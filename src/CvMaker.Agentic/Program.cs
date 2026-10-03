using CvMaker.Agentic.Agents;
using CvMaker.Agentic.Ai;
using CvMaker.Agentic.Flow;
using CvMaker.Shared;

var builder = WebApplication.CreateBuilder(args);

// ── Catalogues (data, read once on first use — a malformed definition fails there) ──
builder.Services.AddSingleton(sp => new AgentCatalog(
    builder.Configuration.GetValue("Agents:Root", "/app/agents")!,
    sp.GetRequiredService<ILogger<AgentCatalog>>()));

builder.Services.AddSingleton(sp => new FlowCatalog(
    builder.Configuration.GetValue("Flows:Root", "/app/flows")!,
    sp.GetRequiredService<ILogger<FlowCatalog>>()));

// ── Model backend ────────────────────────────────────────────────────────────
// Foundry when an endpoint is configured, the deterministic mock otherwise.
// The selection is logged loudly: silently serving mock CVs in an environment
// somebody believed was live would be the worst possible failure mode here.
builder.Services.AddSingleton<IAgentRuntime>(sp =>
{
    var foundry = new FoundryAgentRuntime(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<FoundryAgentRuntime>>());

    if (foundry.IsConfigured) return foundry;

    var logger = sp.GetRequiredService<ILogger<Program>>();
    if (!builder.Environment.IsDevelopment())
    {
        logger.LogError(
            "No Azure AI Foundry endpoint configured outside Development — serving MOCK content. " +
            "Set AzureAIFoundry:Endpoint.");
    }
    else
    {
        logger.LogInformation("No Foundry endpoint configured; using the mock runtime.");
    }

    return new MockAgentRuntime(sp.GetRequiredService<ILogger<MockAgentRuntime>>());
});

builder.Services.AddSingleton<FlowEngine>();
builder.Services.AddSingleton<CvGenerationOrchestrator>();

builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

// Job postings are pasted pages; generous but bounded.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/api/compose/cv", async (
    ComposeRequest request,
    CvGenerationOrchestrator orchestrator,
    CancellationToken ct) =>
{
    var result = await orchestrator.ComposeAsync(request, ct);

    // Returned as 200 with a failure body rather than a problem response: the
    // caller is the API's worker, which needs the FailureCause to classify the
    // job, and mapping causes onto status codes would lose that distinction.
    return Results.Ok(result);
});

app.MapGet("/api/agents", (AgentCatalog catalog) =>
    Results.Ok(catalog.All.Select(a => new { a.Slug, a.DisplayName, a.Version, a.Model })));

// Reports whether real model calls are being made. Without this, a
// misconfigured deployment looks identical to a working one until someone
// reads a CV and finds mock text in it.
app.MapGet("/api/runtime", (IAgentRuntime runtime) =>
    Results.Ok(new { configured = runtime.IsConfigured, mode = runtime.IsConfigured ? "foundry" : "mock" }));

app.MapHealthChecks("/health");

// Liveness, separate from /health, per the architecture standard's checklist. /health can
// depend on downstream state; /alive answers only "this process is serving", which is the
// question a restart policy needs.
app.MapGet("/alive", () => Results.Ok(new { status = "alive" }));

app.Run();

public partial class Program;
