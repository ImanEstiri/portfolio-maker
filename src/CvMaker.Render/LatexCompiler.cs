using System.Diagnostics;

namespace CvMaker.Render;

/// <summary>
/// A compile request. <paramref name="Assets"/> carries template support files
/// — a document class such as altacv.cls, a .sty — keyed by file name and
/// written into the work directory beside the document.
/// </summary>
public sealed record CompileRequest(
    string TexSource,
    string Engine = "lualatex",
    IReadOnlyDictionary<string, string>? Assets = null);

public sealed record CompileResult(
    bool Success,
    byte[]? Pdf,
    string? Error,
    string Log,
    TimeSpan Duration)
{
    public static CompileResult Failed(string error, string log, TimeSpan duration)
        => new(false, null, error, log, duration);
}

public sealed class LatexCompilerOptions
{
    /// <summary>
    /// Wall-clock cap on a single compile. An expansion bomb that survives
    /// escaping still has to die here, and a legitimate two-page CV compiles in
    /// one to three seconds, so this is generous.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Where per-compile working directories are created. Should be a tmpfs mount.</summary>
    public string WorkRoot { get; init; } = Path.Combine(Path.GetTempPath(), "cvmaker-render");

    /// <summary>Cap on generated PDF size — a defence against output-amplification.</summary>
    public long MaxPdfBytes { get; init; } = 20 * 1024 * 1024;

    public long MaxTexBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>
/// Runs latexmk over bound LaTeX source and returns a PDF.
///
/// This is the product's replacement for the <c>xu-cheng/latex-action</c> step
/// in the CV repo's CI, and the one place where input derived from strangers
/// drives a process. The differences from that workflow are all deliberate:
///
/// <list type="bullet">
/// <item><b>No shell escape.</b> The CV repo compiles with
/// <c>latexmk_shell_escape: true</c>, which is correct for a repo whose only
/// author is its owner and a remote shell for a public service. The flag is
/// never passed here, and <c>shell_escape=f</c> is set in the environment so
/// that a template cannot re-enable it either.</item>
/// <item><b>No file access outside the work directory.</b> <c>openin_any=p</c>
/// and <c>openout_any=p</c> stop <c>\input{/etc/passwd}</c> from reading
/// anything the process could otherwise reach.</item>
/// <item><b>One directory per compile</b>, created fresh and deleted after, so
/// nothing leaks between users.</item>
/// <item><b>A hard timeout</b> with a process-tree kill.</item>
/// </list>
///
/// The container adds the rest where it is configured to: a non-root user
/// everywhere, and a read-only rootfs with dropped capabilities under
/// docker-compose.yml and in CI, though not on Fly. Egress is not denied yet.
/// Neither layer is sufficient alone; see the Dockerfile for what "no network"
/// can and cannot mean for a service that has to accept HTTP.
/// </summary>
public sealed class LatexCompiler(
    LatexCompilerOptions options,
    ILogger<LatexCompiler> logger)
{
    private static readonly string[] AllowedEngines = ["lualatex", "pdflatex", "xelatex"];

    public async Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(request.TexSource))
            return CompileResult.Failed("Empty LaTeX source.", string.Empty, TimeSpan.Zero);

        if (request.TexSource.Length > options.MaxTexBytes)
            return CompileResult.Failed(
                $"LaTeX source exceeds {options.MaxTexBytes} bytes.", string.Empty, TimeSpan.Zero);

        // Allowlist, not validation: the engine name becomes a command-line
        // argument, and anything not on this list is a caller bug at best.
        if (!AllowedEngines.Contains(request.Engine, StringComparer.Ordinal))
            return CompileResult.Failed($"Unsupported engine '{request.Engine}'.", string.Empty, TimeSpan.Zero);

        var workDir = Path.Combine(options.WorkRoot, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workDir);

