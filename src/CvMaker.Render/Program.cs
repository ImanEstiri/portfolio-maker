using CvMaker.Render;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(_ => new LatexCompilerOptions
{
    Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Render:TimeoutSeconds", 30)),
    WorkRoot = builder.Configuration.GetValue("Render:WorkRoot", "/tmp/cvmaker-render")!
});
builder.Services.AddSingleton<LatexCompiler>();

builder.Services.AddHealthChecks();

// The renderer is an internal service — only CvMaker.Api calls it — so the
// request body cap can be tight. Bound LaTeX for a three-page CV is tens of KB.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4 * 1024 * 1024);

var app = builder.Build();

app.MapPost("/render", async (
    CompileRequest request,
    LatexCompiler compiler,
    CancellationToken ct) =>
{
    var result = await compiler.CompileAsync(request, ct);

    if (!result.Success)
    {
        // The log is deliberately not returned: it carries absolute paths and
        // package versions. It is logged server-side instead.
        return Results.Problem(
            title: "Typesetting failed",
            detail: result.Error,
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    return Results.File(result.Pdf!, "application/pdf", "document.pdf");
});

app.MapHealthChecks("/health");

// Liveness only. /ready below compiles a real document and is the one a deploy should
// wait on; this one must stay cheap, because a restart policy polls it.
app.MapGet("/alive", () => Results.Ok(new { status = "alive" }));

// Readiness is not the same as liveness here: the process can be up while the
// TeX installation is broken or missing, which is exactly what a bad image
// build produces. Compiling a minimal document is the only honest check.
app.MapGet("/ready", async (LatexCompiler compiler, CancellationToken ct) =>
{
    const string probe = @"\documentclass{article}\begin{document}ok\end{document}";
    var result = await compiler.CompileAsync(new CompileRequest(probe), ct);

    return result.Success
        ? Results.Ok(new { status = "ready", ms = result.Duration.TotalMilliseconds })
        : Results.Problem(
            title: "TeX installation is not usable",
            detail: result.Error,
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
