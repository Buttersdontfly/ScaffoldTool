using System.Text.Json.Nodes;

namespace Scaffold;

public sealed record GeneratedFile(string RelativePath, string Contents, bool Existed);

/// <summary>
/// Decides what gets written where. Rendering is in Renderer, the shape of the
/// data is in Planner; this only maps templates to output paths.
/// </summary>
public sealed class Generator(JsonNode ir, string projectDirectory, Renderer renderer)
{
    public IReadOnlyList<GeneratedFile> Generate(string? onlyEntity)
    {
        var files = new List<GeneratedFile>();

        var viewModelFolder = ir["viewModelFolder"]?.GetValue<string>() ?? "ViewModels";

        foreach (var entity in ir["entities"] as JsonArray ?? [])
        {
            if (entity is null)
            {
                continue;
            }

            var name = entity["name"]?.GetValue<string>();

            if (name is null || (onlyEntity is not null && !string.Equals(name, onlyEntity, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var plan = Planner.Plan(ir, entity);
            var route = Get(plan, "entity", "route") ?? name + "s";

            var createModel = Get(plan, "createModel", "className")!;
            var editModel = Get(plan, "editModel", "className")!;
            var searchModel = Get(plan, "searchModel", "className")!;

            // ViewModels
            Add(files, $"{viewModelFolder}/{createModel}.cs",
                renderer.Render("ViewModelForm.scriban", plan, ("model", plan["createModel"])));

            Add(files, $"{viewModelFolder}/{editModel}.cs",
                renderer.Render("ViewModelForm.scriban", plan, ("model", plan["editModel"])));

            Add(files, $"{viewModelFolder}/{Get(plan, "listItemModel", "className")}.cs",
                renderer.Render("ViewModelListItem.scriban", plan, ("model", plan["listItemModel"])));

            Add(files, $"{viewModelFolder}/{Get(plan, "detailsModel", "className")}.cs",
                renderer.Render("ViewModelDetails.scriban", plan, ("model", plan["detailsModel"])));

            Add(files, $"{viewModelFolder}/{searchModel}.cs",
                renderer.Render("ViewModelSearch.scriban", plan, ("model", plan["searchModel"])));

            Add(files, $"{viewModelFolder}/{Get(plan, "indexModel", "className")}.cs",
                renderer.Render("ViewModelIndex.scriban", plan, ("model", plan["indexModel"])));

            // Controller
            Add(files, $"Controllers/{Get(plan, "controller", "className")}.cs",
                renderer.Render("Controller.scriban", plan));

            // Views. Create and Edit share one template, differing only in the
            // model they bind and the action they post to.
            Add(files, $"Views/{route}/Create.cshtml",
                renderer.Render("ViewForm.scriban", plan,
                    ("model", plan["createModel"]),
                    ("formAction", "Create"),
                    ("pageTitle", $"New {Get(plan, "entity", "titleLower")}")));

            Add(files, $"Views/{route}/Edit.cshtml",
                renderer.Render("ViewForm.scriban", plan,
                    ("model", plan["editModel"]),
                    ("formAction", "Edit"),
                    ("pageTitle", $"Edit {Get(plan, "entity", "titleLower")}")));

            Add(files, $"Views/{route}/Index.cshtml", renderer.Render("ViewIndex.scriban", plan));
            Add(files, $"Views/{route}/Details.cshtml", renderer.Render("ViewDetails.scriban", plan));
            Add(files, $"Views/{route}/Delete.cshtml", renderer.Render("ViewDelete.scriban", plan));
        }

        if (files.Count == 0)
        {
            throw new ScaffoldException(onlyEntity is null
                ? "model.json contains no entities."
                : $"No entity named '{onlyEntity}' in model.json.");
        }

        return files;
    }

    /// <summary>
    /// Writes the rendered files. _ViewImports is handled separately, after the
    /// dry-run early return, so --dry-run never touches disk.
    /// </summary>
    public void Write(IReadOnlyList<GeneratedFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(projectDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Contents);
        }
    }

    /// <summary>
    /// Generated views reference the ViewModel namespace, and a missing using in
    /// _ViewImports.cshtml fails at request time rather than at build time --
    /// the worst place to find out. The line is appended if absent rather than
    /// merely warned about, since there is exactly one correct fix.
    /// </summary>
    public string? EnsureViewImports()
    {
        var viewModelNamespace = ir["viewModelNamespace"]?.GetValue<string>();

        if (viewModelNamespace is null)
        {
            return null;
        }

        var path = Path.Combine(projectDirectory, "Views", "_ViewImports.cshtml");
        var directive = $"@using {viewModelNamespace}";

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, directive + Environment.NewLine);

            return $"created Views/_ViewImports.cshtml with '{directive}'.";
        }

        var lines = File.ReadAllLines(path);

        if (lines.Any(l => l.Trim() == directive))
        {
            return null;
        }

        // Appended after the last @using so the file keeps its grouping, rather
        // than at the end where @inject and @addTagHelper lines usually sit.
        var lastUsing = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("@using", StringComparison.Ordinal));
        var updated = lines.ToList();

        updated.Insert(lastUsing >= 0 ? lastUsing + 1 : 0, directive);

        File.WriteAllLines(path, updated);

        return $"added '{directive}' to Views/_ViewImports.cshtml.";
    }

    private void Add(List<GeneratedFile> files, string relativePath, string contents)
    {
        var full = Path.Combine(projectDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        files.Add(new GeneratedFile(relativePath, contents, File.Exists(full)));
    }

    private static string? Get(Dictionary<string, object?> plan, string section, string key) =>
        plan[section] is Dictionary<string, object?> map && map.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
}
