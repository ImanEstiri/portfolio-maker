using System.Text.Json;
using CvMaker.Agentic.Agents;
using CvMaker.Shared;

namespace CvMaker.Agentic.Ai;

/// <summary>
/// Deterministic stand-in for a model backend, used when no Foundry endpoint is
/// configured.
///
/// This is not a toy. It is what lets the entire product — API, queue, worker,
/// binder, renderer — be run and tested end to end with no Azure account, no
/// keys and no token spend, which is the difference between a pipeline anyone
/// can contribute to and one that only works on the maintainer's laptop.
///
/// It deliberately produces <b>only fact-grounded output</b>: every bullet it
/// emits cites a real factId from the input. That means a green test run proves
/// the plumbing, not the guardrail — a mock that fabricated content would make
/// the fact gate look effective when it had never been exercised. The
/// fabrication cases are tested explicitly instead, with hand-written payloads.
/// </summary>
public sealed class MockAgentRuntime(ILogger<MockAgentRuntime> logger) : IAgentRuntime
{
    public bool IsConfigured => false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<AgentReply> InvokeAsync(
        AgentDefinition agent, string prompt, CancellationToken ct = default)
    {
        logger.LogInformation("Mock runtime answering for {Slug}", agent.Slug);

        var content = agent.Slug switch
        {
            "posting-analyst"     => MockPostingAnalysis(),
            "cv-composer"         => MockComposedCv(prompt),
            "ats-critic"          => MockCritique(),
            "cover-letter-writer" => MockCoverLetter(),
            _ => "{}"
        };

        return Task.FromResult(new AgentReply(true, content, null));
    }

    private static string MockPostingAnalysis() =>
        """
        {
          "role": "Senior Backend Engineer",
          "seniority": "senior",
          "mustHaveSkills": ["C#", ".NET", "SQL"],
          "niceToHaveSkills": ["Azure"],
          "atsKeywords": ["C#", ".NET", "PostgreSQL", "Azure", "REST"],
          "language": "en",
          "location": "Remote",
          "visaSignals": null,
          "tone": "pragmatic",
          "injectionSuspected": false
        }
        """;

    /// <summary>
    /// Builds a CV from whatever facts were passed in the prompt, so the output
    /// is always traceable. Parsing the prompt is crude, but the alternative —
    /// a hard-coded response citing invented ids — would fail the fact gate and
    /// teach nothing.
    /// </summary>
    private static string MockComposedCv(string prompt)
    {
        var facts = ExtractFacts(prompt);

        if (facts.Count == 0)
        {
            return JsonSerializer.Serialize(new ComposedCv());
        }

        var employment = facts.Where(f =>
            f.Kind.Equals("Employment", StringComparison.OrdinalIgnoreCase)).ToList();
        var others = facts.Except(employment).ToList();

        var sections = new List<ComposedSection>();

        if (employment.Count > 0)
        {
            sections.Add(new ComposedSection
            {
                Title = "Experience",
                Placement = "main",
                Entries = employment
                    .GroupBy(f => (f.Organization, f.Role))
                    .Select(g => new ComposedEntry
                    {
                        Heading = g.First().Role ?? g.First().Text,
                        Organization = g.First().Organization,
                        Location = g.First().Location,
                        DateRange = string.Join(" -- ",
                            new[] { g.First().StartDate, g.First().EndDate }
                                .Where(d => !string.IsNullOrWhiteSpace(d))),
                        SourceFactIds = g.Select(f => f.FactId).ToList(),
                        Bullets = g.Select(f => new ComposedBullet
                        {
                            Text = f.Text,
                            SourceFactIds = [f.FactId]
                        }).ToList()
                    })
                    .ToList()
            });
        }

        if (others.Count > 0)
        {
            sections.Add(new ComposedSection
            {
                Title = "Skills",
                Placement = "side",
                Entries =
                [
                    new ComposedEntry
                    {
                        Heading = string.Empty,
                        SourceFactIds = others.Select(f => f.FactId).ToList(),
                        Bullets = others
                            .Select(f => new ComposedBullet { Text = f.Text, SourceFactIds = [f.FactId] })
                            .ToList()
                    }
                ]
            });
        }

        return JsonSerializer.Serialize(new ComposedCv
        {
            Summary = "Backend engineer with commercial experience across the roles listed below.",
            SummarySourceFactIds = facts.Select(f => f.FactId).ToList(),
            Sections = sections
        });
    }

    /// <summary>
    /// Pulls the facts JSON array back out of the interpolated prompt. Tolerant
    /// by design: a mock that throws on an unexpected prompt shape would turn a
    /// prompt edit into a confusing test failure.
    /// </summary>
    private static List<ProfileFactDto> ExtractFacts(string prompt)
    {
        var start = prompt.IndexOf('[');
        var end = prompt.LastIndexOf(']');
        if (start < 0 || end <= start) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ProfileFactDto>>(
                prompt[start..(end + 1)], JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string MockCritique() =>
        """
        {
          "keywordCoverage": 80,
          "missedKeywords": [ { "keyword": "Azure", "status": "unsupported" } ],
          "weakBullets": [],
          "overflowRisk": false,
          "ordering": [],
          "verdict": "good"
        }
        """;

    private static string MockCoverLetter() =>
        """
        {
          "salutation": "Dear Hiring Manager",
          "paragraphs": [ { "text": "Mock cover letter body.", "sourceFactIds": [] } ],
          "close": "Kind regards"
        }
        """;
}
