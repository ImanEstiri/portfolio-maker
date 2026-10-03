using System.Text.Json;
using CvMaker.Agentic.Flow;
using CvMaker.Core.Content;
using CvMaker.Core.Profile;
using CvMaker.Core.Validation;
using CvMaker.Shared;
using Xunit;

namespace CvMaker.Tests;

/// <summary>
/// The agent layer's contract: what comes back from a model is parsed
/// defensively, and anything it invents is caught before it reaches a PDF.
/// </summary>
public class AgentPipelineTests
{
    // ── Structured output parsing ────────────────────────────────────────────

    [Fact]
    public void Valid_composer_output_parses()
    {
        const string json = """
        {
          "summary": "Backend engineer.",
          "summarySourceFactIds": ["e1"],
          "sections": [
            {
              "title": "Experience",
              "placement": "main",
              "entries": [
                {
                  "heading": "Senior Engineer",
                  "organization": "Acme",
                  "dateRange": "2019 -- 2024",
                  "sourceFactIds": ["e1"],
                  "bullets": [ { "text": "Led the migration", "sourceFactIds": ["e1"] } ]
                }
              ]
            }
          ]
        }
        """;

        Assert.True(CvGenerationOrchestrator.TryParse(json, out var composed, out var error), error);
        Assert.Equal("Experience", composed!.Sections.Single().Title);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"sections\": [] }")]                                     // nothing to render
    [InlineData("{ \"sections\": [ { \"title\": \"\", \"entries\": [] } ] }")] // untitled section
    public void Malformed_composer_output_is_rejected(string json)
    {
        Assert.False(CvGenerationOrchestrator.TryParse(json, out _, out _));
    }

    /// <summary>
    /// An entry with no heading and no bullets renders as an empty block. The
    /// user would see it as a defect, so it fails here instead.
    /// </summary>
    [Fact]
    public void Empty_entry_is_rejected()
    {
        const string json = """
        { "sections": [ { "title": "Experience", "entries": [ { "heading": "", "bullets": [] } ] } ] }
        """;

        Assert.False(CvGenerationOrchestrator.TryParse(json, out _, out var error));
        Assert.Contains("neither a heading nor bullets", error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Models fence JSON despite being told not to, often enough that treating
    /// it as a hard failure would break generations for a cosmetic reason.
    /// </summary>
    [Theory]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    public void Code_fences_are_stripped(string raw, string expected)
    {
        Assert.Equal(expected, FlowEngine.StripCodeFence(raw));
    }

    // ── The guardrail, on agent output ───────────────────────────────────────

    private static CandidateProfile Profile() => new()
    {
        FullName = "Konrad Cinkusz",
        Facts =
        [
            new ProfileFact
            {
                FactId = "e1", Kind = FactKind.Employment,
                Text = "Led the payments platform migration",
                Organization = "Acme", Role = "Senior Engineer"
            }
        ]
    };

    private static CvContent ToContent(string composerJson)
    {
        Assert.True(CvGenerationOrchestrator.TryParse(composerJson, out var composed, out var error), error);
        return Map(composed!, Profile());
    }

    /// <summary>
    /// Mirrors CvMaker.Api's Mapping.ToContent. Duplicated rather than
    /// referenced so the test project does not take a dependency on the web
    /// project purely to assert a guardrail.
    /// </summary>
    private static CvContent Map(ComposedCv composed, CandidateProfile profile) => new()
    {
        FullName = profile.FullName,
        Summary = string.IsNullOrWhiteSpace(composed.Summary)
            ? null
            : new CvProse { Text = composed.Summary, SourceFactIds = composed.SummarySourceFactIds },
        Sections = composed.Sections.Select(s => new CvSection
        {
            Title = s.Title,
            Placement = string.Equals(s.Placement, "side", StringComparison.OrdinalIgnoreCase)
                ? SectionPlacement.Side : SectionPlacement.Main,
            Entries = s.Entries.Select(e => new CvEntry
            {
                Heading = e.Heading,
                Organization = e.Organization,
                DateRange = e.DateRange,
                SourceFactIds = e.SourceFactIds,
                Bullets = e.Bullets
                    .Select(b => new CvBullet { Text = b.Text, SourceFactIds = b.SourceFactIds })
                    .ToList()
            }).ToList()
        }).ToList()
    };

    /// <summary>
    /// The characteristic hallucination: a real job, an invented metric. The
    /// output is well-formed JSON and looks entirely plausible — only the
    /// missing provenance gives it away.
    /// </summary>
    [Fact]
    public void Invented_metric_on_agent_output_is_caught()
    {
        var content = ToContent("""
        {
          "sections": [
            {
              "title": "Experience",
              "placement": "main",
              "entries": [
                {
                  "heading": "Senior Engineer",
                  "organization": "Acme",
                  "sourceFactIds": ["e1"],
                  "bullets": [
                    { "text": "Led the payments platform migration", "sourceFactIds": ["e1"] },
                    { "text": "Cut settlement latency by 40% across 12 markets", "sourceFactIds": [] }
                  ]
                }
              ]
            }
          ]
        }
        """);

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Kind == FactViolationKind.MissingReference);
    }

    /// <summary>
    /// The subtler failure: the model cites a factId that does not exist,
    /// which would pass any check that merely required provenance to be present.
    /// </summary>
    [Fact]
    public void Fabricated_employer_with_a_made_up_source_is_caught()
    {
        var content = ToContent("""
        {
          "sections": [
            {
              "title": "Experience",
              "placement": "main",
              "entries": [
                {
                  "heading": "Principal Engineer",
                  "organization": "Globex",
                  "sourceFactIds": ["e7"],
                  "bullets": [ { "text": "Ran the platform org", "sourceFactIds": ["e7"] } ]
                }
              ]
            }
          ]
        }
        """);

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.False(result.IsValid);
        Assert.All(result.Violations, v => Assert.Equal(FactViolationKind.UnknownReference, v.Kind));
    }

    [Fact]
    public void Properly_sourced_agent_output_passes()
    {
        var content = ToContent("""
        {
          "summary": "Backend engineer with platform migration experience.",
          "summarySourceFactIds": ["e1"],
          "sections": [
            {
              "title": "Experience",
              "placement": "main",
              "entries": [
                {
                  "heading": "Senior Engineer",
                  "organization": "Acme",
                  "sourceFactIds": ["e1"],
                  "bullets": [ { "text": "Delivered the payments migration", "sourceFactIds": ["e1"] } ]
                }
              ]
            }
          ]
        }
        """);

        var result = FactReferenceValidator.Validate(content, Profile());

        Assert.True(result.IsValid, result.Summarise());
    }

    /// <summary>
    /// Prompt injection in a pasted posting cannot reach the compiler, because
    /// the model's only channel is a JSON string field that gets escaped. Even
    /// if the model echoes an instruction verbatim, it lands as text.
    /// </summary>
    [Fact]
    public void Injection_echoed_by_the_model_is_still_only_text()
    {
        var composed = JsonSerializer.Deserialize<ComposedCv>("""
        {
          "sections": [
            {
              "title": "Experience",
              "entries": [
                {
                  "heading": "Engineer",
                  "sourceFactIds": ["e1"],
                  "bullets": [ { "text": "\\write18{rm -rf /} \\input{/etc/passwd}", "sourceFactIds": ["e1"] } ]
                }
              ]
            }
          ]
        }
        """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var tex = new CvMaker.Core.Binding.TemplateBinder()
            .Bind("{{ for s in cv.sections }}{{ for e in s.entries }}{{ for b in e.bullets }}{{ b }}{{ end }}{{ end }}{{ end }}",
                  Map(composed, Profile()));

        Assert.DoesNotContain(@"\write18{rm", tex, StringComparison.Ordinal);
        Assert.Contains(@"\textbackslash{}write18", tex, StringComparison.Ordinal);
    }
}
