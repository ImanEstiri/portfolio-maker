using CvMaker.Api.Data;
using CvMaker.Api.Jobs;
using CvMaker.Api.Services.Storage;
using CvMaker.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvMaker.Api.Endpoints;

public static class JobsEndpoints
{
    public static RouteGroupBuilder MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs");

        group.MapGet("/", ListAsync).WithName("ListJobs");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetJob");
        group.MapGet("/{id:guid}/pdf", GetPdfAsync).WithName("GetJobPdf");
        group.MapDelete("/{id:guid}", CancelAsync).WithName("CancelJob");

        return group;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db, HttpContext http, CancellationToken ct, int page = 1, int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);

        var userId = http.UserId();
        var jobs = await db.GenerationJobs
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            // PdfBlobPath is an internal storage key, not something a list
            // response should leak. Projected into an anonymous type rather
            // than the entity — EF Core rejects projecting into a mapped
            // entity type.
            .Select(j => new JobRow(
                j.Id, j.DocumentId, j.Status, j.Stage, j.Progress,
                j.Error, j.FailureCause, j.CreatedAt, j.CompletedAt, j.AtsReviewJson))
            .ToListAsync(ct);

        return Results.Ok(jobs.Select(j => j.ToDto()));
    }

    private static async Task<IResult> GetAsync(
        Guid id, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = http.UserId();
        var job = await db.GenerationJobs
            .Where(j => j.Id == id && j.UserId == userId)
            .Select(j => new JobRow(
                j.Id, j.DocumentId, j.Status, j.Stage, j.Progress,
                j.Error, j.FailureCause, j.CreatedAt, j.CompletedAt, j.AtsReviewJson))
            .FirstOrDefaultAsync(ct);

        return job is null ? Results.NotFound() : Results.Ok(job.ToDto());
    }

    private static async Task<IResult> GetPdfAsync(
        Guid id, AppDbContext db, IArtifactStorageService storage, HttpContext http, CancellationToken ct)
    {
        var userId = http.UserId();
        var job = await db.GenerationJobs
            .Where(j => j.Id == id && j.UserId == userId)
            .Select(j => new { j.Status, j.PdfBlobPath })
            .FirstOrDefaultAsync(ct);

        if (job is null) return Results.NotFound();

        if (job.Status != JobStatus.Completed || job.PdfBlobPath is null)
            return Results.NotFound(new { error = "The PDF is not ready yet." });

        var file = await storage.DownloadFileAsync(job.PdfBlobPath, ct);
        if (file is null)
            return Results.NotFound(new { error = "The PDF could not be retrieved from storage." });

        return Results.File(file.Value.Stream, file.Value.ContentType, $"cv-{id}.pdf");
    }

    private static async Task<IResult> CancelAsync(
        Guid id, AppDbContext db, JobCancellationRegistry registry, HttpContext http, CancellationToken ct)
    {
        var userId = http.UserId();
        var job = await db.GenerationJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId, ct);
        if (job is null) return Results.NotFound();

        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            return Results.Conflict(new { error = $"Job is already {job.Status}." });

        // Signals the worker if it holds this job; a queued-but-not-started job
        // is marked here and skipped when it is picked up.
        registry.Cancel(id);

        job.Status = JobStatus.Cancelled;
        job.Stage = "cancelled";
        job.FailureCause = FailureCause.Cancelled;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}

/// <summary>
/// Projection target for job queries, so the Pdf column is never loaded when
/// only status is wanted. A plain record rather than the entity: EF Core does
/// not allow projecting into a mapped entity type.
/// </summary>
internal sealed record JobRow(
    Guid Id,
    Guid DocumentId,
    JobStatus Status,
    string Stage,
    double Progress,
    string? Error,
    FailureCause FailureCause,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? AtsReviewJson)
{
    public JobDto ToDto() => new(
        Id, DocumentId, Status, Stage, Progress, Error, FailureCause,
        CreatedAt, CompletedAt,
        Status == JobStatus.Completed ? $"/api/jobs/{Id}/pdf" : null,
        ParseReview(AtsReviewJson));

    private static AtsReview? ParseReview(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<AtsReview>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
