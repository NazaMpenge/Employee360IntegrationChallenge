using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Employee360.Integration.Messaging;

public sealed class SamplesOptions
{
    public const string SectionName = "Samples";

    /// <summary>Relative to where the app is started (the repo root when using `dotnet run` from there).</summary>
    public string Directory { get; set; } = "samples/events";
}

/// <summary>
/// The tiny "publisher": reads the synthetic scenario files so they can be pushed onto the queue.
/// Top-level files form the ordered demo sequence; files in /manual are published one at a time.
/// </summary>
public sealed class SampleCatalog
{
    private readonly string _directory;

    public SampleCatalog(IOptions<SamplesOptions> options) => _directory = options.Value.Directory;

    public IReadOnlyList<string> SequenceFiles() =>
        System.IO.Directory.Exists(_directory)
            ? System.IO.Directory.GetFiles(_directory, "*.json")
                .Select(f => Path.GetFileName(f)).OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal).ToList()
            : new List<string>();

    public bool TryRead(string name, [NotNullWhen(true)] out string? payload)
    {
        payload = null;

        // GetFileName strips any directory part, so "../../etc/passwd" cannot escape the samples folder.
        var safeName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safeName)) return false;

        foreach (var candidate in new[]
                 {
                     Path.Combine(_directory, safeName),
                     Path.Combine(_directory, "manual", safeName)
                 })
        {
            if (File.Exists(candidate))
            {
                payload = File.ReadAllText(candidate);
                return true;
            }
        }

        return false;
    }
}
