using CvMaker.Core.Profile;

namespace CvMaker.Core.Content;

/// <summary>
/// Builds <see cref="CvContent"/> directly from a hand-entered profile, with no
/// model involved.
///
/// This is the untailored route — a PDF from your own data, with no posting, no
/// model and no quota. It is deliberately not a fallback for a failed tailoring:
/// the worker fails that job instead, so nobody sends a generic CV believing it
/// was tailored.
///
/// It also gets provenance right for free: every bullet cites the fact it was
/// copied from, so content the user typed themselves passes
/// <see cref="Validation.FactReferenceValidator"/> by construction rather than
/// by exemption. The gate is exercised on the real pipeline from day one
/// instead of being switched off for the non-AI route.
/// </summary>
public static class ProfileContentBuilder
{
    /// <summary>
    /// Section title and column for each kind of fact. Side-column sections are
    /// the short, list-like ones; anything with dates and bullets needs the
    /// wide column. Single-column templates ignore the placement.
    /// </summary>
    private static readonly Dictionary<FactKind, (string Title, SectionPlacement Placement, int Order)> SectionMap = new()
    {
        [FactKind.Employment]    = ("Experience",     SectionPlacement.Main, 0),
        [FactKind.Project]       = ("Projects",       SectionPlacement.Main, 1),
        [FactKind.Education]     = ("Education",      SectionPlacement.Main, 2),
        [FactKind.Publication]   = ("Publications",   SectionPlacement.Main, 3),
        [FactKind.Skill]         = ("Skills",         SectionPlacement.Side, 4),
        [FactKind.Certification] = ("Certifications", SectionPlacement.Side, 5),
        [FactKind.Language]      = ("Languages",      SectionPlacement.Side, 6),
        [FactKind.Award]         = ("Awards",         SectionPlacement.Side, 7),
        [FactKind.Other]         = ("Other",          SectionPlacement.Side, 8)
    };

    public static CvContent Build(CandidateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var sections = profile.Facts
            .GroupBy(f => f.Kind)
            .OrderBy(g => SectionMap.TryGetValue(g.Key, out var m) ? m.Order : int.MaxValue)
            .Select(BuildSection)
            .Where(s => s.Entries.Count > 0)
            .ToList();

        return new CvContent
        {
            FullName = profile.FullName,
            Headline = profile.Headline,
            Email = profile.Email,
            Phone = profile.Phone,
            Location = profile.Location,
            Links = profile.Links,
            // No summary is synthesised here. Inventing one would be exactly the
            // thing the fact gate exists to prevent, and the user can write
            // their own headline.
            Summary = null,
            Sections = sections
        };
    }

    private static CvSection BuildSection(IGrouping<FactKind, ProfileFact> group)
    {
        var (title, placement, _) = SectionMap.TryGetValue(group.Key, out var m)
            ? m
            : (group.Key.ToString(), SectionPlacement.Side, int.MaxValue);

        return new CvSection
        {
            Title = title,
            Placement = placement,
            Entries = placement == SectionPlacement.Side
                ? BuildListEntries(group)
                : BuildDatedEntries(group)
        };
    }

    /// <summary>
    /// Groups facts that describe the same position into one entry, so three
    /// bullets about one job render as one <c>\cvevent</c> with three items
    /// rather than the same employer repeated three times.
    /// </summary>
    private static List<CvEntry> BuildDatedEntries(IEnumerable<ProfileFact> facts) =>
        facts
            .GroupBy(f => (f.Organization, f.Role, f.StartDate, f.EndDate))
            .Select(g =>
            {
                var first = g.First();
                return new CvEntry
                {
                    Heading = first.Role ?? first.Text,
                    Organization = first.Organization,
                    Location = first.Location,
                    DateRange = FormatDates(first.StartDate, first.EndDate),
                    SourceFactIds = g.Select(f => f.FactId).ToList(),
                    // When Role is absent the fact's own text became the
                    // heading, so repeating it as a bullet would be noise.
                    Bullets = g
                        .Where(f => f.Role is not null || !ReferenceEquals(f, first))
                        .Select(f => new CvBullet
                        {
                            Text = f.Text,
                            SourceFactIds = [f.FactId]
                        })
                        .ToList()
                };
            })
            .ToList();

    /// <summary>
    /// Side-column sections are flat lists: one entry, no heading, one bullet
    /// per fact.
    /// </summary>
    private static List<CvEntry> BuildListEntries(IEnumerable<ProfileFact> facts)
    {
        var items = facts.ToList();
        if (items.Count == 0) return [];

        return
        [
            new CvEntry
            {
                Heading = string.Empty,
                SourceFactIds = items.Select(f => f.FactId).ToList(),
                Bullets = items
                    .Select(f => new CvBullet { Text = f.Text, SourceFactIds = [f.FactId] })
                    .ToList()
            }
        ];
    }

    private static string? FormatDates(string? start, string? end) => (start, end) switch
    {
        (null or "", null or "") => null,
        (var s, null or "") => s,
        (null or "", var e) => e,
        var (s, e) => $"{s} -- {e}"
    };
}
