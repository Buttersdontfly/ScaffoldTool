using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Scaffold;

/// <summary>
/// Writes a throwaway project that ProjectReferences the target, then runs it.
///
/// The probe therefore compiles against the target's own EF Core version. This
/// is the whole reason for the extra process: loading the target's assemblies
/// into this tool would mean the tool's EF version has to match the project's,
/// which is the failure mode that makes most scaffolders fragile.
/// </summary>
public sealed class ProbeRunner(string targetProjectPath, bool verbose)
{
    private readonly string _targetProjectPath = Path.GetFullPath(targetProjectPath);

    public async Task<JsonNode> RunAsync(string? contextName, string? provider, string? connection, CancellationToken ct)
    {
        var projectDir = Path.GetDirectoryName(_targetProjectPath)!;
        var probeDir = Path.Combine(projectDir, "obj", "scaffold-probe");
        var rawPath = Path.Combine(probeDir, "raw-model.json");

        Directory.CreateDirectory(probeDir);

        var targetFramework = ReadTargetFramework(_targetProjectPath);
        var assemblyName = ReadAssemblyName(_targetProjectPath);

        WriteProbeSources(probeDir, targetFramework);

        var args = new List<string>
        {
            "run",
            "--project", Path.Combine(probeDir, "Probe.csproj"),
            "--configuration", "Debug",
            "--verbosity", verbose ? "normal" : "quiet",
            "--",
            "--assembly", assemblyName,
            "--out", rawPath
        };

        if (contextName is not null) { args.AddRange(["--context", contextName]); }
        if (provider is not null) { args.AddRange(["--provider", provider]); }
        if (connection is not null) { args.AddRange(["--connection", connection]); }

        var (exitCode, output) = await RunDotnetAsync(args, projectDir, ct);

        if (exitCode != 0)
        {
            // The probe's own message matters far more than the exit code, and
            // swallowing it turns every failure into the same unhelpful line.
            var detail = string.IsNullOrWhiteSpace(output)
                ? "The probe produced no output."
                : output.Trim();

            throw new ScaffoldException(
                $"The probe failed (exit {exitCode}).{Environment.NewLine}{detail}{Environment.NewLine}" +
                "Re-run with --verbose for the full build output. If the target has no " +
                "IDesignTimeDbContextFactory, pass --provider and --connection.");
        }

        if (!File.Exists(rawPath))
        {
            throw new ScaffoldException($"The probe reported success but wrote no file at {rawPath}.");
        }

        await using var stream = File.OpenRead(rawPath);

        return await JsonNode.ParseAsync(stream, cancellationToken: ct)
            ?? throw new ScaffoldException("The probe wrote an empty document.");
    }

    private void WriteProbeSources(string probeDir, string targetFramework)
    {
        // Rewritten on every run so an upgraded tool never runs a stale probe.
        var csproj = EmbeddedFiles.Read(EmbeddedFiles.ProbeProject)
            .Replace("{{TargetFramework}}", targetFramework)
            .Replace("{{TargetProjectPath}}", Path.GetRelativePath(probeDir, _targetProjectPath));

        File.WriteAllText(Path.Combine(probeDir, "Probe.csproj"), csproj);
        File.WriteAllText(Path.Combine(probeDir, "Program.cs"), EmbeddedFiles.Read(EmbeddedFiles.ProbeProgram));

        WriteBuildChainTerminators(probeDir);

        // Keeps the probe out of source control without touching the repo's
        // .gitignore, which the tool has no business editing.
        File.WriteAllText(Path.Combine(probeDir, ".gitignore"), "*\n");
    }

    /// <summary>
    /// The probe lives inside the target repo, so MSBuild walks up from it and
    /// picks up Directory.Build.props, Directory.Build.targets and
    /// Directory.Packages.props from the solution root. Those routinely carry
    /// TreatWarningsAsErrors, analyzer packages, StyleCop rules and central
    /// package management -- none of which should apply to generated throwaway
    /// code, and any of which can fail the build for reasons unrelated to the
    /// model.
    ///
    /// MSBuild stops walking at the first file it finds, so empty ones placed
    /// here terminate the search without modifying anything in the repo.
    /// </summary>
    private static void WriteBuildChainTerminators(string probeDir)
    {
        const string empty = """
            <Project>
            </Project>
            """;

        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            File.WriteAllText(Path.Combine(probeDir, name), empty);
        }

        File.WriteAllText(Path.Combine(probeDir, "Directory.Packages.props"), """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """);
    }

    private static string ReadTargetFramework(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);

        var tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value
            ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value.Split(';').FirstOrDefault();

        return string.IsNullOrWhiteSpace(tfm) ? "net10.0" : tfm.Trim();
    }

    private static string ReadAssemblyName(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);

        return doc.Descendants("AssemblyName").FirstOrDefault()?.Value.Trim()
            ?? Path.GetFileNameWithoutExtension(csprojPath);
    }

    private async Task<(int ExitCode, string Output)> RunDotnetAsync(
        IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        if (verbose)
        {
            Console.WriteLine($"> dotnet {string.Join(' ', args)}");
        }

        using var process = Process.Start(info)
            ?? throw new ScaffoldException("Could not start 'dotnet'. Is the SDK on PATH?");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var output = await stdout;
        var errors = await stderr;

        if (verbose && output.Length > 0)
        {
            Console.WriteLine(output);
        }

        if (process.ExitCode != 0 && errors.Length > 0)
        {
            Console.Error.WriteLine(errors);
        }

        // stderr first: the probe writes its diagnosis there, while stdout is
        // mostly MSBuild noise.
        var combined = string.Join(Environment.NewLine,
            new[] { errors, output }.Where(t => !string.IsNullOrWhiteSpace(t)));

        return (process.ExitCode, combined);
    }
}
