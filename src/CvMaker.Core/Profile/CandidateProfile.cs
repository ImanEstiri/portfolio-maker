namespace CvMaker.Core.Profile;

/// <summary>
/// What kind of thing a <see cref="ProfileFact"/> asserts. Kinds exist so the
/// composer can be told "you may reorder and reword, but a Skill cannot become
/// an Employment" — and so the fact validator can enforce it.
/// </summary>
public enum FactKind
{
    Employment,
    Education,
    Skill,
    Project,
    Publication,
    Certification,
    Language,
    Award,
    Other
}

/// <summary>
/// One atomic, verbatim claim from the candidate's own history — a single
/// bullet, a single job title, a single degree.
///
/// Granularity is the point. Facts are the unit a composed CV bullet has to
/// cite, so they have to be small enough that "this bullet came from that
/// fact" is a meaningful statement. A whole job with six bullets as one fact
/// would let a model attach any invention to it and pass validation.
/// </summary>
public sealed record ProfileFact
{
    /// <summary>
    /// Stable identifier, unique within a profile. Assigned when the fact is
    /// created — by the CRUD form or by <c>profile-extractor</c> — and never
    /// reused, because it is what composed content points at.
    /// </summary>
    public required string FactId { get; init; }

    public required FactKind Kind { get; init; }

    /// <summary>The candidate's own wording. Never overwritten by a model.</summary>
    public required string Text { get; init; }

    public string? Organization { get; init; }
    public string? Role { get; init; }
    public string? Location { get; init; }

    /// <summary>
    /// Free-form on purpose. CV dates are "2019", "Mar 2019", "2019–present"
    /// and "summer 2019" in roughly equal measure; parsing them into
    /// <see cref="DateOnly"/> loses information the candidate chose to convey.
    /// </summary>
    public string? StartDate { get; init; }

    public string? EndDate { get; init; }
}

/// <summary>
/// The canonical, editable record of a candidate. Both entry paths — the CRUD
/// form and pasting a LinkedIn page — produce one of these, so nothing
/// downstream needs to know which was used.
/// </summary>
public sealed record CandidateProfile
{
    public required string FullName { get; init; }
    public string? Headline { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Location { get; init; }

    /// <summary>Label → URL. Rendered only when <see cref="Latex.LatexEscaper.EscapeUrl"/> accepts the target.</summary>
    public IReadOnlyDictionary<string, string> Links { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<ProfileFact> Facts { get; init; } = [];

    /// <summary>
    /// Fact lookup for validation. Ordinal comparison: fact ids are
    /// machine-generated tokens, and culture-sensitive matching on them would
    /// be both slower and occasionally wrong.
    ///
    /// Built fresh on each call rather than memoised in a field. A cache field
    /// would silently join this record's compiler-generated equality — records
    /// compare private fields too — so two identical profiles would stop being
    /// equal as soon as one of them had been validated. Callers hold the result
    /// in a local; profiles are small enough that this is not worth a bug.
    /// </summary>
    public IReadOnlySet<string> FactIds =>
        Facts.Select(f => f.FactId).ToHashSet(StringComparer.Ordinal);
}
