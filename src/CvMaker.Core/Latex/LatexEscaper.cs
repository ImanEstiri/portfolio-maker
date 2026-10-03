using System.Globalization;
using System.Text;

namespace CvMaker.Core.Latex;

/// <summary>
/// Escapes untrusted text so it can be interpolated into a LaTeX template as
/// literal content.
///
/// This is the security boundary of the whole product. Everything that reaches
/// a template — profile text the user typed, a job posting they pasted, and
/// anything a model wrote on top of both — passes through here first. The
/// guarantee it makes is narrow and absolute:
///
///     the output contains no character sequence the TeX engine will read as
///     a control sequence, a group delimiter, a comment, or an alignment tab.
///
/// That is what stops <c>\write18{...}</c>, <c>\input{/etc/passwd}</c> and
/// <c>\def\x{\x}\x</c> from surviving the trip from a text box to a compiler.
/// The renderer's sandbox (no shell-escape, confined file access, a hard
/// timeout — SECURITY.md lists what each environment adds) is the second layer;
/// this is the first, and the cheaper one.
/// </summary>
public static class LatexEscaper
{
    /// <summary>
    /// Escaped forms, indexed for a single pass over the input.
    ///
    /// This MUST be applied in one pass. The tempting implementation —
    /// <c>s.Replace("\\", "\\textbackslash{}").Replace("{", "\\{")…</c> — is
    /// wrong, because each replacement's output is re-scanned by the next one
    /// and the braces introduced by earlier rules get escaped again. It is a
    /// classic bug, it corrupts output rather than failing loudly, and in the
    /// other direction (escaping backslash last) it is a sanitiser bypass.
    /// </summary>
    private static string? EscapeFor(char c) => c switch
    {
        '\\' => @"\textbackslash{}",
        '{'  => @"\{",
        '}'  => @"\}",
        '$'  => @"\$",
        '&'  => @"\&",
        '#'  => @"\#",
        '_'  => @"\_",
        '%'  => @"\%",
        '~'  => @"\textasciitilde{}",
        '^'  => @"\textasciicircum{}",

        // Not strictly dangerous under LuaLaTeX with fontspec, but they render
        // as inverted punctuation under OT1 encodings. Mapping them keeps a
        // template's choice of engine from silently changing what a CV says.
        '<'  => @"\textless{}",
        '>'  => @"\textgreater{}",
        '|'  => @"\textbar{}",

        _ => null
    };

    /// <summary>
    /// Escapes a single-line slot: a name, a job title, a date range, one
    /// bullet. All internal whitespace — including newlines — collapses to
    /// single spaces, because these slots land inside LaTeX macro arguments
    /// where a blank line (<c>\par</c>) is a syntax error.
    /// </summary>
    public static string EscapeInline(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var normalised = NormaliseWhitespace(StripInvisible(value), collapseBlankLines: false);
        return EscapeCore(Truncate(normalised, maxLength));
    }

    /// <summary>
    /// Escapes a multi-paragraph slot: a summary, a cover-letter body. Blank
    /// lines survive as paragraph breaks; every other whitespace run collapses.
    ///
    /// <c>\par</c> is emitted rather than a literal blank line so the result is
    /// still safe to place inside macro arguments that tolerate it, and so the
    /// output has no line-structure for a template to accidentally depend on.
    /// </summary>
    public static string EscapeParagraphs(string? value, int maxLength = DefaultMaxParagraphLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var normalised = NormaliseWhitespace(StripInvisible(value), collapseBlankLines: true);
        var truncated  = Truncate(normalised, maxLength);

        var paragraphs = truncated
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EscapeCore);

