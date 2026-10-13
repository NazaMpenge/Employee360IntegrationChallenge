using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace Employee360.Integration.Messaging;

public sealed class SamplesOptions
{
    public const string SectionName = "Samples";

    public string Directory { get; set; } = "samples/events";
}

public sealed class SampleCatalog
{
    private readonly string _directory;

    public SampleCatalog(IOptions<SamplesOptions> options) => _directory = Resolve(options.Value.Directory);

    private static string Resolve(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;

        foreach (var start in new[] { System.IO.Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, configured);
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
        }

        return configured; 
    }

    public IReadOnlyList<string> SequenceFiles() =>
        System.IO.Directory.Exists(_directory)
            ? System.IO.Directory.GetFiles(_directory, "*.json")
                .Select(f => Path.GetFileName(f)).OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal).ToList()
            : new List<string>();

    public bool TryRead(string name, [NotNullWhen(true)] out string? payload)
    {
        payload = null;

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
