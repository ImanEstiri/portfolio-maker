using System.Text.Json.Serialization;

namespace CvMaker.Shared;

// Every enum on the wire is serialized by name, not by ordinal.
//
// System.Text.Json's default is the ordinal, which made `status` come back as `2` — fine for
// a typed client, useless for anything else. The end-to-end test polled for "Completed",
// never matched a number, and timed out on every run while the pipeline underneath it was
// working correctly. curl, the CI script, the README examples and the issue tracker all
// describe these values by name, so the name is the contract people actually rely on.
//
// It is also the safer contract: FailureCause's values are deliberately sparse (20, 40), and
// a consumer reading `40` learns nothing, while inserting a member would silently change what
// every stored ordinal means.
//
// The attribute rather than a serializer option, deliberately: it travels with the type, so
// the API, the Blazor client and any future consumer agree without either of them configuring
// anything. JsonStringEnumConverter still reads the numeric form, so nothing already
// serialized becomes unreadable.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentKind
{
    Cv,
    CoverLetter
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Where a generation failed. Recorded on every failure so the
/// consumed-but-failed rate can be measured before deciding whether quota
/// refunds are worth building (docs/decisions/0004-quota-and-refunds.md).
///
/// The split is deliberately the same one a refund policy would need: the
/// first group is ours, the second is driven by the input.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FailureCause
{
    None = 0,

    // Ours.
    RenderUnavailable = 1,
    RenderTimeout = 2,
    RenderCrashed = 3,
    StorageFailure = 4,
    WorkerRestart = 5,
    AgentUnavailable = 6,
    AgentTimeout = 7,

    // Driven by the input.
    TemplateNotFound = 20,
    BindingFailed = 21,
    FactValidationFailed = 22,
    SchemaValidationFailed = 23,
    PageBudgetExceeded = 24,

    Cancelled = 40
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuotaTier
{
    Free,
    Pro
}

public sealed record QuotaDto(
    QuotaTier Tier,
    int Used,
    int Limit,
    int Remaining,
    DateTime RenewsAt);

// ── Profiles ─────────────────────────────────────────────────────────────────

public sealed record ProfileFactDto(
    string FactId,
    string Kind,
    string Text,
    string? Organization = null,
    string? Role = null,
    string? Location = null,
    string? StartDate = null,
    string? EndDate = null);

public sealed record ProfileDto(
    Guid Id,
    string FullName,
    string? Headline,
    string? Email,
    string? Phone,
    string? Location,
    IReadOnlyDictionary<string, string> Links,
    IReadOnlyList<ProfileFactDto> Facts,
    DateTime UpdatedAt);

public sealed record UpsertProfileRequest(
    string FullName,
    string? Headline,
    string? Email,
    string? Phone,
    string? Location,
    IReadOnlyDictionary<string, string>? Links,
    IReadOnlyList<ProfileFactDto>? Facts);

// ── Templates ────────────────────────────────────────────────────────────────

public sealed record TemplateDto(
    string Id,
    string DisplayName,
    string Description,
    string Engine,
    string AtsRating,
    bool TwoColumn,
    int TargetPages);

// ── Documents and jobs ───────────────────────────────────────────────────────

/// <summary>
/// Asks for a document built from a profile. Without a posting the content
/// comes straight from the profile's facts; with one, the agent flow tailors it.
/// Both routes produce the same content shape.
/// </summary>
public sealed record CreateDocumentRequest(
    Guid ProfileId,
    string TemplateId,
    DocumentKind Kind = DocumentKind.Cv,

    /// <summary>
    /// A pasted job posting. When present the document is tailored by the agent
    /// pipeline; when absent it is built directly from the profile. A failed
    /// tailoring is not retried as an untailored document.
    /// </summary>
    string? PostingText = null);

public sealed record JobDto(
    Guid Id,
    Guid DocumentId,
    JobStatus Status,
    string Stage,
    double Progress,
    string? Error,
    FailureCause FailureCause,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? PdfUrl,

    /// <summary>The critic's assessment. Null on the untailored path, or when the critique could not be parsed.</summary>
    AtsReview? AtsReview = null);
