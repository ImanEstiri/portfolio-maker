using System.Text.Json;
using CvMaker.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CvMaker.Api.Data;

public sealed class ProfileEntity
{
    public Guid Id { get; set; }

    /// <summary>
    /// The JWT <c>sub</c> claim, stored as an opaque key. Identity lives in
    /// authservice's own database and is never joined against from here.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;
    public string? Headline { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }

    /// <summary>Label → URL, as JSON. A join table would buy nothing; these are never queried.</summary>
    public Dictionary<string, string> Links { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<ProfileFactEntity> Facts { get; set; } = [];
}

public sealed class ProfileFactEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }

    /// <summary>Stable within a profile; what composed content cites.</summary>
    public string FactId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? Organization { get; set; }
    public string? Role { get; set; }
    public string? Location { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
}

public sealed class JobPostingEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    /// <summary>The pasted posting, verbatim. Untrusted text.</summary>
    public string RawText { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the raw text should be deleted. Postings are the shortest-lived
    /// personal data here — they are only needed while a document is being
    /// generated. <c>PostingPurgeService</c> deletes a row once this has passed
    /// (docs/ARCHITECTURE.md, "Retention and erasure").
    /// </summary>
    public DateTime PurgeAt { get; set; }
}

public sealed class DocumentEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public Guid ProfileId { get; set; }

    /// <summary>Null for the hand-entered path; set when the agent tailored it.</summary>
    public Guid? PostingId { get; set; }
    public string TemplateId { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
}

public sealed class GenerationJobEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public Guid DocumentId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string Stage { get; set; } = "queued";
    public double Progress { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// Set at every failure site. This is the instrumentation the quota-refund
    /// decision was deferred in favour of (docs/decisions/0004-quota-and-refunds.md):
    /// the split between causes we own and causes the input drove is exactly what
    /// a refund policy would key off, so the data is being collected before the
    /// policy is written.
    /// </summary>
    public FailureCause FailureCause { get; set; } = FailureCause.None;

    /// <summary>SHA-256 of the bound .tex, recorded per job. Nothing reads it: there is no render cache.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Keyword coverage from the critic, 0-100. Null on the untailored path.</summary>
    public int? AtsScore { get; set; }

    /// <summary>The critic's full assessment as JSON, shown to the user.</summary>
    public string? AtsReviewJson { get; set; }

    /// <summary>
    /// The blob path in Azure Storage (Services/Storage/IArtifactStorageService),
    /// e.g. <c>{userId}/{jobId}/cv-{jobId}.pdf</c> — not the PDF bytes. CVs are
    /// retained artifacts and do not belong in every database backup long-term.
    /// Null until the upload after render succeeds.
    /// </summary>
    public string? PdfBlobPath { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// A user's metering, owned by this service (see Quota/QuotaService.cs).
/// </summary>
public sealed class UserQuotaEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    public QuotaTier Tier { get; set; } = QuotaTier.Free;

    /// <summary>Tailored generations used in the current period.</summary>
    public int TailoredUsed { get; set; }

    /// <summary>Allowance for the tier. Denormalised so changing a tier's size does not retroactively alter periods in flight.</summary>
    public int TailoredLimit { get; set; }

    public DateTime PeriodStart { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ProfileEntity> Profiles => Set<ProfileEntity>();
    public DbSet<ProfileFactEntity> ProfileFacts => Set<ProfileFactEntity>();
    public DbSet<JobPostingEntity> JobPostings => Set<JobPostingEntity>();
    public DbSet<DocumentEntity> Documents => Set<DocumentEntity>();
    public DbSet<GenerationJobEntity> GenerationJobs => Set<GenerationJobEntity>();
    public DbSet<UserQuotaEntity> UserQuotas => Set<UserQuotaEntity>();

    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProfileEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Headline).HasMaxLength(300);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(64);
            e.Property(x => x.Location).HasMaxLength(200);

            // Serialised explicitly rather than relying on the provider's
            // default handling of Dictionary<string, string> — Npgsql can map
            // one to hstore or to jsonb depending on configuration, and the
            // difference only shows up at runtime against a real database.
            // The comparer is required: without it EF compares the dictionary
            // by reference and silently misses in-place edits.
            e.Property(x => x.Links)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions)
                         ?? new Dictionary<string, string>(),
                    new ValueComparer<Dictionary<string, string>>(
                        (l, r) => l != null && r != null && l.Count == r.Count && !l.Except(r).Any(),
                        v => v.Aggregate(0, (a, kv) => HashCode.Combine(a, kv.Key.GetHashCode(), kv.Value.GetHashCode())),
                        v => new Dictionary<string, string>(v)))
                .HasColumnType("jsonb");

            e.HasIndex(x => x.UserId);
            e.HasMany(x => x.Facts)
             .WithOne()
             .HasForeignKey(f => f.ProfileId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProfileFactEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FactId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Text).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Organization).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.StartDate).HasMaxLength(64);
            e.Property(x => x.EndDate).HasMaxLength(64);
            // Fact ids are cited by generated content, so a duplicate inside one
            // profile would make provenance ambiguous.
            e.HasIndex(x => new { x.ProfileId, x.FactId }).IsUnique();
        });

        b.Entity<JobPostingEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.RawText).HasMaxLength(60_000).IsRequired();
            e.HasIndex(x => x.PurgeAt);
        });

        b.Entity<DocumentEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.TemplateId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<UserQuotaEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            // The atomic consume relies on there being exactly one row per user;
            // without this a race creates two allowances instead of one.
            e.HasIndex(x => x.UserId).IsUnique();
        });

        b.Entity<GenerationJobEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            e.Property(x => x.Stage).HasMaxLength(32);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.PdfBlobPath).HasMaxLength(512);
            e.Property(x => x.AtsReviewJson).HasColumnType("jsonb");
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Status);
        });
    }
}
