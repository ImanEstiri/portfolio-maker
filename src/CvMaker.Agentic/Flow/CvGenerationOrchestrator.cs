using System.Text.Json;
using CvMaker.Shared;

namespace CvMaker.Agentic.Flow;

/// <summary>
/// Runs the CV generation flow and turns whatever the model said into either a
/// validated <see cref="ComposedCv"/> or a clean failure.
///
/// The parsing here is the second half of the contract described in
/// <see cref="ComposedCv"/>. The prompt asks for a shape; this decides whether
/// the shape arrived. A model is never trusted to honour a schema, so output
/// that does not deserialise fails the generation outright — it never falls
/// through to "let the model try again differently", which is how a pipeline
/// ends up shipping something nobody checked.
///
/// There is no repair pass: a single malformed response fails the generation
/// and still costs the user a quota unit. The flow's one revision step is
/// something else — it runs only when the ATS critic asks for changes to
/// output that already parsed.
/// </summary>
public sealed class CvGenerationOrchestrator(
    FlowCatalog flows,
    FlowEngine engine,
    ILogger<CvGenerationOrchestrator> logger)
{
    public const string FlowSlug = "cv-generation-flow";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ComposeResult> ComposeAsync(ComposeRequest request, CancellationToken ct = default)
    {
        var flow = flows.Find(FlowSlug);
        if (flow is null)
            return Fail($"Flow '{FlowSlug}' is not loaded.", FailureCause.AgentUnavailable, []);

        if (string.IsNullOrWhiteSpace(request.PostingText))
            return Fail("A job posting is required.", FailureCause.SchemaValidationFailed, []);

        if (request.Facts.Count == 0)
            return Fail("The profile has no facts to draw on.", FailureCause.FactValidationFailed, []);

        var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["postingText"] = request.PostingText,
            ["facts"] = JsonSerializer.Serialize(request.Facts, JsonOptions),
            ["templateCapabilities"] = request.TemplateCapabilities ?? "single column, no photo",
            ["pageBudget"] = request.PageBudget.ToString()
        };

        FlowResult result;
        try
        {
            result = await engine.ExecuteAsync(flow, input, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Flow {Slug} threw", FlowSlug);
            return Fail("The generation pipeline failed.", FailureCause.AgentUnavailable, []);
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return Fail(result.Error ?? "The generation pipeline produced no output.",
                FailureCause.AgentUnavailable, result.Log);

        if (TryParse(result.Output, out var composed, out var parseError))
            return new ComposeResult(true, composed, null, FailureCause.None, result.Log,
                ExtractReview(result.StepOutputs, logger));

        logger.LogWarning("Composer output did not match the schema: {Error}", parseError);
        return Fail(
            "The generated content did not match the expected format.",
            FailureCause.SchemaValidationFailed,
            result.Log);
    }

    /// <summary>
    /// Deserialises and sanity-checks composer output.
    ///
    /// Structural checks only — provenance is checked separately, against the
    /// profile, by FactReferenceValidator in CvMaker.Core. Keeping the two
    /// apart matters: this runs in the agent service, that runs in the API, and
    /// the API must not depend on the agent service having done it.
    /// </summary>
    internal static bool TryParse(string json, out ComposedCv? composed, out string? error)
    {
        composed = null;
        error = null;

        try
        {
            composed = JsonSerializer.Deserialize<ComposedCv>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        if (composed is null)
        {
            error = "Output deserialised to null.";
            return false;
        }

        if (composed.Sections.Count == 0)
        {
            error = "Output contains no sections.";
            return false;
        }

        foreach (var section in composed.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Title))
            {
                error = "A section has no title.";
                return false;
            }

            foreach (var entry in section.Entries)
            {
                // An entry with neither a heading nor bullets renders as an
                // empty block on the page — visible to the user as a defect,
                // so it fails here instead.
                if (string.IsNullOrWhiteSpace(entry.Heading) && entry.Bullets.Count == 0)
                {
                    error = $"An entry in '{section.Title}' has neither a heading nor bullets.";
                    return false;
                }
            }
        }

        return true;
    }

    private static ComposeResult Fail(string error, FailureCause cause, IReadOnlyList<string> log) =>
        new(false, null, error, cause, log);

    /// <summary>
    /// Pulls the critic's assessment out of the flow's step outputs.
    ///
    /// Best-effort by design: a malformed critique must never fail a generation
    /// that otherwise produced a valid CV. The review is advisory — the user
    /// still gets their document, just without the score.
    /// </summary>
    private static AtsReview? ExtractReview(
        IReadOnlyDictionary<string, string>? stepOutputs, ILogger logger)
    {
        if (stepOutputs is null || !stepOutputs.TryGetValue("critique", out var json)) return null;

        try
        {
            return JsonSerializer.Deserialize<AtsReview>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Critique output could not be parsed; continuing without a score");
            return null;
        }
    }
}