        return string.Join("\n\\par\n", paragraphs);
    }

    /// <summary>
    /// Escapes a URL for use as the target of <c>\href</c>.
    ///
    /// Only a small set of schemes is allowed through. A CV is a document full
    /// of user-supplied links, and a PDF viewer will happily follow
    /// <c>javascript:</c> or <c>file:</c> — so the scheme is checked here
    /// rather than left to the template author to remember.
    /// Returns <see langword="null"/> when the URL is not safe to link, which
    /// callers should render as plain text instead.
    /// </summary>
    public static string? EscapeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var candidate = StripInvisible(value).Trim();

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return null;

        // Inside \href the '%' of a percent-encoded URL is still a comment
        // character, and '#' still starts a fragment TeX will try to read as a
        // parameter, so the target needs escaping exactly like body text.
        return EscapeCore(uri.AbsoluteUri);
    }

    public const int DefaultMaxLength = 2_000;
    public const int DefaultMaxParagraphLength = 20_000;

    private static string EscapeCore(string value)
    {
        // Most CV text contains nothing that needs escaping; skip the
        // allocation entirely in that case.
        var needsEscaping = false;
        foreach (var c in value)
        {
            if (EscapeFor(c) is not null) { needsEscaping = true; break; }
        }
        if (!needsEscaping) return value;

        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            var replacement = EscapeFor(c);
            if (replacement is null) sb.Append(c);
            else sb.Append(replacement);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Removes characters that are invisible in the rendered PDF but change
    /// what the text means to something reading the file.
    ///
    /// Bidirectional overrides are the reason this exists. A CV carrying
    /// U+202E can display one employer to a human reading the PDF while the
    /// underlying text — the part an applicant tracking system parses —
    /// says something else. That is a Trojan Source attack pointed at a
    /// hiring pipeline, and it is invisible to review, so it is stripped
    /// rather than escaped.
    ///
    /// C0/C1 controls go too: they survive into the PDF text layer and have no
    /// legitimate use in a CV.
    /// </summary>
    private static string StripInvisible(string value)
    {
        var sb = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes())
        {
            var v = rune.Value;

            // Keep the whitespace NormaliseWhitespace is about to fold.
            if (v is '\t' or '\n' or '\r') { sb.Append((char)v); continue; }

            // C0 and C1 control ranges.
            if (v < 0x20 || (v >= 0x7F && v <= 0x9F)) continue;

            var stripped =
                // Zero-width space / non-joiner / joiner, and the LTR/RTL marks
                // and embedding/override controls that follow them.
                (v >= 0x200B && v <= 0x200F) ||
                (v >= 0x202A && v <= 0x202E) ||
                // Isolate controls (LRI/RLI/FSI/PDI) — the modern equivalent.
                (v >= 0x2066 && v <= 0x2069) ||
                // Word joiner, invisible operators, BOM as a mid-string char.
                v == 0x2060 || v == 0xFEFF ||
                // Soft hyphen: invisible, and splits keywords for ATS parsers.
                v == 0x00AD;

            if (stripped) continue;

            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Collapses whitespace runs to a single space. When
    /// <paramref name="collapseBlankLines"/> is set, a run containing two or
    /// more newlines becomes a single newline instead, marking a paragraph
    /// break for <see cref="EscapeParagraphs"/> to act on.
    /// </summary>
    private static string NormaliseWhitespace(string value, bool collapseBlankLines)
    {
        var sb = new StringBuilder(value.Length);
        var i = 0;

        while (i < value.Length)
        {
            if (!IsWhitespace(value[i]))
            {
                sb.Append(value[i]);
                i++;
                continue;
            }

            var newlines = 0;
            var start = i;
            while (i < value.Length && IsWhitespace(value[i]))
            {
                if (value[i] == '\n' || value[i] == '\u2028' || value[i] == '\u2029') newlines++;
                i++;
            }

            // Leading whitespace produces nothing; trailing is trimmed at the end.
            if (start == 0) continue;

            sb.Append(collapseBlankLines && newlines >= 2 ? '\n' : ' ');
        }

        return sb.ToString().Trim();

        static bool IsWhitespace(char c) =>
            char.IsWhiteSpace(c) || c == '\u2028' || c == '\u2029';
    }

    /// <summary>
    /// Caps slot length, cutting on a text-element boundary so a truncated
    /// string cannot end mid-surrogate-pair or split a combining sequence off
    /// its base character — either of which produces a replacement glyph in
    /// the PDF, or an encoding error in the engine.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0) return string.Empty;
        if (value.Length <= maxLength) return value;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var lastSafeEnd = 0;

        while (enumerator.MoveNext())
        {
            var end = enumerator.ElementIndex + enumerator.GetTextElement().Length;
            if (end > maxLength) break;
            lastSafeEnd = end;
        }

        return value[..lastSafeEnd].TrimEnd();
    }
}
