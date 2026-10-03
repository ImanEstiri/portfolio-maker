using System.Text.Json;
using System.Text.Json.Serialization;

namespace CvMaker.Agentic.Agents;

/// <summary>
/// A versioned agent definition, loaded from <c>agents/&lt;slug&gt;/definition.json</c>.
///
/// Definitions are data rather than code so that a prompt change becomes a
/// reviewable diff with a version number, and can be redeployed and rolled back
/// like any other artifact. A prompt embedded in a C# string literal is none of
/// those things.
/// </summary>
public sealed record AgentDefinition
{
    public required string Slug { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Version { get; init; } = "0.0.0";
    public string Model { get; init; } = "gpt-4.1";
    public float Temperature { get; init; } = 0.2f;
    public required string Instructions { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class AgentCatalog
{
    private readonly Dictionary<string, AgentDefinition> _agents;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AgentCatalog(string rootPath, ILogger<AgentCatalog> logger)
    {
        _agents = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
        {
            logger.LogError("Agent root {Root} does not exist; no agents loaded", rootPath);
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(rootPath))
        {
            var path = Path.Combine(dir, "definition.json");
            if (!File.Exists(path)) continue;

            var definition = JsonSerializer.Deserialize<AgentDefinition>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"{path} deserialised to null");

            _agents[definition.Slug] = definition;
            logger.LogInformation("Loaded agent {Slug} v{Version} ({Model})",
                definition.Slug, definition.Version, definition.Model);
        }

        if (_agents.Count == 0)
            logger.LogError("No agent definitions found under {Root}", rootPath);
    }

    public IReadOnlyCollection<AgentDefinition> All => _agents.Values;

    public AgentDefinition? Find(string slug) => _agents.TryGetValue(slug, out var a) ? a : null;
}
