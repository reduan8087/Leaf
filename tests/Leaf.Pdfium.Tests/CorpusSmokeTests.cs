using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Leaf.Pdfium.Tests;

/// <summary>
/// Opt-in stability sweep over real PDFs: set LEAF_CORPUS_DIR to a folder and run
/// <c>dotnet test --filter Category=Corpus</c>. Every file is opened, page 1 is rendered as a thumbnail
/// and its text extracted. Failures are collected into artifacts/corpus-report.txt; the process must not crash.
/// </summary>
[Trait("Category", "Corpus")]
public sealed class CorpusSmokeTests
{
    private readonly ITestOutputHelper _output;

    public CorpusSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Every_pdf_in_the_corpus_opens_or_fails_cleanly()
    {
        string? dir = Environment.GetEnvironmentVariable("LEAF_CORPUS_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _output.WriteLine("LEAF_CORPUS_DIR not set; skipping.");
            return;
        }

        int max = int.TryParse(Environment.GetEnvironmentVariable("LEAF_CORPUS_MAX"), out int m) ? m : int.MaxValue;
        string[] files = Directory.EnumerateFiles(dir, "*.pdf", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Take(max)
            .ToArray();

        var report = new List<string>();
        int ok = 0, password = 0, failed = 0;
        long totalMs = 0;
        var sw = new Stopwatch();
        foreach (string file in files)
        {
            sw.Restart();
            try
            {
                PdfiumThread.Instance.Run(() =>
                {
                    using PdfDocument doc = PdfDocument.Open(file);
                    if (doc.PageCount > 0)
                    {
                        doc.RenderPage(0, 0, 0.3, out _, out _);
                        _ = doc.GetTextLayout(0);
                    }
                });
                ok++;
            }
            catch (PdfException ex) when (ex.Error == PdfError.Password)
            {
                password++;
            }
            catch (Exception ex)
            {
                failed++;
                report.Add($"{file}\t{ex.GetType().Name}: {ex.Message}");
            }

            totalMs += sw.ElapsedMilliseconds;
            if (sw.ElapsedMilliseconds > 2000)
            {
                report.Add($"{file}\tSLOW {sw.ElapsedMilliseconds} ms");
            }
        }

        string summary = $"corpus: {files.Length} files, {ok} ok, {password} password-protected, {failed} failed, {totalMs / Math.Max(1, files.Length)} ms avg";
        _output.WriteLine(summary);
        string artifacts = Path.Combine(FindRepoRoot(), "artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllLines(Path.Combine(artifacts, "corpus-report.txt"), [summary, .. report]);
        Assert.True(failed <= files.Length / 20, $"{failed} files failed to open (>5%) - see artifacts/corpus-report.txt");
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Leaf.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? AppContext.BaseDirectory;
    }
}
