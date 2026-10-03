using System.Text.Json;
using System.Text.Json.Serialization;

namespace CvMaker.Api.Templates;

public sealed record TemplateCapabilities
{
    public bool Photo { get; init; }
    public bool TwoColumn { get; init; }
    public bool Publications { get; init; }
    public bool SkillBars { get; init; }
    public bool ColourAccent { get; init; }
    public bool Links { get; init; } = true;
}

public sealed record TemplatePageBudget
{
    public int Target { get; init; } = 2;
    public int Max { get; init; } = 3;
}

public sealed record TemplateManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Engine { get; init; } = "lualatex";
    public string AtsRating { get; init; } = "unknown";
    public TemplateCapabilities Capabilities { get; init; } = new();
    public TemplatePageBudget PageBudget { get; init; } = new();

    /// <summary>Support files in the template's assets/ directory, shipped to the renderer.</summary>
    public IReadOnlyList<string> Assets { get; init; } = [];

    [JsonIgnore]
    public string CvTemplateSource { get; init; } = string.Empty;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> AssetContents { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Loads the template catalogue from disk, once, when the singleton is first
/// resolved.
///
/// This does the file I/O that <c>CvMaker.Core</c> deliberately does not. The
/// whole catalogue is read in the constructor, so a malformed template.json or
/// a missing asset fails that first resolution. Nothing resolves it during
/// startup, so the failure arrives with the first request or job that needs a
/// template rather than at deploy time; resolving it once after
/// <c>Build()</c> would move it there.
/// </summary>
public sealed class TemplateCatalog
{
    private readonly Dictionary<string, TemplateManifest> _templates;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TemplateCatalog(string rootPath, ILogger<TemplateCatalog> logger)
    {
        _templates = new Dictionary<string, TemplateManifest>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(rootPath))
        {
            logger.LogError("Template root {Root} does not exist; catalogue is empty", rootPath);
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(rootPath))
        {
            var manifestPath = Path.Combine(dir, "template.json");
            var cvPath = Path.Combine(dir, "cv.tex.scriban");

            if (!File.Exists(manifestPath) || !File.Exists(cvPath))
            {
                logger.LogWarning("Skipping {Dir}: needs template.json and cv.tex.scriban", dir);
                continue;
            }

            var manifest = JsonSerializer.Deserialize<TemplateManifest>(
                File.ReadAllText(manifestPath), JsonOptions)
                ?? throw new InvalidOperationException($"template.json in {dir} deserialised to null");

            var assets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in manifest.Assets)
            {
                var assetPath = Path.Combine(dir, "assets", asset);
                if (!File.Exists(assetPath))
                    throw new FileNotFoundException(
                        $"Template '{manifest.Id}' declares asset '{asset}' which is missing", assetPath);

                assets[asset] = File.ReadAllText(assetPath);
            }

            _templates[manifest.Id] = manifest with
            {
                CvTemplateSource = File.ReadAllText(cvPath),
                AssetContents = assets
            };

            logger.LogInformation(
                "Loaded template {Id} ({Assets} asset(s))", manifest.Id, assets.Count);
        }

        if (_templates.Count == 0)
            logger.LogError("No templates loaded from {Root}", rootPath);
    }

    public IReadOnlyCollection<TemplateManifest> All => _templates.Values;

    public TemplateManifest? Find(string id) =>
        _templates.TryGetValue(id, out var t) ? t : null;
}
