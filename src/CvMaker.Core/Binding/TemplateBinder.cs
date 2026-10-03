using CvMaker.Core.Content;
using CvMaker.Core.Latex;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace CvMaker.Core.Binding;

public sealed class TemplateBindingException(string message) : Exception(message);

/// <summary>
/// Renders <see cref="CvContent"/> into LaTeX source through a repo-owned
/// Scriban template.
///
/// The ordering here is the whole design: <b>every string is escaped before
/// the template engine sees it.</b> The binder builds a fully escaped view
/// model, and Scriban's only job is to place already-safe strings into a
/// layout. Nothing downstream has to remember to escape, no template author
/// can forget to, and the correctness of the security boundary does not depend
/// on Scriban's own escaping behaviour — which is built for HTML, not TeX, and
/// would be the wrong tool for this even if it were enabled.
///
/// Templates take the template <i>source</i> rather than a path: this project
/// stays free of I/O, and the caller owns file access and caching.
/// </summary>
public sealed class TemplateBinder
{
    /// <summary>
    /// Binds content to a template.
    /// </summary>
    /// <param name="templateSource">Scriban template text, owned by this repo — never model-authored.</param>
    /// <param name="content">Composed content. Assumed to have passed
    /// <see cref="Validation.FactReferenceValidator"/> already; this method does not re-check provenance.</param>
    public string Bind(string templateSource, CvContent content)
    {
        ArgumentNullException.ThrowIfNull(templateSource);
        ArgumentNullException.ThrowIfNull(content);

        var template = Template.Parse(templateSource);
        if (template.HasErrors)
        {
            // A broken template is our bug, not the user's, and it should read
            // like one in the logs rather than surfacing as a LaTeX error 40
            // seconds later in the renderer.
            throw new TemplateBindingException(
                "Template failed to parse: " +
                string.Join("; ", template.Messages.Select(m => m.ToString())));
        }

        var root = new ScriptObject();
        root.Add("cv", BuildEscapedModel(content));

        var context = new TemplateContext
        {
            // Surfaces a typo in a template as a build failure instead of
            // silently rendering a CV with an empty name on it.
            StrictVariables = true,
            // Templates are ours, but they are still data files; a runaway
            // loop in one should not take out the process.
            LoopLimit = 5_000,
            RecursiveLimit = 50
        };
        context.PushGlobal(root);

        try
        {
            return template.Render(context);
        }
        catch (ScriptRuntimeException ex)
        {
            throw new TemplateBindingException($"Template failed to render: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the escaped view model.
    ///
    /// Plain dictionaries rather than typed objects, for two reasons: the keys
    /// in the template are then exactly the keys written here (no dependency on
    /// Scriban's PascalCase-to-snake_case member renamer), and a value can only
    /// enter this structure by passing through <see cref="LatexEscaper"/>,
    /// which is easy to verify by reading the method.
    /// </summary>
    private static Dictionary<string, object?> BuildEscapedModel(CvContent content) => new()
    {
        ["full_name"] = LatexEscaper.EscapeInline(content.FullName, maxLength: 200),
        ["headline"]  = LatexEscaper.EscapeInline(content.Headline, maxLength: 300),
        ["email"]     = LatexEscaper.EscapeInline(content.Email, maxLength: 320),
        ["phone"]     = LatexEscaper.EscapeInline(content.Phone, maxLength: 64),
        ["location"]  = LatexEscaper.EscapeInline(content.Location, maxLength: 200),

        ["summary"] = content.Summary is null
            ? string.Empty
            : LatexEscaper.EscapeParagraphs(content.Summary.Text),

        ["links"] = content.Links
            .Select(kv => new Dictionary<string, object?>
            {
                ["label"] = LatexEscaper.EscapeInline(kv.Key, maxLength: 64),
                // Null target means the scheme was rejected. The label still
                // renders, just not as a clickable link — losing a hyperlink is
                // a better outcome than putting javascript: in someone's CV.
                ["url"]     = LatexEscaper.EscapeUrl(kv.Value),
                ["is_link"] = LatexEscaper.EscapeUrl(kv.Value) is not null
            })
            .ToList(),

        // Single-column templates use `sections`; two-column templates use the
        // pre-split lists so a template never has to filter, which keeps
        // Scriban doing placement rather than logic.
        ["sections"] = content.Sections.Select(BuildSection).ToList(),

        ["main_sections"] = content.Sections
            .Where(s => s.Placement == SectionPlacement.Main)
            .Select(BuildSection)
            .ToList(),

        ["side_sections"] = content.Sections
            .Where(s => s.Placement == SectionPlacement.Side)
            .Select(BuildSection)
            .ToList()
    };

    private static Dictionary<string, object?> BuildSection(CvSection section) => new()
    {
        ["title"] = LatexEscaper.EscapeInline(section.Title, maxLength: 120),
        ["entries"] = section.Entries
            .Select(entry => new Dictionary<string, object?>
            {
                ["heading"]      = LatexEscaper.EscapeInline(entry.Heading, maxLength: 200),
                ["organization"] = LatexEscaper.EscapeInline(entry.Organization, maxLength: 200),
                ["location"]     = LatexEscaper.EscapeInline(entry.Location, maxLength: 200),
                ["date_range"]   = LatexEscaper.EscapeInline(entry.DateRange, maxLength: 64),
                ["bullets"]      = entry.Bullets
                    .Select(b => LatexEscaper.EscapeInline(b.Text, maxLength: 600))
                    .ToList()
            })
            .ToList()
    };
}
