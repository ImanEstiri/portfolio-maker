using CvMaker.Api.Data;
using CvMaker.Api.Templates;
using CvMaker.Core.Content;
using CvMaker.Core.Profile;
using CvMaker.Shared;

namespace CvMaker.Api;

public static class Mapping
{
    public static CandidateProfile ToDomain(ProfileEntity e) => new()
    {
        FullName = e.FullName,
        Headline = e.Headline,
        Email = e.Email,
        Phone = e.Phone,
        Location = e.Location,
        Links = e.Links,
        Facts = e.Facts.Select(f => new ProfileFact
        {
            FactId = f.FactId,
            Kind = ParseKind(f.Kind),
            Text = f.Text,
            Organization = f.Organization,
            Role = f.Role,
            Location = f.Location,
            StartDate = f.StartDate,
            EndDate = f.EndDate
        }).ToList()
    };

    /// <summary>
    /// Unknown kinds map to <see cref="FactKind.Other"/> rather than throwing:
    /// a stored profile must keep loading after the enum gains a value, and a
    /// mis-typed kind should degrade to a generic section, not break someone's
    /// CV.
    /// </summary>
    public static FactKind ParseKind(string? kind) =>
        Enum.TryParse<FactKind>(kind, ignoreCase: true, out var parsed) ? parsed : FactKind.Other;

    public static ProfileDto ToDto(this ProfileEntity e) => new(
        e.Id, e.FullName, e.Headline, e.Email, e.Phone, e.Location,
        e.Links,
        e.Facts.Select(f => new ProfileFactDto(
            f.FactId, f.Kind, f.Text, f.Organization, f.Role, f.Location, f.StartDate, f.EndDate)).ToList(),
        e.UpdatedAt);

    public static TemplateDto ToDto(this TemplateManifest m) => new(
        m.Id, m.DisplayName, m.Description, m.Engine, m.AtsRating,
        m.Capabilities.TwoColumn, m.PageBudget.Target);

    public static JobDto ToDto(this GenerationJobEntity j) => new(
        j.Id, j.DocumentId, j.Status, j.Stage, j.Progress, j.Error, j.FailureCause,
        j.CreatedAt, j.CompletedAt,
        j.Status == JobStatus.Completed ? $"/api/jobs/{j.Id}/pdf" : null,
        ParseReview(j.AtsReviewJson));

    /// <summary>
    /// The review is advisory, so a stored value that no longer deserialises
    /// must not break the job it belongs to.
    /// </summary>
    private static AtsReview? ParseReview(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<AtsReview>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Converts agent output into the binder's content model.
    ///
    /// Provenance is carried across verbatim rather than being regenerated or
    /// defaulted — the whole point is that FactReferenceValidator gets to see
    /// exactly what the model claimed, and reject it if the sources do not
    /// resolve against the profile.
    /// </summary>
    public static CvContent ToContent(ComposedCv composed, CandidateProfile profile) => new()
    {
        FullName = profile.FullName,
        Headline = profile.Headline,
        Email = profile.Email,
        Phone = profile.Phone,
        Location = profile.Location,
        Links = profile.Links,

        Summary = string.IsNullOrWhiteSpace(composed.Summary)
            ? null
            : new CvProse
            {
                Text = composed.Summary,
                SourceFactIds = composed.SummarySourceFactIds
            },

        Sections = composed.Sections.Select(s => new CvSection
        {
            Title = s.Title,
            // Unknown placement falls back to the main column: a section in the
            // wrong column is a layout annoyance, a dropped section is data loss.
            Placement = string.Equals(s.Placement, "side", StringComparison.OrdinalIgnoreCase)
                ? SectionPlacement.Side
                : SectionPlacement.Main,
            Entries = s.Entries.Select(e => new CvEntry
            {
                Heading = e.Heading,
                Organization = e.Organization,
                Location = e.Location,
                DateRange = e.DateRange,
                SourceFactIds = e.SourceFactIds,
                Bullets = e.Bullets
                    .Select(b => new CvBullet { Text = b.Text, SourceFactIds = b.SourceFactIds })
                    .ToList()
            }).ToList()
        }).ToList()
    };

    public static ProfileFactDto ToDto(this ProfileFactEntity f) => new(
        f.FactId, f.Kind, f.Text, f.Organization, f.Role, f.Location, f.StartDate, f.EndDate);
}
