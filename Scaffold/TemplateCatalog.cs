namespace Scaffold;

/// <summary>
/// The set of editor templates that actually exist in the project.
///
/// Every hint decision is checked against this set. A rule that asks for a
/// template with no matching .cshtml degrades to the type-name template and
/// leaves a TODO comment, so an unrecognised shape never blocks generation --
/// a working 90% beats a failed 100%.
/// </summary>
public sealed class TemplateCatalog(IReadOnlySet<string> names, string directory)
{
    public int Count => names.Count;

    public string Directory => directory;

    public static TemplateCatalog Discover(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return new TemplateCatalog(new HashSet<string>(StringComparer.OrdinalIgnoreCase), directory);
        }

        var names = System.IO.Directory
            .EnumerateFiles(directory, "*.cshtml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n) && !n!.StartsWith('_'))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new TemplateCatalog(names, directory);
    }

    public bool Has(string? template) =>
        !string.IsNullOrWhiteSpace(template) && names.Contains(template);

    public IReadOnlyList<string> All() => names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Returns the requested template if it exists, otherwise the fallback, and
    /// reports which happened so the caller can attach a TODO.
    /// </summary>
    public (string? Template, string? Todo) Resolve(string? requested, string fallback, string propertyName)
    {
        if (requested is null)
        {
            return (Has(fallback) ? fallback : null, null);
        }

        if (Has(requested))
        {
            return (requested, null);
        }

        // Bare filename, not the absolute path: model.json is committed and an
        // absolute path from one machine is noise on every other one.
        var todo =
            $"{propertyName} wanted the '{requested}' editor template, " +
            $"but {requested}.cshtml is not in the EditorTemplates folder. Falling back to '{fallback}'.";

        return (Has(fallback) ? fallback : null, todo);
    }
}