        try
        {
            var texPath = Path.Combine(workDir, "document.tex");
            await File.WriteAllTextAsync(texPath, request.TexSource, ct);

            if (request.Assets is { Count: > 0 })
            {
                foreach (var (name, content) in request.Assets)
                {
                    if (!IsSafeAssetName(name))
                        return CompileResult.Failed(
                            $"Rejected asset file name '{name}'.", string.Empty, TimeSpan.Zero);

                    await File.WriteAllTextAsync(Path.Combine(workDir, name), content, ct);
                }
            }

            var (exitCode, log, timedOut) = await RunLatexmkAsync(request.Engine, workDir, ct);
            var duration = Stopwatch.GetElapsedTime(started);

            if (timedOut)
            {
                logger.LogWarning("Compile exceeded {Timeout}s and was killed", options.Timeout.TotalSeconds);
                return CompileResult.Failed(
                    $"Typesetting timed out after {options.Timeout.TotalSeconds:0}s.", log, duration);
            }

            var pdfPath = Path.Combine(workDir, "document.pdf");

            // latexmk can exit non-zero while still producing a usable PDF (an
            // overfull box is an error to it, not to a reader), so the PDF's
            // existence is the success signal rather than the exit code.
            if (!File.Exists(pdfPath))
            {
                // The comment on ExtractFirstTexError promises the full log stays
                // server-side, but nothing previously logged it anywhere — a
                // real compile failure left no way to diagnose it after the work
                // directory was deleted in `finally`.
                logger.LogError("latexmk exited {ExitCode} with no PDF produced. Full log:\n{Log}", exitCode, log);
                return CompileResult.Failed(
                    $"Typesetting produced no PDF (latexmk exit {exitCode}). {ExtractFirstTexError(log)}",
                    log, duration);
            }

            var info = new FileInfo(pdfPath);
            if (info.Length > options.MaxPdfBytes)
            {
                return CompileResult.Failed(
                    $"Generated PDF is {info.Length} bytes, over the {options.MaxPdfBytes} byte limit.",
                    log, duration);
            }

            var pdf = await File.ReadAllBytesAsync(pdfPath, ct);
            logger.LogInformation("Compiled {Bytes} byte PDF in {Ms}ms", pdf.Length, duration.TotalMilliseconds);

            return new CompileResult(true, pdf, null, log, duration);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    /// <summary>
    /// Asset names come from repo-owned template.json files, so this should
    /// never fire — which is exactly why it is cheap to enforce. A name that
    /// escapes the work directory would let a template write over anything the
    /// process can reach, and "it's our own config" is how that stops being
    /// true later.
    ///
    /// Allowlist rather than a blocklist of "..": bare names, one dot, known
    /// TeX support extensions.
    /// </summary>
    private static readonly string[] AllowedAssetExtensions = [".cls", ".sty", ".bib", ".bst", ".def", ".cfg"];

    private static bool IsSafeAssetName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 128
        && name == Path.GetFileName(name)
        && !name.StartsWith('.')
        && name.Count(c => c == '.') == 1
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && AllowedAssetExtensions.Contains(Path.GetExtension(name), StringComparer.Ordinal);

    private async Task<(int ExitCode, string Log, bool TimedOut)> RunLatexmkAsync(
        string engine, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "latexmk",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add($"-{engine}");
        psi.ArgumentList.Add("-interaction=nonstopmode");
        psi.ArgumentList.Add("-halt-on-error");
        psi.ArgumentList.Add("-file-line-error");
        psi.ArgumentList.Add("-no-shell-escape");
        psi.ArgumentList.Add($"-output-directory={workDir}");
        psi.ArgumentList.Add("document.tex");

        // Belt and braces to the -no-shell-escape flag above: these are read by
        // the engine itself, so they hold even if latexmk's own handling of the
        // flag changes, and they are the only control that covers \openin.
        psi.Environment["openin_any"] = "p";
        psi.Environment["openout_any"] = "p";
        psi.Environment["shell_escape"] = "f";
        psi.Environment["max_print_line"] = "1000";
        // Stop the engine from reaching a per-user texmf tree that a future
        // packaging change might make writable.
        psi.Environment["TEXMFHOME"] = workDir;
        psi.Environment["TEXMFVAR"] = workDir;
        psi.Environment["TEXMFCONFIG"] = workDir;
        psi.Environment["HOME"] = workDir;

        using var process = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, output.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            // entireProcessTree: latexmk spawns the engine as a child, and
            // killing only latexmk leaves a spinning lualatex holding the CPU.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            // Caller cancellation is not a timeout — only report a timeout when the
            // deadline is what fired.
            return (-1, output.ToString(), !ct.IsCancellationRequested);
        }
    }

    /// <summary>
    /// Pulls the first real TeX error out of the log for the API response.
    /// The full log stays server-side: it contains absolute paths and package
    /// versions that are of no use to a user and of some use to an attacker.
    /// </summary>
    private static string ExtractFirstTexError(string log)
    {
        foreach (var line in log.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("! ", StringComparison.Ordinal))
                return trimmed.Length > 200 ? trimmed[..200] : trimmed;
        }
        return "No TeX error line found in the log.";
    }

    private void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            // Never fail a request over cleanup; the work root is a tmpfs that
            // goes away with the machine, and a leaked directory is a metric,
            // not an outage.
            logger.LogWarning(ex, "Could not clean work directory {Dir}", dir);
        }
    }
}
