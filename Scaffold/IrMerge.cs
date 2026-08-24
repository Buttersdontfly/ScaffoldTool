using System.Text.Json.Nodes;

namespace Scaffold;

public sealed class MergeReport
{
    public List<string> DroppedProperties { get; } = [];
    public List<string> DroppedEntities { get; } = [];
    public List<string> AddedProperties { get; } = [];
    public List<string> AddedEntities { get; } = [];

    public static MergeReport Empty => new();

    public bool HasLosses => DroppedProperties.Count > 0 || DroppedEntities.Count > 0;

    public void Print()
    {
        void Section(string title, List<string> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            Console.WriteLine($"scaffold: {title}");

            foreach (var item in items)
            {
                Console.WriteLine($"    {item}");
            }
        }

        Section("added entities --", AddedEntities);
        Section("added properties --", AddedProperties);
        Section("dropped entities (edits lost) --", DroppedEntities);
        Section("dropped properties (edits lost) --", DroppedProperties);

        if (HasLosses)
        {
            Console.WriteLine(
                "scaffold: a rename looks like a drop plus an add. If one of these was a rename, " +
                "copy the old settings across before regenerating.");
        }
    }
}

/// <summary>
/// Re-running inspect after a migration must not wipe hand edits. Schema facts
/// come from the fresh probe; intent stays from the existing file.
/// </summary>
public static class IrMerge
{
    /// <summary>
    /// Fields the user owns. Everything else is overwritten from the probe,
    /// because the probe is the authority on what the schema actually is.
    /// </summary>
    private static readonly string[] PreservedPropertyFields =
    [
        "display",
        "additionalViewData",
        "search",
        "items",
        "include",
        "order",
        "sortable",
        "viewModelType",
        "editor"
    ];

    private static readonly string[] PreservedEntityFields =
    [
        "route",
        "controller",
        "displayColumn",
        "index"
    ];

    private static readonly string[] PreservedRootFields =
    [
        "typeMap",
        "viewModelNamespace",
        "viewModelFolder",
        "controllerNamespace",
        "useDisplayTemplates"
    ];

    public static (JsonNode Result, MergeReport Report) Merge(JsonNode existing, JsonNode fresh)
    {
        var report = new MergeReport();
        var result = fresh.DeepClone();

        foreach (var field in PreservedRootFields)
        {
            if (existing[field] is { } value)
            {
                result[field] = value.DeepClone();
            }
        }

        var oldEntities = Index(existing["entities"] as JsonArray, "name");
        var newEntities = result["entities"] as JsonArray ?? [];

        foreach (var entity in newEntities)
        {
            var name = entity?["name"]?.GetValue<string>();

            if (name is null || entity is null)
            {
                continue;
            }

            if (!oldEntities.TryGetValue(name, out var old))
            {
                report.AddedEntities.Add(name);
                continue;
            }

            MergeEntity(old, entity, report);
            oldEntities.Remove(name);
        }

        foreach (var name in oldEntities.Keys)
        {
            report.DroppedEntities.Add(name);
        }

        return (result, report);
    }

    private static void MergeEntity(JsonNode old, JsonNode fresh, MergeReport report)
    {
        var entityName = fresh["name"]?.GetValue<string>() ?? "?";

        foreach (var field in PreservedEntityFields)
        {
            if (old[field] is { } value)
            {
                fresh[field] = value.DeepClone();
            }
        }

        var oldProperties = Index(old["properties"] as JsonArray, "name");

        foreach (var property in fresh["properties"] as JsonArray ?? [])
        {
            var name = property?["name"]?.GetValue<string>();

            if (name is null || property is null)
            {
                continue;
            }

            if (!oldProperties.TryGetValue(name, out var oldProperty))
            {
                report.AddedProperties.Add($"{entityName}.{name}");
                continue;
            }

            MergeProperty(oldProperty, property);
            oldProperties.Remove(name);
        }

        foreach (var name in oldProperties.Keys)
        {
            report.DroppedProperties.Add($"{entityName}.{name}");
        }
    }

    private static void MergeProperty(JsonNode old, JsonNode fresh)
    {
        foreach (var field in PreservedPropertyFields)
        {
            if (old[field] is not { } value)
            {
                continue;
            }

            // The editor block is special: a hand-set template is intent and is
            // kept, but a template the tool picked should follow current rules.
            if (field == "editor")
            {
                MergeEditor(value, fresh["editor"]);
                continue;
            }

            fresh[field] = value.DeepClone();
        }

        // Requiredness is re-derived every time -- it is a schema fact, and a
        // stale answer here silently produces wrong validation attributes.
        // An explicit override survives via "requiredOverride".
        if (old["validation"]?["requiredOverride"] is { } overridden)
        {
            fresh["validation"]!["requiredOverride"] = overridden.DeepClone();
        }
    }

    private static void MergeEditor(JsonNode old, JsonNode? fresh)
    {
        if (fresh is null)
        {
            return;
        }

        if (old["templateOverride"]?.GetValue<string?>() is { Length: > 0 } overridden)
        {
            fresh["templateOverride"] = overridden;
            fresh["template"] = overridden;
        }

        if (old["additionalViewData"] is { } extra)
        {
            fresh["additionalViewData"] = extra.DeepClone();
        }
    }

    private static Dictionary<string, JsonNode> Index(JsonArray? array, string keyField)
    {
        var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        foreach (var item in array ?? [])
        {
            if (item?[keyField]?.GetValue<string>() is { } key)
            {
                map[key] = item.DeepClone();
            }
        }

        return map;
    }
}
