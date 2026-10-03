using CvMaker.Api.Data;
using CvMaker.Api.Quota;
using CvMaker.Api.Jobs;
using CvMaker.Api.Templates;
using CvMaker.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvMaker.Api.Endpoints;

public static class DocumentsEndpoints
{
    public static void MapTemplatesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/templates", (TemplateCatalog catalog) =>
                Results.Ok(catalog.All
                    .OrderBy(t => t.DisplayName, StringComparer.Ordinal)
                    .Select(t => t.ToDto())))
           .WithName("ListTemplates")
           .WithTags("Templates");
    }

    public static RouteGroupBuilder MapDocumentsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").WithTags("Documents");

        group.MapPost("/", CreateAsync)
             .WithName("CreateDocument")
             .RequireRateLimiting("generate");

        return group;
    }

    /// <summary>
    /// Accepts the work and returns immediately with a job to poll.
    ///
    /// 202 rather than a synchronous render: even the untailored path runs a
    /// LaTeX compile, and the tailored path adds up to four sequential model
    /// calls, so a generation can take tens of seconds.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateDocumentRequest request,
        AppDbContext db,
        TemplateCatalog catalog,
        IGenerationJobQueue queue,
        QuotaService quota,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = http.UserId();

        if (catalog.Find(request.TemplateId) is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["templateId"] = [$"Unknown template '{request.TemplateId}'."]
            });

        if (request.Kind != DocumentKind.Cv)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                // Cover letters need their own template file and content shape;
                // failing clearly beats rendering a CV under the wrong name.
                ["kind"] = ["Cover letters are not implemented yet."]
            });

        var profile = await db.Profiles
            .FirstOrDefaultAsync(p => p.Id == request.ProfileId, ct);

        if (profile is null || profile.UserId != userId) return Results.NotFound();

        // The quota gate. Consumed here rather than in the worker because this
        // is before any model call, which is the only ordering that gates
        // spend, and because a failure can still be reported to the caller.
        //
        // Only the tailored path consumes it — that is the one costing tokens.
        // A straight CV from the profile is compute, already covered by the
        // per-user rate limiter.
        if (!string.IsNullOrWhiteSpace(request.PostingText))
        {
            if (await quota.TryConsumeAsync(userId, ct) == QuotaOutcome.Exhausted)
            {
                return Results.Problem(
                    title: "Out of generations",
                    detail: "You have used this month's tailored CVs.",
                    statusCode: StatusCodes.Status402PaymentRequired);
            }
        }

        JobPostingEntity? posting = null;
        if (!string.IsNullOrWhiteSpace(request.PostingText))
        {
            posting = new JobPostingEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RawText = request.PostingText.Trim(),
                CreatedAt = DateTime.UtcNow,
                // Raw pasted text is the shortest-lived data here: it is only
                // needed while the document is generated, and a LinkedIn page
                // can carry third parties' details (docs/ARCHITECTURE.md,
                // "Retention and erasure").
                PurgeAt = DateTime.UtcNow.AddDays(30)
            };
            db.JobPostings.Add(posting);
        }

        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProfileId = request.ProfileId,
            PostingId = posting?.Id,
            TemplateId = request.TemplateId,
            Kind = request.Kind,
            CreatedAt = DateTime.UtcNow
        };

        var job = new GenerationJobEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentId = document.Id,
            Status = JobStatus.Pending,
            Stage = "queued",
            CreatedAt = DateTime.UtcNow
        };

        db.Documents.Add(document);
        db.GenerationJobs.Add(job);

        // Committed before enqueuing: if the process dies between the two, the
        // row is recoverable at startup. Enqueue-first would lose it entirely.
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(job.Id, ct);

        return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
    }
}
