using CvMaker.Api.Data;
using CvMaker.Api.Quota;
using CvMaker.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace CvMaker.Api.Endpoints;

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me").WithTags("Me");

        group.MapGet("/quota", async (QuotaService quota, HttpContext http, CancellationToken ct) =>
                Results.Ok(await quota.GetAsync(http.UserId(), ct)))
             .WithName("GetMyQuota");

        group.MapGet("/data", SummariseAsync).WithName("SummariseMyData");
        group.MapDelete("/data", PurgeAsync).WithName("PurgeMyData");

        return group;
    }

    /// <summary>
    /// What this service holds about the caller: the transparency half of data
    /// protection. The sign-in account lives in authservice and is not counted
    /// here (docs/ARCHITECTURE.md, "Retention and erasure").
    /// </summary>
    private static async Task<IResult> SummariseAsync(
        AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = http.UserId();

        return Results.Ok(new
        {
            profiles = await db.Profiles.CountAsync(p => p.UserId == userId, ct),
            jobPostings = await db.JobPostings.CountAsync(p => p.UserId == userId, ct),
            documents = await db.Documents.CountAsync(d => d.UserId == userId, ct),
            generatedPdfs = await db.GenerationJobs
                .CountAsync(j => j.UserId == userId && j.PdfBlobPath != null, ct)
        });
    }

    /// <summary>
    /// Deletes everything this service holds for the caller.
    ///
    /// authservice owns account deletion and knows nothing about CVs, so closing
    /// an account is only complete when both halves run. Only this half is wired:
    /// nothing in CvMaker.Web calls authservice's account deletion yet, so erasing
    /// here leaves the login in place. That is the safe way round — a login with
    /// no data behind it, never someone's CVs left in the database with no login
    /// to reach them — and it is the order any future wiring has to keep: this
    /// endpoint first, the account only if it succeeded.
    ///
    /// <para>The durable version is an authservice webhook or a reconciler rather
    /// than a client-side sequence. It is not built (README, "Known gaps").</para>
    ///
    /// PDFs live in blob storage, not the database, so the transaction below
    /// only ever covers the rows. The blobs are deleted afterwards — a purge
    /// that leaves the artifacts behind is precisely the failure this endpoint
    /// exists to prevent, but a blob store has no part in a SQL transaction, so
    /// that half is necessarily best-effort.
    /// </summary>
    private static async Task<IResult> PurgeAsync(
        AppDbContext db, IArtifactStorageService storage, HttpContext http, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var userId = http.UserId();
        var logger = loggerFactory.CreateLogger("DataPurge");

        // Captured before the rows go, since ExecuteDeleteAsync does not
        // return the deleted values.
        var pdfBlobPaths = await db.GenerationJobs
            .Where(j => j.UserId == userId && j.PdfBlobPath != null)
            .Select(j => j.PdfBlobPath!)
            .ToListAsync(ct);

        // One transaction: a partial purge is the outcome worth ruling out.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var jobs = await db.GenerationJobs.Where(j => j.UserId == userId).ExecuteDeleteAsync(ct);
        var documents = await db.Documents.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
        var postings = await db.JobPostings.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        // Facts cascade from the profile.
        var profiles = await db.Profiles.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);

        // The quota row is deliberately NOT deleted. It holds no content — a
        // counter and a period start — and removing it would make "delete my
        // data" a free way to reset the monthly allowance, which is a
        // straightforward way to get unlimited generations.
        //
        // It is retained on the legitimate-interest basis of abuse prevention,
        // and it becomes deletable when the account itself goes: that is the
        // same orphan the client-orchestrated deletion note below describes.

        await tx.CommitAsync(ct);

        foreach (var blobPath in pdfBlobPaths)
            await storage.DeleteFileAsync(blobPath, ct);

        // Deletion is an auditable event; the user id is already the only
        // identifier here and nothing about the content is logged.
        logger.LogInformation(
            "Purged data for {UserId}: {Profiles} profile(s), {Postings} posting(s), " +
            "{Documents} document(s), {Jobs} job(s), {Blobs} PDF blob(s)",
            userId, profiles, postings, documents, jobs, pdfBlobPaths.Count);

        return Results.Ok(new { profiles, postings, documents, jobs });
    }
}
