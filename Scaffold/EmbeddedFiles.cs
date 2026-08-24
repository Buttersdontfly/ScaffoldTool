using System.Reflection;

namespace Scaffold;

/// <summary>
/// Single place that reads files baked into the tool.
///
/// Names are pinned with LogicalName in Scaffold.csproj and the source files
/// deliberately avoid a ".cs" segment: the SDK's resource naming convention
/// peels the final extension and, if the remainder looks like a source file,
/// rewrites the manifest name from that file's namespace -- which silently
/// overrides LogicalName.
/// </summary>
public static class EmbeddedFiles
{
    public const string ProbeProgram = "Scaffold.Probe.ProbeProgram.txt";
    public const string ProbeProject = "Scaffold.Probe.ProbeProject.txt";
    public const string Rules = "Scaffold.rules.json";

    public static string Read(string logicalName)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(logicalName);

        if (stream is null)
        {
            var available = assembly.GetManifestResourceNames();

            var list = available.Length == 0
                ? "    (none -- nothing was embedded at all)"
                : string.Join(Environment.NewLine, available.Select(n => $"    {n}"));

            throw new ScaffoldException(
                $"Embedded resource '{logicalName}' is missing from the tool.{Environment.NewLine}" +
                $"Resources actually present:{Environment.NewLine}{list}{Environment.NewLine}" +
                $"Tool assembly: {assembly.Location}{Environment.NewLine}" +
                "If the list is empty or short, the .txt files were not packed. See 'Rebuilding the tool' in the README.");
        }

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>Used by 'scaffold doctor' to show what shipped.</summary>
    public static IReadOnlyList<string> All() =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames().OrderBy(n => n).ToList();
}
