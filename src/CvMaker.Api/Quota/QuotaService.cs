using CvMaker.Api.Data;
using CvMaker.Shared;
using Microsoft.EntityFrameworkCore;

namespace CvMaker.Api.Quota;

public sealed record QuotaOptions
{
    /// <summary>Length of a quota period. Rolling from first use, not calendar months.</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromDays(30);

    public int FreeTailoredGenerations { get; init; } = 3;
    public int ProTailoredGenerations { get; init; } = 100;
}

public enum QuotaOutcome
{
    Allowed,
    Exhausted
}

/// <summary>
/// Metering, owned by cv-maker rather than by the identity service.
///
/// authservice issues tokens and knows nothing about CVs or what a generation
/// costs; charging for a new kind of action should not need a change to a
/// service this repository does not own. Keeping identity there and metering
/// here means the thing that is genuinely cv-maker's concern lives in cv-maker.
///
/// It also makes the deferred refund-by-cause policy
/// (docs/decisions/0004-quota-and-refunds.md) a local change if the
/// consumed-but-failed numbers ever justify building it.
/// </summary>
public sealed class QuotaService(AppDbContext db, QuotaOptions options, ILogger<QuotaService> logger)
{
    /// <summary>
    /// Atomically consumes one tailored generation.
    ///
    /// The consume is a single conditional UPDATE — <c>used &lt; limit</c> in the
    /// WHERE clause, allowed only when it affects a row. Read-then-write would
    /// race two concurrent requests into one allowance and hand out a free
    /// generation; the database decides instead.
    /// </summary>
    public async Task<QuotaOutcome> TryConsumeAsync(string userId, CancellationToken ct)
    {
        var quota = await EnsureCurrentPeriodAsync(userId, ct);

        var updated = await db.UserQuotas
            .Where(q => q.Id == quota.Id && q.TailoredUsed < q.TailoredLimit)
            .ExecuteUpdateAsync(u => u.SetProperty(q => q.TailoredUsed, q => q.TailoredUsed + 1), ct);

        if (updated == 1) return QuotaOutcome.Allowed;

        logger.LogInformation("Quota exhausted for {UserId}", userId);
        return QuotaOutcome.Exhausted;
    }

    public async Task<QuotaDto> GetAsync(string userId, CancellationToken ct)
    {
        var quota = await EnsureCurrentPeriodAsync(userId, ct);

        return new QuotaDto(
            quota.Tier,
            quota.TailoredUsed,
            quota.TailoredLimit,
            Math.Max(0, quota.TailoredLimit - quota.TailoredUsed),
            quota.PeriodStart.Add(options.Period));
    }

    /// <summary>
    /// Returns the caller's quota row, creating it on first use and rolling the
    /// period over when it has expired.
    ///
    /// Rolling from first use rather than on calendar months: a user who signs
    /// up on the 30th should not get a fresh allowance the next day.
    /// </summary>
    private async Task<UserQuotaEntity> EnsureCurrentPeriodAsync(string userId, CancellationToken ct)
    {
        var quota = await db.UserQuotas.FirstOrDefaultAsync(q => q.UserId == userId, ct);

        if (quota is null)
        {
            quota = new UserQuotaEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Tier = QuotaTier.Free,
                TailoredLimit = options.FreeTailoredGenerations,
                TailoredUsed = 0,
                PeriodStart = DateTime.UtcNow
            };

            db.UserQuotas.Add(quota);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two first requests raced. The unique index on UserId settles
                // it; re-read the winner rather than failing the request.
                db.Entry(quota).State = EntityState.Detached;
                quota = await db.UserQuotas.FirstAsync(q => q.UserId == userId, ct);
            }
        }

        if (DateTime.UtcNow - quota.PeriodStart >= options.Period)
        {
            quota.PeriodStart = DateTime.UtcNow;
            quota.TailoredUsed = 0;
            await db.SaveChangesAsync(ct);
        }

        return quota;
    }
}
