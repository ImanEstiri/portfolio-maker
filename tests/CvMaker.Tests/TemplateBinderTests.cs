using CvMaker.Core.Binding;
using CvMaker.Core.Content;
using Xunit;

namespace CvMaker.Tests;

public class TemplateBinderTests
{
    private readonly TemplateBinder _binder = new();

    private static CvContent Content(string fullName = "Konrad Cinkusz") => new()
    {
        FullName = fullName,
        Headline = "Senior Software Engineer",
        Email = "someone@example.com",
        Summary = new CvProse { Text = "Backend engineer.", SourceFactIds = ["f1"] },
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
                        DateRange = "2019-2024",
                        SourceFactIds = ["f1"],
                        Bullets = [new CvBullet { Text = "Led the migration", SourceFactIds = ["f1"] }]
                    }
                ]
            }
        ]
    };

    [Fact]
    public void Binds_content_into_the_template()
    {
        var tex = _binder.Bind("Name: {{ cv.full_name }}, Mail: {{ cv.email }}", Content());

        Assert.Equal("Name: Konrad Cinkusz, Mail: someone@example.com", tex);
    }

    [Fact]
    public void Iterates_sections_entries_and_bullets()
    {
        const string template =
            "{{ for s in cv.sections }}{{ s.title }}:" +
            "{{ for e in s.entries }}{{ e.heading }}@{{ e.organization }}" +
            "{{ for b in e.bullets }}[{{ b }}]{{ end }}{{ end }}{{ end }}";

        var tex = _binder.Bind(template, Content());

        Assert.Equal("Experience:Senior Software Engineer@Acme[Led the migration]", tex);
    }

    /// <summary>
    /// The end-to-end property that matters: hostile input in the content model
    /// cannot reach the compiler as a command, because the binder escapes
    /// before Scriban ever sees the value.
    /// </summary>
    [Fact]
    public void Injection_through_content_is_escaped_before_templating()
    {
        var tex = _binder.Bind(
            @"\author{ {{- cv.full_name -}} }",
            Content(@"Konrad} \write18{rm -rf /} \input{/etc/passwd"));

        Assert.DoesNotContain(@"\write18{rm", tex, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\input{/etc", tex, StringComparison.Ordinal);
        Assert.Contains(@"\textbackslash{}write18", tex, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CV full of user links is a link-scheme problem. Rejected targets still
    /// render their label — losing a hyperlink beats shipping <c>javascript:</c>.
    /// </summary>
    [Fact]
    public void Unsafe_link_targets_are_marked_not_linkable()
    {
        var content = Content() with
        {
            Links = new Dictionary<string, string>
            {
                ["GitHub"] = "https://github.com/konradcinkusz",
                ["Bad"] = "javascript:alert(1)"
            }
        };

        var tex = _binder.Bind(
            "{{ for l in cv.links }}{{ l.label }}={{ if l.is_link }}LINK{{ else }}TEXT{{ end }};{{ end }}",
            content);

        Assert.Equal("GitHub=LINK;Bad=TEXT;", tex);
    }

    [Fact]
    public void Missing_optional_fields_render_as_empty_not_null()
    {
        var tex = _binder.Bind("[{{ cv.phone }}][{{ cv.location }}]",
            new CvContent { FullName = "Konrad Cinkusz" });

        Assert.Equal("[][]", tex);
    }

    [Fact]
    public void Malformed_template_fails_loudly()
    {
        var ex = Assert.Throws<TemplateBindingException>(
            () => _binder.Bind("{{ for x in }}", Content()));

        Assert.Contains("parse", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A typo in a template slot must never silently resolve to real content.
    ///
    /// Asserted as "does not render the name" rather than "throws" on purpose:
    /// the binder sets <c>StrictVariables</c>, but Scriban applies that to
    /// undefined globals, and its behaviour for a missing *member* on a
    /// dictionary is the thing this test would be pinning down. The invariant
    /// worth having either way is that a misspelled slot yields nothing.
    /// </summary>
    [Fact]
    public void Typo_in_a_template_slot_never_resolves_to_real_content()
    {
        string? rendered = null;
        try
        {
            rendered = _binder.Bind("{{ cv.full_naem }}", Content());
        }
        catch (TemplateBindingException)
        {
            // Strict variables rejected it outright — the stronger outcome.
            return;
        }

        Assert.DoesNotContain("Konrad", rendered, StringComparison.Ordinal);
    }
}
