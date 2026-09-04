using System.Runtime.InteropServices;

namespace Leaf;

/// <summary>Turns raw launch arguments (initial launch or redirected activation) into PDF paths.</summary>
internal static partial class ActivationParser
{
    public static IReadOnlyList<string> ExtractPdfPaths(IEnumerable<string>? tokens)
    {
        var result = new List<string>();
        if (tokens is null)
        {
            return result;
        }

        foreach (string raw in tokens)
        {
            string token = raw.Trim().Trim('"');
            if (token.Length == 0 || token.StartsWith('-') || token.StartsWith('/'))
            {
                continue;
            }

            try
            {
                string full = Path.GetFullPath(token);
                if (File.Exists(full) && !result.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // not a path; ignore
            }
        }

        return result;
    }

    /// <summary>Splits a Windows command line the same way the C runtime does (quotes, escapes).</summary>
    public static unsafe string[] SplitCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        char** argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv is null)
        {
            return [];
        }

        try
        {
            var args = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                args[i] = new string(argv[i]);
            }

            // Redirected activations may pass the full line including our executable; drop it.
            if (args.Length > 0 && args[0].EndsWith("Leaf.exe", StringComparison.OrdinalIgnoreCase))
            {
                return args[1..];
            }

            return args;
        }
        finally
        {
            LocalFree((nint)argv);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial char** CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);
}
