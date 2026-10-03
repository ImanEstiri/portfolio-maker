namespace CvMaker.Core.Content;

/// <summary>
/// A composed, tailored CV — the structured output of the agent flow and the
/// only thing a template is ever bound to.
///
/// Note what is absent: there is no field here that holds LaTeX, markup, or
/// formatting of any kind. A model that wants bold text cannot ask for it.
/// That is deliberate — the moment a model can influence markup, the escaping
/// boundary in <see cref="Latex.LatexEscaper"/> stops being a boundary.
/// </summary>
public sealed record CvContent
{
    public required string FullName { get; init; }
    public string? Headline { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Location { get; init; }

    public IReadOnlyDictionary<string, string> Links { get; init; } =
        new Dictionary<string, string>();

    /// <summary>The tailored professional summary, with the facts it draws on.</summary>
    public CvProse? Summary { get; init; }

    public IReadOnlyList<CvSection> Sections { get; init; } = [];
}

/// <summary>Free prose with its provenance — a summary, a cover-letter paragraph.</summary>
public sealed record CvProse
{
    public required string Text { get; init; }
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
}

/// <summary>
/// Which column a section belongs in, for templates that have more than one.
///
/// This is a layout <i>hint</i>, not layout itself — still no markup in the
/// content model. Single-column templates ignore it entirely; the two-column
/// AltaCV template renders <see cref="Main"/> in the wide column and
/// <see cref="Side"/> in the narrow one. The composer is told the template's
/// capabilities (template.json <c>twoColumn</c>) and sets it accordingly, so
/// short tag-like sections such as Skills do not end up in the wide column
/// wasting a third of the page.
/// </summary>
public enum SectionPlacement
{
    Main,
    Side
}

public sealed record CvSection
{
    public required string Title { get; init; }
    public SectionPlacement Placement { get; init; } = SectionPlacement.Main;
    public IReadOnlyList<CvEntry> Entries { get; init; } = [];
}

/// <summary>
/// One position, degree or project. The header fields carry the load-bearing
/// claims — employer, title, dates — so they cite facts of their own rather
/// than inheriting trust from the bullets underneath them.
/// </summary>
public sealed record CvEntry
{
    public required string Heading { get; init; }
    public string? Organization { get; init; }
    public string? Location { get; init; }
    public string? DateRange { get; init; }

    /// <summary>
    /// Facts backing the header itself. An entry claiming "Senior Engineer,
    /// Acme, 2019–2024" must point at the fact that says so.
    /// </summary>
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];

    public IReadOnlyList<CvBullet> Bullets { get; init; } = [];
}

public sealed record CvBullet
{
    public required string Text { get; init; }

    /// <summary>
    /// The facts this bullet is a rewording of. Empty is a validation failure,
    /// not a default — see <see cref="Validation.FactReferenceValidator"/>.
    /// </summary>
    public IReadOnlyList<string> SourceFactIds { get; init; } = [];
}
