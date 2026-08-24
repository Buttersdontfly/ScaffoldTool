using System.Text.Json.Nodes;
using Scriban;
using Scriban.Runtime;

namespace Scaffold;

/// <summary>
/// Scriban rather than T4 or Razor-generating-Razor: its {{ }} delimiters do not
/// collide with Razor's @, which is the thing that makes the alternatives
/// miserable when the output is itself a .cshtml file.
/// </summary>
public sealed class Renderer(string? ejectedTemplateDirectory)
{
    private readonly Dictionary<string, Template> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Render(string templateName, Dictionary<string, object?> plan)
    {
        var template = Load(templateName);

        var globals = new ScriptObject();

        foreach (var (key, value) in plan)
        {
            globals[key] = Convert(value);
        }

        var context = new TemplateContext
        {
            // Keys are used exactly as written, so templates read the same
            // camelCase names that appear in model.json.
            MemberRenamer = member => member.Name,
            StrictVariables = false
        };

        context.PushGlobal(globals);

        var output = template.Render(context);

        if (template.HasErrors)
        {
            throw new ScaffoldException(
                $"Template '{templateName}' failed:{Environment.NewLine}{string.Join(Environment.NewLine, template.Messages)}");
        }

        return Normalise(output);
    }

    /// <summary>
    /// Adds a per-render value on top of the plan, for templates used twice with
    /// a different subject -- Create and Edit share ViewForm.scriban.
    /// </summary>
    public string Render(string templateName, Dictionary<string, object?> plan, params (string Key, object? Value)[] extras)
    {
        var merged = new Dictionary<string, object?>(plan);

        foreach (var (key, value) in extras)
        {
            merged[key] = value;
        }

        return Render(templateName, merged);
    }

    private Template Load(string name)
    {
        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var text = ReadTemplate(name);
        var template = Template.Parse(text, name);

        if (template.HasErrors)
        {
            throw new ScaffoldException(
                $"Template '{name}' does not parse:{Environment.NewLine}{string.Join(Environment.NewLine, template.Messages)}");
        }

        _cache[name] = template;

        return template;
    }

    private string ReadTemplate(string name)
    {
        // An ejected copy always wins, so editing one is enough to change output
        // without rebuilding the tool.
        if (ejectedTemplateDirectory is not null)
        {
            var path = Path.Combine(ejectedTemplateDirectory, name);

            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        return EmbeddedFiles.Read($"Scaffold.Templates.{name}");
    }

    public static IReadOnlyList<string> BuiltInTemplateNames() =>
        EmbeddedFiles.All()
            .Where(n => n.StartsWith("Scaffold.Templates.", StringComparison.Ordinal))
            .Select(n => n["Scaffold.Templates.".Length..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Scriban resolves members on ScriptObject and ScriptArray directly.
    /// Nested dictionaries and lists are converted so a template can walk the
    /// whole plan without special cases.
    /// </summary>
    private static object? Convert(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case string or bool or int or long or double or decimal:
                return value;

            case IDictionary<string, object?> dictionary:
            {
                var script = new ScriptObject();

                foreach (var (key, item) in dictionary)
                {
                    script[key] = Convert(item);
                }

                return script;
            }

            case System.Collections.IEnumerable enumerable:
            {
                var array = new ScriptArray();

                foreach (var item in enumerable)
                {
                    array.Add(Convert(item));
                }

                return array;
            }

            case JsonNode node:
                return node.ToJsonString();

            default:
                return value;
        }
    }

    /// <summary>
    /// Whitespace control in Scriban still leaves runs of blank lines where a
    /// conditional block collapsed. Generated code should not look generated.
    /// </summary>
    private static string Normalise(string output)
    {
        var normalised = output.Replace("\r\n", "\n");

        while (normalised.Contains("\n\n\n"))
        {
            normalised = normalised.Replace("\n\n\n", "\n\n");
        }

        normalised = normalised.TrimEnd() + "\n";

        return normalised.Replace("\n", Environment.NewLine);
    }
}
