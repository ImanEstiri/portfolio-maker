using CvMaker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CvMaker.Api.Jobs;

/// <summary>
/// Deletes job postings once their retention window has passed.
///
/// <c>JobPostingEntity.PurgeAt</c> was written on every posting and indexed for exactly this
/// query, and nothing ever ran it. A retention limit that is recorded but not enforced is
/// worse than none: the privacy notice, the schema and the index all state a promise the
/// system was not keeping.
///
/// Postings are the most sensitive thing here and the least useful to keep. They are pasted
/// verbatim, they routinely name a third party — a recruiter, a hiring manager — who never
/// agreed to anything, and they are only needed while a document is being generated from
/// them.
///
/// The document survives the posting it was tailored from. That is deliberate: the PDF is the
/// user's, the advert is not, and <c>DocumentEntity.PostingId</c> is left pointing at a row
/// that no longer exists rather than cascading the deletion outward. Every read of a posting
/// already handles it being gone, because a user can delete one at any time.
/// </summary>
public sealed class PostingPurgeService(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<PostingPurgeService> logger) : BackgroundService
{
    private TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Retention:SweepIntervalMinutes", 60)));

    /// <summary>
    /// Bounded so one sweep cannot turn into a single enormous DELETE against a table the API
    /// is still serving reads from. Anything left over goes on the next pass.
    /// </summary>
    private int BatchSize =>
        Math.Max(1, configuration.GetValue("Retention:PurgeBatchSize", 500));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Job posting purge running every {Interval}", Interval);

        // Runs immediately rather than after the first interval: a deployment that restarts
        // more often than the interval would otherwise never sweep at all.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // An unhandled exception in a BackgroundService stops the whole host by
                // default. A failed sweep is not worth taking the API down for — the rows are
                // still there and the next pass will find them.
                logger.LogError(ex, "Job posting purge failed; will retry at the next interval");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTime.UtcNow;
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            // ExecuteDeleteAsync issues one DELETE rather than loading 60 KB of raw text per
            // row into memory only to throw it away.
            var deleted = await db.JobPostings
                .Where(p => p.PurgeAt <= cutoff)
                .OrderBy(p => p.PurgeAt)
                .Take(BatchSize)
                .ExecuteDeleteAsync(ct);

            total += deleted;

            if (deleted < BatchSize) break;
        }

        if (total > 0)
            logger.LogInformation("Purged {Count} expired job posting(s)", total);
    }
}
