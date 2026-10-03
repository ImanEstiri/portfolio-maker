using System.Text.Json;

namespace CvMaker.Agentic.Flow;

public sealed class FlowCatalog
{
    private readonly Dictionary<string, FlowDocument> _flows;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FlowCatalog(string rootPath, ILogger<FlowCatalog> logger)
    {
        _flows = new Dictionary<string, FlowDocument>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
        {
            logger.LogError("Flow root {Root} does not exist; no flows loaded", rootPath);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.json"))
        {
            var flow = JsonSerializer.Deserialize<FlowDocument>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidOperationException($"{file} deserialised to null");

            _flows[flow.Slug] = flow;
            logger.LogInformation("Loaded flow {Slug} v{Version} ({Steps} steps)",
                flow.Slug, flow.Version, flow.Steps.Count);
        }
    }

    public FlowDocument? Find(string slug) => _flows.TryGetValue(slug, out var f) ? f : null;
}
