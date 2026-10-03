using CvMaker.Core.Content;
using CvMaker.Core.Profile;
using CvMaker.Core.Validation;
using Xunit;

namespace CvMaker.Tests;

public class ProfileContentBuilderTests
{
    private static CandidateProfile Profile(params ProfileFact[] facts) => new()
    {
        FullName = "Konrad Cinkusz",
        Headline = "Senior Software Engineer",
        Facts = facts
    };

    private static ProfileFact Fact(
        string id, FactKind kind, string text,
        string? org = null, string? role = null, string? start = null, string? end = null) =>
        new()
        {
            FactId = id, Kind = kind, Text = text,
            Organization = org, Role = role, StartDate = start, EndDate = end
        };

    /// <summary>
    /// The property that makes the no-AI path safe by construction: content
    /// copied from the user's own facts always satisfies the provenance gate,
    /// so the gate runs on the real pipeline rather than being bypassed.
    /// </summary>
    [Fact]
    public void Built_content_always_passes_the_fact_gate()
    {
        var profile = Profile(
            Fact("e1", FactKind.Employment, "Led the payments migration", "Acme", "Senior Engineer", "2019", "2024"),
            Fact("e2", FactKind.Employment, "Cut deploy time to minutes", "Acme", "Senior Engineer", "2019", "2024"),
            Fact("s1", FactKind.Skill, "C#, .NET, PostgreSQL"),
            Fact("d1", FactKind.Education, "BSc Computer Science", "University", "BSc", "2012", "2015"));

        var content = ProfileContentBuilder.Build(profile);
        var result = FactReferenceValidator.Validate(content, profile);

        Assert.True(result.IsValid, result.Summarise());
    }

    [Fact]
    public void Facts_for_one_position_collapse_into_a_single_entry()
    {
        var profile = Profile(
            Fact("e1", FactKind.Employment, "Led the payments migration", "Acme", "Senior Engineer", "2019", "2024"),
            Fact("e2", FactKind.Employment, "Cut deploy time to minutes", "Acme", "Senior Engineer", "2019", "2024"));

        var content = ProfileContentBuilder.Build(profile);

        var section = Assert.Single(content.Sections);
        var entry = Assert.Single(section.Entries);
        Assert.Equal("Senior Engineer", entry.Heading);
        Assert.Equal("Acme", entry.Organization);
        Assert.Equal("2019 -- 2024", entry.DateRange);
        Assert.Equal(2, entry.Bullets.Count);
    }

    [Fact]
    public void Different_positions_stay_separate()
    {
        var profile = Profile(
            Fact("e1", FactKind.Employment, "Led the migration", "Acme", "Senior Engineer", "2019", "2024"),
            Fact("e2", FactKind.Employment, "Built the API", "Globex", "Engineer", "2015", "2019"));

        var content = ProfileContentBuilder.Build(profile);

        var section = Assert.Single(content.Sections);
        Assert.Equal(2, section.Entries.Count);
    }

    [Fact]
    public void Short_list_sections_go_to_the_side_column()
    {
        var profile = Profile(
            Fact("e1", FactKind.Employment, "Led the migration", "Acme", "Senior Engineer"),
            Fact("s1", FactKind.Skill, "C#"),
            Fact("s2", FactKind.Skill, "PostgreSQL"),
            Fact("l1", FactKind.Language, "English (C1)"));

        var content = ProfileContentBuilder.Build(profile);

        var experience = content.Sections.Single(s => s.Title == "Experience");
        var skills = content.Sections.Single(s => s.Title == "Skills");
        var languages = content.Sections.Single(s => s.Title == "Languages");

        Assert.Equal(SectionPlacement.Main, experience.Placement);
        Assert.Equal(SectionPlacement.Side, skills.Placement);
        Assert.Equal(SectionPlacement.Side, languages.Placement);

        // Side sections are flat: one entry holding every item as a bullet.
        var skillEntry = Assert.Single(skills.Entries);
        Assert.Equal(2, skillEntry.Bullets.Count);
    }

    [Fact]
    public void Sections_are_ordered_experience_first()
    {
        var profile = Profile(
            Fact("s1", FactKind.Skill, "C#"),
            Fact("d1", FactKind.Education, "BSc", "Uni", "BSc"),
            Fact("e1", FactKind.Employment, "Led it", "Acme", "Engineer"));

        var titles = ProfileContentBuilder.Build(profile).Sections.Select(s => s.Title).ToList();

        Assert.Equal(new[] { "Experience", "Education", "Skills" }, titles);
    }

    /// <summary>
    /// A model is the only thing allowed to write a summary, and only with
    /// provenance. Synthesising one here would be the exact failure the fact
    /// gate exists to prevent.
    /// </summary>
    [Fact]
    public void No_summary_is_invented()
    {
        var content = ProfileContentBuilder.Build(
            Profile(Fact("e1", FactKind.Employment, "Led it", "Acme", "Engineer")));

        Assert.Null(content.Summary);
    }

    [Fact]
    public void Contact_details_carry_over()
    {
        var profile = Profile() with
        {
            Email = "someone@example.com",
            Phone = "+48 000 000 000",
            Location = "Poland",
            Links = new Dictionary<string, string> { ["GitHub"] = "https://github.com/konradcinkusz" }
        };

        var content = ProfileContentBuilder.Build(profile);

        Assert.Equal("someone@example.com", content.Email);
        Assert.Equal("Poland", content.Location);
        Assert.Single(content.Links);
    }

    [Fact]
    public void A_profile_with_no_facts_produces_no_sections()
    {
        Assert.Empty(ProfileContentBuilder.Build(Profile()).Sections);
    }

    [Fact]
    public void Open_ended_dates_render_without_a_dangling_separator()
    {
        var profile = Profile(
            Fact("e1", FactKind.Employment, "Still here", "Acme", "Engineer", start: "2019"));

        var entry = ProfileContentBuilder.Build(profile).Sections.Single().Entries.Single();

        Assert.Equal("2019", entry.DateRange);
    }
}
