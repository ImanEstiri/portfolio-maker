using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CvMaker.Api.Data;
using CvMaker.Api.Rendering;
using CvMaker.Api.Services.Storage;
using CvMaker.Api.Templates;
using CvMaker.Core.Binding;
using CvMaker.Core.Content;
using CvMaker.Core.Profile;
using CvMaker.Core.Validation;
using CvMaker.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvMaker.Api.Jobs;

/// <summary>
/// Drives a generation from queued to PDF.
///
/// Ported from <c>black-hole-sim</c>'s RenderWorker, including its two hard
/// parts: recovering jobs that were in flight when the process last stopped,
/// and a cancellation registry so a user can abandon a running job.
///
/// Pipeline: profile → content → fact gate → bind → render → store. Content
/// comes from <c>ProfileContentBuilder</c>, or from the agent flow when the
/// document has a posting; everything downstream is the same, which is the
/// point of the content model being the interface.
/// </summary>
public sealed class GenerationWorker(
    IGenerationJobQueue queue,
    JobCancellationRegistry cancelRegistry,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<GenerationWorker> logger) : BackgroundService
{
    /// <summary>
    /// How many generations may be in flight at once.
    ///
    /// Most of a job's wall-clock is spent waiting on the renderer and the agent plane, both
    /// of which are separate apps — so running one at a time left this process idle for
    /// almost all of it while users queued behind a job that was doing nothing locally.
    ///
    /// Not unbounded: each in-flight job holds a DI scope, a DbContext and a rendered PDF in
    /// memory, and the renderer is the actual scarce resource. Two is a floor that helps
    /// noticeably without pretending this process is the bottleneck; raise it with
    /// <c>Jobs:MaxConcurrency</c> once the renderer is scaled to match.
    /// </summary>
    private int MaxConcurrency =>
        Math.Max(1, configuration.GetValue("Jobs:MaxConcurrency", 2));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = MaxConcurrency;
        logger.LogInformation("Generation worker started with concurrency {Concurrency}", concurrency);

        // Recovery runs alongside the consumer loop rather than before it.
        //
        // It enqueues into the same bounded channel this loop drains, so doing it first
        // deadlocks the moment there are more interrupted jobs than the channel's capacity:
        // the write blocks waiting for a reader that has not started yet, and the reader is
        // the statement after it. The failure needed 100 recoverable jobs to appear, so it
        // would have surfaced during an incident rather than before one.
        var recovery = RecoverStaleJobsAsync(stoppingToken);

        using var slots = new SemaphoreSlim(concurrency, concurrency);
        var running = new ConcurrentDictionary<Task, byte>();

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            await slots.WaitAsync(stoppingToken);

            var task = Task.Run(async () =>
            {
                try
                {
                    await ProcessJobAsync(jobId, stoppingToken);
                }
                finally
                {
                    slots.Release();
                }
            }, stoppingToken);

            running[task] = 0;
            _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
        }

        // Shutdown: let whatever is mid-flight finish writing its status row, so a stopping
        // host does not leave a job stuck at Running for the next process to recover.
        await Task.WhenAll(running.Keys);
        await recovery;
    }

    /// <summary>
    /// The queue is in-memory, so a restart loses anything queued. Rows in
    /// Pending or Running are the durable record of that work; re-enqueue them
    /// rather than leaving jobs that never finish and a UI that polls forever.
    /// </summary>
    private async Task RecoverStaleJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var stale = await db.GenerationJobs
                .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
                .Select(j => j.Id)
                .ToListAsync(ct);

            if (stale.Count == 0) return;

            await db.GenerationJobs
                .Where(j => j.Status == JobStatus.Running)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.Status, JobStatus.Pending)
                    .SetProperty(e => e.Progress, 0.0)
                    .SetProperty(e => e.Stage, "queued")
                    .SetProperty(e => e.FailureCause, FailureCause.WorkerRestart), ct);

            foreach (var id in stale)
                await queue.EnqueueAsync(id, ct);

            logger.LogInformation("Recovered {Count} interrupted generation job(s)", stale.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down before recovery finished. The rows are still Pending, so the next
            // process picks them up — that is the whole point of them being durable.
        }
        catch (Exception ex)
        {
            // This now runs unawaited alongside the consumer loop, so an exception here would
            // otherwise surface as an unobserved task rather than a log line — and taking the
            // host down over failed recovery would turn a recoverable state into a crash loop.
            logger.LogError(ex, "Could not recover interrupted generation jobs");
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<TemplateCatalog>();
        var renderer = scope.ServiceProvider.GetRequiredService<RenderClient>();
        var binder = scope.ServiceProvider.GetRequiredService<TemplateBinder>();
        var storage = scope.ServiceProvider.GetRequiredService<IArtifactStorageService>();

        var job = await db.GenerationJobs.FindAsync([jobId], stoppingToken);
        if (job is null)
        {
            logger.LogWarning("Job {JobId} vanished before processing", jobId);
            return;
        }

        // Cancelled while it sat in the queue. DELETE /api/jobs/{id} writes the row and calls
        // the registry, but the registry only knows about jobs already running — so for a
        // queued job the write was the only thing that happened, and nothing read it. The job
        // then ran to completion and produced a PDF the user had already said they did not
        // want, having been told it was cancelled.
        if (job.Status == JobStatus.Cancelled)
        {
            logger.LogInformation("Job {JobId} was cancelled before it started; skipping", jobId);
            return;
        }

        using var cts = cancelRegistry.Register(jobId, stoppingToken);
        var ct = cts.Token;

        // Registering and reading the row are not atomic: a cancellation landing between the
        // check above and the registration would set the row and find no registry entry. Both
        // orderings are covered by re-reading after registering.
        await db.Entry(job).ReloadAsync(stoppingToken);
        if (job.Status == JobStatus.Cancelled)
        {
            logger.LogInformation("Job {JobId} was cancelled while starting; skipping", jobId);
            return;
        }

        try
        {
            job.Status = JobStatus.Running;
            job.Stage = "composing";
            job.Progress = 0.1;
            // Cleared as the run begins. Recovery stamps WorkerRestart on interrupted jobs to
            // record why they were requeued; leaving it set meant a job that was recovered and
            // then succeeded still read as WorkerRestart forever, which is exactly the signal
            // the refund instrumentation counts.
            job.FailureCause = FailureCause.None;
            await db.SaveChangesAsync(ct);

            var document = await db.Documents.FindAsync([job.DocumentId], ct);
            if (document is null)
            {
                await FailAsync(db, job, "Document no longer exists.", FailureCause.BindingFailed, ct);
                return;
            }

            var template = catalog.Find(document.TemplateId);
            if (template is null)
            {
                await FailAsync(db, job,
                    $"Template '{document.TemplateId}' is not in the catalogue.",
                    FailureCause.TemplateNotFound, ct);
                return;
            }

            var profileEntity = await db.Profiles
                .Include(p => p.Facts)
                .FirstOrDefaultAsync(p => p.Id == document.ProfileId, ct);

            if (profileEntity is null)
            {
                await FailAsync(db, job, "Profile no longer exists.", FailureCause.BindingFailed, ct);
                return;
            }

            var profile = Mapping.ToDomain(profileEntity);

            CvContent content;

            if (document.PostingId is { } postingId)
            {
                var posting = await db.JobPostings.FindAsync([postingId], ct);
                if (posting is null)
                {
                    await FailAsync(db, job, "The job posting no longer exists.",
                        FailureCause.BindingFailed, ct);
                    return;
                }

                job.Stage = "tailoring";
                job.Progress = 0.25;
                await db.SaveChangesAsync(ct);

                var agentic = scope.ServiceProvider.GetRequiredService<AgenticClient>();
                var template2 = catalog.Find(document.TemplateId)!;

                var composed = await agentic.ComposeAsync(new ComposeRequest(
                    posting.RawText,
                    profileEntity.Facts.Select(f => f.ToDto()).ToList(),
                    TemplateCapabilities: template2.Capabilities.TwoColumn
                        ? "two columns; place short list-like sections in the side column"
                        : "single column",
                    PageBudget: template2.PageBudget.Target), ct);

                if (!composed.Success || composed.Content is null)
                {
                    // Deliberately not falling back to the untailored CV. The
                    // user asked for a document tailored to a posting; quietly
                    // handing them a generic one they might send without
                    // noticing is worse than failing.
                    await FailAsync(db, job, composed.Error ?? "Tailoring failed.", composed.Cause, ct);
                    return;
                }

                foreach (var line in composed.StepLog)
                    logger.LogInformation("Job {JobId} flow: {Step}", jobId, line);

                if (composed.Review is { } review)
                {
                    job.AtsScore = review.KeywordCoverage;
                    job.AtsReviewJson = System.Text.Json.JsonSerializer.Serialize(review);
                }

                content = Mapping.ToContent(composed.Content, profile);
            }
            else
            {
                content = ProfileContentBuilder.Build(profile);
            }

            // The load-bearing check. On the hand-entered path this passes by
            // construction; on the agent path it is the thing standing between
            // a model's invention and a PDF the user sends to an employer.
            // Same validator, same profile, no exemption for either route.
            var validation = FactReferenceValidator.Validate(content, profile);
            if (!validation.IsValid)
            {
                logger.LogWarning("Fact validation failed for job {JobId}: {Summary}",
                    jobId, validation.Summarise());
                await FailAsync(db, job,
                    "Generated content could not be traced to your profile and was rejected.",
                    FailureCause.FactValidationFailed, ct);
                return;
            }

            job.Stage = "typesetting";
            job.Progress = 0.6;
            await db.SaveChangesAsync(ct);

            string tex;
            try
            {
                tex = binder.Bind(template.CvTemplateSource, content);
            }
            catch (TemplateBindingException ex)
            {
                logger.LogError(ex, "Binding failed for template {Template}", template.Id);
                await FailAsync(db, job, "Could not build the document.", FailureCause.BindingFailed, ct);
                return;
            }

            job.ContentHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(tex))).ToLowerInvariant();

            var outcome = await renderer.RenderAsync(tex, template.Engine, template.AssetContents, ct);

            if (outcome.Pdf is null)
            {
                await FailAsync(db, job, outcome.Error ?? "Typesetting failed.", outcome.Cause, ct);
                return;
            }

            job.Stage = "uploading";
            job.Progress = 0.9;
            await db.SaveChangesAsync(ct);

            var pdfBlobPath = await storage.UploadBinaryFileAsync(
                job.UserId, jobId.ToString(), $"cv-{jobId}.pdf", outcome.Pdf, "application/pdf", ct);

            if (pdfBlobPath is null)
            {
                // A PDF nobody can download is not a completed job — fail it
                // rather than reporting success and 404-ing on every download.
                await FailAsync(db, job, "Could not save the generated PDF.", FailureCause.StorageFailure, ct);
                return;
            }

            job.PdfBlobPath = pdfBlobPath;
            job.Status = JobStatus.Completed;
            job.Stage = "done";
            job.Progress = 1.0;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Job {JobId} produced a {Bytes} byte PDF, stored at {BlobPath}", jobId, outcome.Pdf.Length, pdfBlobPath);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // User cancelled. Not a failure, and explicitly not refundable-by-us.
            job.Status = JobStatus.Cancelled;
            job.Stage = "cancelled";
            job.FailureCause = FailureCause.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A worker that dies takes the whole host with it
            // (BackgroundServiceExceptionBehavior defaults to StopHost), so an
            // unexpected failure on one job must not escape.
            logger.LogError(ex, "Unhandled failure processing job {JobId}", jobId);
            await FailAsync(db, job, "An unexpected error occurred.", FailureCause.RenderCrashed,
                CancellationToken.None);
        }
        finally
        {
            cancelRegistry.Release(jobId);
        }
    }

    private static async Task FailAsync(
        AppDbContext db, GenerationJobEntity job, string error, FailureCause cause, CancellationToken ct)
    {
        job.Status = JobStatus.Failed;
        job.Stage = "failed";
        job.Error = error;
        job.FailureCause = cause;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
