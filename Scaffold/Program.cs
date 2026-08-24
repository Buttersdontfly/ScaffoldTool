using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scaffold;

public sealed class ScaffoldException(string message) : Exception(message);

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "inspect" => await InspectAsync(args.Skip(1).ToArray()),
                "generate" => Generate(args.Skip(1).ToArray()),
                "eject-rules" => EjectRules(args.Skip(1).ToArray()),
                "eject-templates" => EjectTemplates(args.Skip(1).ToArray()),
                "doctor" => Doctor(),
                null or "-h" or "--help" or "help" => Help(),
                var unknown => Fail($"Unknown command '{unknown}'. Try --help.")
            };
        }
        catch (ScaffoldException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            scaffold -- CRUD generation for an EditorTemplates-based MVC project

              scaffold inspect --project <csproj> [--out model.json] [--context Name]
                               [--provider sqlserver|npgsql|sqlite] [--connection <cs>]
                               [--templates <dir>] [--report] [--verbose]

                  Builds a throwaway probe against the project, reads EF Core's
                  IModel, applies the hint rules, and writes model.json.
                  Re-running MERGES: your edits to display, search, order,
                  additionalViewData and items survive a schema change.

              scaffold generate [--from model.json] [--entity Name]
                                [--dry-run | --force]
                  Renders ViewModels, controllers and views. --dry-run lists the
                  files it would write without touching anything.

              scaffold eject-templates [--out .scaffold/templates]
                  Copies the Scriban templates into the project. An ejected copy
                  always wins over the built-in one.

              scaffold eject-rules [--out rules.json]
                  Copies the built-in hint rules so you can edit them.

              scaffold doctor
                  Shows which tool binary is running, when it was built, and
                  which resources are embedded in it. Start here when something
                  behaves as though your last rebuild did not take effect.
            """);

        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"scaffold: {message}");
        return 1;
    }

    // --- inspect ---------------------------------------------------------

    private static async Task<int> InspectAsync(string[] args)
    {
        var options = ArgMap.Parse(args);

        var project = options.Get("--project") ?? FindSingleProject();
        var outputPath = options.Get("--out") ?? "model.json";
        var verbose = options.Has("--verbose");

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(project))!;

        var templateDir = options.Get("--templates")
            ?? Path.Combine(projectDir, "Views", "Shared", "EditorTemplates");

        Console.WriteLine($"scaffold: probing {Path.GetFileName(project)} ...");

        var runner = new ProbeRunner(project, verbose);
        var raw = await runner.RunAsync(
            options.Get("--context"),
            options.Get("--provider"),
            options.Get("--connection"),
            CancellationToken.None);

        var templates = TemplateCatalog.Discover(templateDir);

        if (templates.Count == 0)
        {
            // Not fatal: someone may keep templates elsewhere and pass
            // --templates. But this tool exists to render through them, so
            // silence here would be misleading.
            Console.WriteLine($"scaffold: warning -- no editor templates found under {templateDir}.");
            Console.WriteLine(
                "scaffold: this tool generates views that render through StarterAspMVCEditorTemplates. " +
                "Without them every field falls back to MVC's built-in defaults.");
            Console.WriteLine(
                "scaffold: add the templates with 'dotnet add package StarterAspMVCEditorTemplates', " +
                "or point at them with --templates <dir>.");
        }
        else
        {
            Console.WriteLine($"scaffold: found {templates.Count} editor templates.");
        }

        var rules = HintRules.Load(options.Get("--rules"));
        var fresh = IrBuilder.Build(raw, templates, rules, projectDir);

        JsonNode result;
        MergeReport report;

        if (File.Exists(outputPath))
        {
            await using var existingStream = File.OpenRead(outputPath);
            var existing = await JsonNode.ParseAsync(existingStream)
                ?? throw new ScaffoldException($"{outputPath} is not valid JSON.");

            (result, report) = IrMerge.Merge(existing, fresh);
            Console.WriteLine($"scaffold: merged into existing {outputPath}.");
        }
        else
        {
            result = fresh;
            report = MergeReport.Empty;
        }

        var json = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);

        Console.WriteLine($"scaffold: wrote {outputPath}");

        if (options.Has("--report") || report.HasLosses)
        {
            report.Print();
        }

        PrintSkipped(raw);

        return 0;
    }

    private static void PrintSkipped(JsonNode raw)
    {
        if (raw["skipped"] is not JsonArray skipped || skipped.Count == 0)
        {
            return;
        }

        Console.WriteLine("scaffold: skipped entity types --");

        foreach (var item in skipped)
        {
            Console.WriteLine($"    {item?["entity"]}: {item?["reason"]}");
        }
    }

    private static string FindSingleProject()
    {
        var projects = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");

        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new ScaffoldException("No .csproj in the current directory. Pass --project."),
            _ => throw new ScaffoldException("Several .csproj files here. Pass --project.")
        };
    }

    // --- generate --------------------------------------------------------

    private static int Generate(string[] args)
    {
        var options = ArgMap.Parse(args);
        var from = options.Get("--from") ?? "model.json";

        if (!File.Exists(from))
        {
            throw new ScaffoldException($"{from} not found. Run 'scaffold inspect' first.");
        }

        var dryRun = options.Has("--dry-run");

        if (!dryRun && !options.Has("--force"))
        {
            throw new ScaffoldException(
                "Generation overwrites existing files. Use --dry-run to see the list first, " +
                "then --force once you are happy with model.json.");
        }

        var ir = JsonNode.Parse(File.ReadAllText(from))
            ?? throw new ScaffoldException($"{from} is not valid JSON.");

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(from));

        if (string.IsNullOrEmpty(projectDirectory))
        {
            projectDirectory = Directory.GetCurrentDirectory();
        }

        var ejected = Path.Combine(projectDirectory, ".scaffold", "templates");

        var renderer = new Renderer(Directory.Exists(ejected) ? ejected : null);
        var generator = new Generator(ir, projectDirectory, renderer);

        var files = generator.Generate(options.Get("--entity"));

        foreach (var file in files)
        {
            var marker = file.Existed ? "overwrite" : "new      ";
            var lines = file.Contents.Split('\n').Length;

            Console.WriteLine($"  {marker}  {file.RelativePath}  ({lines} lines)");
        }

        if (dryRun)
        {
            Console.WriteLine();
            Console.WriteLine($"scaffold: {files.Count} files would be written. Nothing was changed.");

            return 0;
        }

        generator.Write(files);

        Console.WriteLine();
        Console.WriteLine($"scaffold: wrote {files.Count} files.");

        if (generator.EnsureViewImports() is { } note)
        {
            Console.WriteLine($"scaffold: {note}");
        }

        return 0;
    }

    // --- eject-templates -------------------------------------------------

    private static int EjectTemplates(string[] args)
    {
        var options = ArgMap.Parse(args);
        var directory = options.Get("--out") ?? Path.Combine(".scaffold", "templates");

        Directory.CreateDirectory(directory);

        foreach (var name in Renderer.BuiltInTemplateNames())
        {
            var path = Path.Combine(directory, name);

            File.WriteAllText(path, EmbeddedFiles.Read($"Scaffold.Templates.{name}"));
            Console.WriteLine($"  {path}");
        }

        Console.WriteLine();
        Console.WriteLine("scaffold: templates ejected. They are picked up automatically from here on.");

        return 0;
    }

    // --- doctor ----------------------------------------------------------

    /// <summary>
    /// Answers "is the installed tool the one I just built?" -- the question
    /// behind almost every confusing failure, because dotnet tool install
    /// caches by version and will happily reinstall a stale package.
    /// </summary>
    private static int Doctor()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();

        Console.WriteLine($"tool assembly : {assembly.Location}");
        Console.WriteLine($"version       : {assembly.GetName().Version}");
        Console.WriteLine($"built         : {File.GetLastWriteTime(assembly.Location):yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"cwd           : {Directory.GetCurrentDirectory()}");
        Console.WriteLine();

        Console.WriteLine("embedded resources:");

        var resources = EmbeddedFiles.All();

        if (resources.Count == 0)
        {
            Console.WriteLine("    (none -- the .txt and .json files were not embedded)");
        }
        else
        {
            foreach (var resource in resources)
            {
                Console.WriteLine($"    {resource}");
            }
        }

        Console.WriteLine();

        var expected = new[] { EmbeddedFiles.ProbeProgram, EmbeddedFiles.ProbeProject, EmbeddedFiles.Rules };
        var missing = expected.Where(e => !resources.Contains(e)).ToList();

        if (missing.Count > 0)
        {
            Console.WriteLine("MISSING:");

            foreach (var name in missing)
            {
                Console.WriteLine($"    {name}");
            }

            Console.WriteLine();
            Console.WriteLine("Rebuild and reinstall -- see 'Rebuilding the tool' in the README.");

            return 1;
        }

        Console.WriteLine("All required resources present.");

        var projects = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");
        Console.WriteLine($"projects here : {(projects.Length == 0 ? "(none)" : string.Join(", ", projects.Select(Path.GetFileName)))}");

        var templateDir = Path.Combine(Directory.GetCurrentDirectory(), "Views", "Shared", "EditorTemplates");
        var catalog = TemplateCatalog.Discover(templateDir);
        Console.WriteLine($"templates     : {catalog.Count} found in {templateDir}");

        return 0;
    }

    // --- eject-rules -----------------------------------------------------

    private static int EjectRules(string[] args)
    {
        var options = ArgMap.Parse(args);
        var outputPath = options.Get("--out") ?? "rules.json";

        File.WriteAllText(outputPath, HintRules.BuiltInJson());
        Console.WriteLine($"scaffold: wrote {outputPath}. It will be picked up automatically from here.");

        return 0;
    }
}

public sealed class ArgMap(IReadOnlyList<string> args)
{
    public static ArgMap Parse(string[] args) => new(args);

    public string? Get(string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public bool Has(string name) => args.Contains(name);
}
