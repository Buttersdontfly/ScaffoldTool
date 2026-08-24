using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scaffold;

/// <summary>
/// Applies rules.json to the raw facts the probe collected. Kept out of the
/// probe on purpose: the probe reports what EF knows, this decides what it
/// means. Only this half changes when a convention is wrong.
/// </summary>
public sealed class HintRules(JsonNode root)
{
    public static HintRules Load(string? path)
    {
        // A rules.json next to the working directory wins over the built-in one.
        var candidate = path ?? (File.Exists("rules.json") ? "rules.json" : null);

        var json = candidate is not null
            ? File.ReadAllText(candidate)
            : BuiltInJson();

        return new HintRules(JsonNode.Parse(json)
            ?? throw new ScaffoldException("rules.json is not valid JSON."));
    }

    public static string BuiltInJson() => EmbeddedFiles.Read(EmbeddedFiles.Rules);

    // --- template selection ----------------------------------------------

    /// <summary>
    /// Mirrors MVC's own lookup order at generation time: explicit override,
    /// then [UIHint], then [DataType], then structure, then name, then type.
    /// </summary>
    public string? ChooseTemplate(JsonNode property)
    {
        var attributes = property["attributes"];

        if (attributes?["uiHint"]?.GetValue<string?>() is { Length: > 0 } uiHint)
        {
            return uiHint;
        }

        if (attributes?["dataType"]?.GetValue<string?>() is { Length: > 0 } dataType)
        {
            var match = Array(root["attributeRules"])
                .FirstOrDefault(r => r?["when"]?["dataType"]?.GetValue<string>() == dataType);

            if (match?["template"]?.GetValue<string>() is { } template)
            {
                return template;
            }
        }

        if (Structural(property) is { } structural)
        {
            return structural;
        }

        if (ByName(property) is { } byName)
        {
            return byName;
        }

        return TypeTemplate(property["clrType"]?.GetValue<string>());
    }

    private string? Structural(JsonNode property)
    {
        if (property["kind"]?.GetValue<string>() == "reference")
        {
            return "Dropdown";
        }

        if (property["isEnum"]?.GetValue<bool>() == true)
        {
            var memberCount = (property["enumMembers"] as JsonArray)?.Count ?? 0;
            var threshold = root["structuralRules"]?[1]?["when"]?["enumMemberCountAtMost"]?.GetValue<int>() ?? 4;

            return memberCount > 0 && memberCount <= threshold ? "RadioGroup" : "Enum";
        }

        var clr = property["clrType"]?.GetValue<string>();

        if (clr == "IFormFile" || clr == "IFormFile?")
        {
            return "FileUpload";
        }

        if (clr is "List<string>" or "ICollection<string>" or "IEnumerable<string>")
        {
            return "Tags";
        }

        return null;
    }

    private string? ByName(JsonNode property)
    {
        var name = property["name"]?.GetValue<string>() ?? "";
        var clr = property["clrType"]?.GetValue<string>() ?? "";
        var maxLength = property["maxLength"]?.GetValue<int?>();
        var hasRange = property["attributes"]?["range"] is not null;

        foreach (var rule in Array(root["nameConventionRules"]))
        {
            var when = rule?["when"];

            if (when is null)
            {
                continue;
            }

            if (when["clrType"]?.GetValue<string>() is { } wantType &&
                !clr.TrimEnd('?').Equals(wantType, StringComparison.Ordinal))
            {
                continue;
            }

            if (when["nameMatches"]?.GetValue<string>() is { } pattern &&
                !Regex.IsMatch(name, pattern))
            {
                continue;
            }

            if (when["maxLengthAtLeast"]?.GetValue<int>() is { } minLength &&
                (maxLength is null || maxLength < minLength))
            {
                continue;
            }

            if (when["hasRange"]?.GetValue<bool>() == true && !hasRange)
            {
                continue;
            }

            return rule?["template"]?.GetValue<string?>();
        }

        return null;
    }

    /// <summary>
    /// Nullable resolves to the underlying type's template: MVC looks up
    /// Nullable&lt;T&gt; as T, which is why DateTime? lands on DateTime.cshtml.
    /// </summary>
    private static string? TypeTemplate(string? clrType) => clrType?.TrimEnd('?') switch
    {
        "string" => "String",
        "int" => "Int32",
        "long" => "Int64",
        "decimal" => "Decimal",
        "double" => "Double",
        "bool" => "Boolean",
        "DateTime" or "System.DateTime" => "DateTime",
        "DateOnly" or "System.DateOnly" => "DateOnly",
        "TimeOnly" or "System.TimeOnly" => "TimeOnly",
        null => null,
        var other => other.Contains('.') ? other[(other.LastIndexOf('.') + 1)..] : other
    };

    // --- requiredness -----------------------------------------------------

    /// <summary>
    /// EF marks every non-nullable property required, but the initializer says
    /// what was meant: `= null!` means "must be set", `= string.Empty` means an
    /// empty value is normal and [Required] on the form would be wrong.
    /// </summary>
    public (bool Required, string Source) IsRequired(JsonNode property)
    {
        if (property["attributes"]?["required"]?.GetValue<bool>() == true)
        {
            return (true, "attribute");
        }

        if (property["nullable"]?.GetValue<bool>() == true)
        {
            return (false, "efModel");
        }

        var clr = property["clrType"]?.GetValue<string>();

        if (clr != "string")
        {
            return (true, "efModel");
        }

        var known = property["initializerKnown"]?.GetValue<bool>() == true;
        var initializer = property["initializer"]?.GetValue<string?>();

        if (known && initializer is "")
        {
            return (false, "initializer");
        }

        return (true, known ? "initializer" : "default");
    }

    // --- search -----------------------------------------------------------

    public JsonNode? SearchFor(JsonNode property)
    {
        var kind = property["kind"]?.GetValue<string>();

        if (kind is "key" or "collection" or "owned")
        {
            return new JsonObject { ["enabled"] = false };
        }

        if (kind == "reference")
        {
            return new JsonObject
            {
                ["enabled"] = true,
                ["operator"] = "equals",
                ["template"] = "Dropdown"
            };
        }

        if (property["isEnum"]?.GetValue<bool>() == true)
        {
            return new JsonObject
            {
                ["enabled"] = true,
                ["operator"] = "equals",
                ["template"] = "Enum"
            };
        }

        var clr = property["clrType"]?.GetValue<string>()?.TrimEnd('?');
        var byType = root["search"]?["byClrType"]?[clr ?? ""];

        if (byType is null)
        {
            return new JsonObject { ["enabled"] = false };
        }

        var result = byType.DeepClone();

        // A rule that names an operator is by definition enabled. Relying on the
        // flag being present in rules.json meant one missing line silently
        // dropped the field from the search panel.
        result["enabled"] = true;

        return result;
    }

    public int MaxListColumns => root["index"]?["maxListColumns"]?.GetValue<int>() ?? 7;

    public int DefaultPageSize => root["index"]?["defaultPageSize"]?.GetValue<int>() ?? 25;

    public IReadOnlyList<string> ExcludedListTemplates =>
        Array(root["index"]?["excludeFromListTemplates"])
            .Select(n => n?.GetValue<string>() ?? "")
            .Where(n => n.Length > 0)
            .ToList();

    private static IEnumerable<JsonNode?> Array(JsonNode? node) =>
        node as JsonArray ?? Enumerable.Empty<JsonNode?>();
}
