using CvMaker.Core.Content;
using CvMaker.Core.Profile;
using CvMaker.Core.Validation;
using Xunit;

namespace CvMaker.Tests;

/// <summary>
/// The no-invented-facts gate. Every case here is a fabrication that would
/// otherwise reach a PDF the candidate then sends to an employer.
/// </summary>
public class FactReferenceValidatorTests
{
    private static CandidateProfile Profile() => new()
    {
        FullName = "Konrad Cinkusz",
        Facts =
        [
            new ProfileFact
            {
                FactId = "emp-1",
                Kind = FactKind.Employment,
                Text = "Led the payments platform migration",
                Organization = "Acme",
                Role = "Senior Software Engineer"
            },
            new ProfileFact
            {
                FactId = "emp-1-b1",
                Kind = FactKind.Employment,
                Text = "Reduced deployment time from hours to minutes"
            },
            new ProfileFact
            {
                FactId = "skill-1",
                Kind = FactKind.Skill,
                Text = "C#, .NET, PostgreSQL"
            }
        ]
    };

    private static CvContent ContentWith(CvBullet bullet, IReadOnlyList<string>? entryRefs = null) => new()
    {
        FullName = "Konrad Cinkusz",
        Sections =
        [
            new CvSection
            {
                Title = "Experience",
                Entries =
                [
                    new CvEntry
                    {
                        Heading = "Senior Software Engineer",
                        Organization = "Acme",
                        SourceFactIds = entryRefs ?? ["emp-1"],
                        Bullets = [bullet]
                    }
                ]
            }
        ]
    };

    [Fact]
    public void Content_traceable_to_facts_is_valid()
    {
        var content = ContentWith(new CvBullet
        {
            Text = "Led the migration of the payments platform",
            SourceFactIds = ["emp-1"]
        });

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.True(result.IsValid, result.Summarise());
    }

    /// <summary>
    /// The characteristic failure: a model turns a qualitative claim into a
    /// quantitative one. The number is invented, so it cites nothing.
    /// </summary>
    [Fact]
    public void Invented_metric_with_no_provenance_is_rejected()
    {
        var content = ContentWith(new CvBullet
        {
            Text = "Drove a 40% reduction in settlement latency across 12 markets",
            SourceFactIds = []
        });

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Kind == FactViolationKind.MissingReference);
    }

    [Fact]
    public void Reference_to_a_fact_that_does_not_exist_is_rejected()
    {
        var content = ContentWith(new CvBullet
        {
            Text = "Managed a team of 15 engineers",
            SourceFactIds = ["emp-9-invented"]
        });

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(FactViolationKind.UnknownReference, violation.Kind);
        Assert.Equal("emp-9-invented", violation.FactId);
    }

    /// <summary>
    /// Employer, title and dates are the claims a reference check actually
    /// verifies, so the entry header is validated on its own rather than
    /// inheriting trust from the bullets beneath it.
    /// </summary>
    [Fact]
    public void Entry_header_needs_its_own_provenance()
    {
        var content = ContentWith(
            new CvBullet { Text = "Led the migration", SourceFactIds = ["emp-1"] },
            entryRefs: []);

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v =>
            v.Kind == FactViolationKind.MissingReference && v.Path.EndsWith("entries[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsourced_summary_is_rejected()
    {
        var content = new CvContent
        {
            FullName = "Konrad Cinkusz",
            Summary = new CvProse
            {
                Text = "Award-winning engineer with 20 years of experience.",
                SourceFactIds = []
            }
        };

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Path == "summary");
    }

    [Fact]
    public void Blank_reference_counts_as_unknown()
    {
        var content = ContentWith(new CvBullet
        {
            Text = "Something plausible",
            SourceFactIds = ["   "]
        });

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Kind == FactViolationKind.UnknownReference);
    }

    /// <summary>
    /// All violations are collected rather than short-circuiting: the critic
    /// pass needs the full list to repair in one round instead of discovering
    /// them one model call at a time.
    /// </summary>
    [Fact]
    public void All_violations_are_reported_together()
    {
        var content = new CvContent
        {
            FullName = "Konrad Cinkusz",
            Sections =
            [
                new CvSection
                {
                    Title = "Experience",
                    Entries =
                    [
                        new CvEntry
                        {
                            Heading = "Staff Engineer",
                            SourceFactIds = ["nope-1"],
                            Bullets =
                            [
                                new CvBullet { Text = "Invented one", SourceFactIds = [] },
                                new CvBullet { Text = "Invented two", SourceFactIds = ["nope-2"] },
                                new CvBullet { Text = "Real one",     SourceFactIds = ["emp-1-b1"] }
                            ]
                        }
                    ]
                }
            ]
        };

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.Equal(3, result.Violations.Count);
    }

    [Fact]
    public void Empty_content_is_vacuously_valid()
    {
        var result = FactReferenceValidator.Validate(
            new CvContent { FullName = "Konrad Cinkusz" }, Profile());

        Assert.True(result.IsValid);
    }
}
