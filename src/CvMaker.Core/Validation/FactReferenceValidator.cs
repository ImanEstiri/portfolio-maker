using CvMaker.Core.Content;
using CvMaker.Core.Profile;

namespace CvMaker.Core.Validation;

public enum FactViolationKind
{
    /// <summary>Content carries no provenance at all.</summary>
    MissingReference,

    /// <summary>Content cites a fact id that is not in the profile — a fabrication, or a hallucinated id.</summary>
    UnknownReference
}

public sealed record FactViolation(
    FactViolationKind Kind,
    string Path,
    string Excerpt,
    string? FactId = null)
{
    public override string ToString() => Kind switch
    {
        FactViolationKind.MissingReference =>
            $"{Path}: no sourceFactIds — \"{Excerpt}\"",
        FactViolationKind.UnknownReference =>
            $"{Path}: unknown fact '{FactId}' — \"{Excerpt}\"",
        _ => $"{Path}: {Kind}"
    };
}

public sealed record FactValidationResult(IReadOnlyList<FactViolation> Violations)
{
    public bool IsValid => Violations.Count == 0;

    public string Summarise() => IsValid
        ? "All content is traceable to profile facts."
        : string.Join(Environment.NewLine, Violations.Select(v => v.ToString()));
}

/// <summary>
/// Enforces the citation rule behind "no invented facts": <b>every summary,
/// entry heading and bullet on a generated CV must cite at least one fact id,
/// and every cited id must exist in the candidate's profile.</b>
///
/// A model rewording "led the payments migration" into "drove a 40% reduction
/// in settlement latency" has invented a metric. On a report that is a quality
/// problem; on a CV it is a claim the candidate may have to defend in an
/// interview, or that gets an offer withdrawn after a reference check. The
/// harm lands on the user, who did not write it and may not notice it.
///
/// So the pipeline fails closed. Content whose provenance does not resolve is
/// rejected and the generation fails with a real error, rather than shipping a
/// plausible PDF with an uncited claim in it.
///
/// What this does not do is compare a claim with the fact it cites. An entry's
/// organisation, location and dates, and a bullet's wording, are checked for
/// having a citation and nothing else, so the invented 40% above passes if the
/// bullet cites the payments-migration fact. The composer's prompt forbids
/// invented employers, titles, dates and numbers; this class cannot enforce that.
/// </summary>
public static class FactReferenceValidator
{
    public static FactValidationResult Validate(CvContent content, CandidateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(profile);

        var known = profile.FactIds;
        var violations = new List<FactViolation>();

        if (content.Summary is { } summary)
        {
            Check(summary.SourceFactIds, "summary", summary.Text, known, violations);
        }

        for (var s = 0; s < content.Sections.Count; s++)
        {
            var section = content.Sections[s];

            for (var e = 0; e < section.Entries.Count; e++)
            {
                var entry = section.Entries[e];
                var entryPath = $"sections[{s}].entries[{e}]";

                // The header carries the employer/title/date claims, so it has to
                // carry its own citation rather than leaning on its bullets'.
                Check(entry.SourceFactIds, entryPath, entry.Heading, known, violations);

                for (var b = 0; b < entry.Bullets.Count; b++)
                {
                    var bullet = entry.Bullets[b];
                    Check(
                        bullet.SourceFactIds,
                        $"{entryPath}.bullets[{b}]",
                        bullet.Text,
                        known,
                        violations);
                }
            }
        }

        return new FactValidationResult(violations);
    }

    private static void Check(
        IReadOnlyList<string> refs,
        string path,
        string text,
        IReadOnlySet<string> known,
        List<FactViolation> violations)
    {
        // An empty reference list is the default a model falls into when it
        // invents something, so it has to be a failure rather than a pass.
        if (refs.Count == 0)
        {
            violations.Add(new FactViolation(
                FactViolationKind.MissingReference, path, Excerpt(text)));
            return;
        }

        foreach (var id in refs)
        {
            if (string.IsNullOrWhiteSpace(id) || !known.Contains(id))
            {
                violations.Add(new FactViolation(
                    FactViolationKind.UnknownReference, path, Excerpt(text), id));
            }
        }
    }

    private const int ExcerptLength = 80;

    private static string Excerpt(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= ExcerptLength ? flat : flat[..ExcerptLength] + "…";
    }
}
