namespace CvMaker.Shared;

/// <summary>
/// The structured output contract for <c>cv-composer</c>, and the boundary
/// between the agent layer and everything downstream.
///
/// Note what cannot be expressed here: markup of any kind. A model that wants
/// bold text, a LaTeX macro, or an HTML tag has nowhere to put it. That is the
/// whole reason the agent emits JSON rather than a document — the escaping
/// boundary in CvMaker.Core only holds if model output can never be markup.
/// </summary>
public sealed record ComposedCv
{
    public string? Summary { get; init; }
    public IReadOnlyList<string> SummarySourceFactIds { get; init; } = [];
    public IReadOnlyList<ComposedSection> Sections { get; init; } = [];
}

public sealed record ComposedSection
{
    public string Title { get; init; } = string.Empty;

    /// <summary>"main" or "side". Anything else is treated as "main".</summary>
    public string Placement { get; init; } = "main";

    public IReadOnlyList<ComposedEntry> Entries { get; init; } = [];
}

public sealed record ComposedEntry
{
    public string Heading { get; init; } = string.Empty;
    public string? Organization { get; init; }
    public string? Location { get; init; }
    public string? DateRange { get; init; }
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
    public IReadOnlyList<ComposedBullet> Bullets { get; init; } = [];
}

public sealed record ComposedBullet
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
}

/// <summary>
/// The ats-critic's assessment, surfaced to the user rather than kept internal.
///
/// Showing the score and — more importantly — the keywords the posting wants
/// that the profile cannot honestly support is the difference between a tool
/// that quietly omits things and one that tells you what to go and learn. The
/// unsupported list is deliberately never acted on automatically.
/// </summary>
public sealed record AtsReview
{
    public int KeywordCoverage { get; init; }
    public IReadOnlyList<MissedKeyword> MissedKeywords { get; init; } = [];
    public bool OverflowRisk { get; init; }
    public string Verdict { get; init; } = "good";
}

public sealed record MissedKeyword(string Keyword, string Status);

/// <summary>What the agent service returns for a generation request.</summary>
public sealed record ComposeResult(
    bool Success,
    ComposedCv? Content,
    string? Error,
    FailureCause Cause,
    IReadOnlyList<string> StepLog,
    AtsReview? Review = null);

public sealed record ComposeRequest(
    string PostingText,
    IReadOnlyList<ProfileFactDto> Facts,
    string? TemplateCapabilities = null,
    int PageBudget = 2);
