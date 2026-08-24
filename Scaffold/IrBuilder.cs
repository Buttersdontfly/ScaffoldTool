using System.Text.Json.Nodes;

namespace Scaffold;

/// <summary>
/// Turns raw probe facts + rules + the discovered template set into model.json.
/// Nothing here talks to EF or to the file system beyond the template folder.
/// </summary>
public static class IrBuilder
{
    public static JsonNode Build(JsonNode raw, TemplateCatalog templates, HintRules rules, string projectDir)
    {
        var rootNamespace = raw["rootNamespace"]?.GetValue<string>() ?? "App";

        var entities = new JsonArray();

        foreach (var entity in raw["entities"] as JsonArray ?? [])
        {
            if (entity is not null)
            {
                entities.Add(BuildEntity(entity, templates, rules, rootNamespace));
            }
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["rootNamespace"] = rootNamespace,
            ["dbContext"] = raw["dbContext"]?.GetValue<string>(),
            ["provider"] = raw["providerName"]?.GetValue<string>(),
            ["templateDirectory"] = Path.GetRelativePath(projectDir, templates.Directory).Replace('\\', '/'),
            ["viewModelNamespace"] = $"{rootNamespace}.ViewModels",
            ["viewModelFolder"] = "ViewModels",
            ["controllerNamespace"] = $"{rootNamespace}.Controllers",
            ["useDisplayTemplates"] = false,
            ["discoveredTemplates"] = new JsonArray(templates.All().Select(t => (JsonNode?)t).ToArray()),

            // #2 from the design discussion: never inferred from a name suffix.
            ["typeMap"] = new JsonObject(),

            ["entities"] = entities
        };
    }

    private static JsonObject BuildEntity(JsonNode entity, TemplateCatalog templates, HintRules rules, string rootNamespace)
    {
        var name = entity["name"]?.GetValue<string>() ?? "Unknown";

        var candidates = entity["candidateDisplayColumns"] as JsonArray;
        var displayColumn = candidates?.FirstOrDefault()?.GetValue<string>();

        var properties = new JsonArray();
        var order = 0;
        var requiredNavigations = new JsonArray();

        foreach (var property in entity["properties"] as JsonArray ?? [])
        {
            if (property is null)
            {
                continue;
            }

            order += 10;
            properties.Add(BuildProperty(property, templates, rules, order));

            if (property["navigationRequired"]?.GetValue<bool>() == true &&
                property["navigation"]?.GetValue<string>() is { } nav)
            {
                requiredNavigations.Add(nav);
            }
        }

        return new JsonObject
        {
            ["name"] = name,
            ["clrType"] = entity["clrType"]?.GetValue<string>(),
            ["dbSet"] = entity["dbSet"]?.GetValue<string>(),
            ["route"] = entity["dbSet"]?.GetValue<string>() ?? name + "s",
            ["controller"] = (entity["dbSet"]?.GetValue<string>() ?? name + "s") + "Controller",
            ["displayColumn"] = displayColumn,
            ["displayColumnSource"] = displayColumn is null ? "none" : "convention",
            ["key"] = entity["key"]?.DeepClone(),
            ["concurrencyToken"] = entity["concurrencyToken"]?.GetValue<string?>(),

            // Drives construction in the Create action: an entity with required
            // navigations cannot be built from foreign keys alone.
            ["requiredNavigations"] = requiredNavigations,
            ["constructionNote"] = requiredNavigations.Count > 0 ? "requiredNavigations" : "plain",

            ["properties"] = properties,
            ["index"] = BuildIndex(properties, entity, rules, displayColumn)
        };
    }

    private static JsonObject BuildProperty(JsonNode property, TemplateCatalog templates, HintRules rules, int order)
    {
        var name = property["name"]?.GetValue<string>() ?? "Unknown";
        var kind = property["kind"]?.GetValue<string>() ?? "scalar";
        var clrType = property["clrType"]?.GetValue<string>();

        var todos = new List<string>();

        // Neither a collection navigation nor a key is ever rendered by an
        // editor template -- a collection is not model-bound at all, and a key
        // is a hidden field. Resolving one produces a bogus fallback plus a
        // misleading TODO, which is how a Guid key ended up asking for
        // Guid.cshtml.
        var isCollection = kind is "collection";

        // A generated key is a hidden field and needs no template. A NATURAL key
        // is typed in, so it needs one like any other scalar.
        var generatedKey = kind == "key"
            && property["valueGenerated"]?.GetValue<string?>() is "OnAdd" or "OnAddOrUpdate";

        var skipTemplate = isCollection || generatedKey;

        string? template = null;

        if (!skipTemplate)
        {
            var wanted = rules.ChooseTemplate(property);
            var fallback = FallbackTemplate(clrType);
            var (resolved, todo) = templates.Resolve(wanted, fallback, name);

            template = resolved;

            if (todo is not null)
            {
                todos.Add(todo);
            }
        }

        var (required, requiredSource) = isCollection
            ? (false, "collection")
            : rules.IsRequired(property);

        var result = new JsonObject
        {
            ["name"] = name,
            ["clrType"] = clrType,
            ["kind"] = kind,
            ["nullable"] = property["nullable"]?.GetValue<bool>() ?? false,
            ["isKey"] = property["isKey"]?.GetValue<bool>() ?? kind == "key",
            ["valueGenerated"] = property["valueGenerated"]?.GetValue<string?>(),
            ["maxLength"] = property["maxLength"]?.GetValue<int?>(),

            ["editor"] = new JsonObject
            {
                ["uiHint"] = template is not null && IsHintTemplate(template) ? template : null,
                ["dataType"] = property["attributes"]?["dataType"]?.GetValue<string?>(),
                ["template"] = template
            },

            ["display"] = new JsonObject
            {
                ["name"] = property["attributes"]?["display"]?["name"]?.GetValue<string?>() ?? Humanise(name, kind),
                ["prompt"] = property["attributes"]?["display"]?["prompt"]?.GetValue<string?>(),
                ["description"] = property["attributes"]?["display"]?["description"]?.GetValue<string?>()
            },

            // Passed straight through to EditorFor as additionalViewData.
            ["additionalViewData"] = new JsonObject(),

            ["validation"] = new JsonObject
            {
                ["required"] = required,
                ["requiredSource"] = requiredSource,
                ["stringLength"] = property["maxLength"]?.GetValue<int?>(),
                ["range"] = property["attributes"]?["range"]?.DeepClone()
            },

            ["include"] = BuildInclude(property, kind),
            ["search"] = rules.SearchFor(property),
            ["sortable"] = kind is "key" or "scalar" or "reference",
            ["order"] = order
        };

        if (kind == "reference" && property["principal"] is { } principal)
        {
            var principalDisplay = (principal["candidateDisplayColumns"] as JsonArray)
                ?.FirstOrDefault()?.GetValue<string>();

            result["navigation"] = property["navigation"]?.GetValue<string?>();
            result["navigationRequired"] = property["navigationRequired"]?.GetValue<bool>() ?? false;
            result["deleteBehavior"] = property["deleteBehavior"]?.GetValue<string?>();

            result["principal"] = new JsonObject
            {
                ["entity"] = principal["entity"]?.GetValue<string?>(),
                ["key"] = principal["key"]?.GetValue<string?>(),
                ["displayColumn"] = principalDisplay,
                ["displayColumnSource"] = principalDisplay is null ? "none" : "convention"
            };

            result["items"] = new JsonObject
            {
                ["strategy"] = "dropdown",
                ["method"] = $"Get{principal["entity"]?.GetValue<string>()}ItemsAsync",
                ["property"] = $"{name}Items",
                ["orderBy"] = principalDisplay
            };
        }

        if (property["enumMembers"] is JsonArray members && members.Count > 0)
        {
            result["enumMembers"] = members.DeepClone();
        }

        if (kind == "collection")
        {
            // Needed to link a Details page at the child controller. Dropping it
            // left the generator guessing the route from the type name.
            result["targetEntity"] = property["targetEntity"]?.GetValue<string?>();
            result["hasSetter"] = property["hasSetter"]?.GetValue<bool>() ?? false;
            result["deleteBehavior"] = property["deleteBehavior"]?.GetValue<string?>();
            result["foreignKey"] = property["foreignKey"]?.GetValue<string?>();

            if (Str(property["deleteBehavior"]) is "Cascade" or "ClientCascade")
            {
                todos.Add(
                    $"deleting a row cascades to its {name}. The confirmation page shows the " +
                    "count; set deleteBehavior to Restrict in OnModelCreating if that is not intended.");
            }
        }

        if (kind == "manyToMany")
        {
            result["targetEntity"] = property["targetEntity"]?.GetValue<string?>();
            result["joinEntity"] = property["joinEntity"]?.GetValue<string?>();
            result["targetKey"] = property["targetKey"]?.GetValue<string?>();
            result["targetKeyClrType"] = property["targetKeyClrType"]?.GetValue<string?>() ?? "int";
            result["targetDbSet"] = property["targetDbSet"]?.GetValue<string?>()
                ?? Str(property["targetEntity"]) + "s";

            var display = (property["candidateDisplayColumns"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();

            result["principal"] = new JsonObject
            {
                ["entity"] = property["targetEntity"]?.GetValue<string?>(),
                ["key"] = property["targetKey"]?.GetValue<string?>(),
                ["displayColumn"] = display,
                ["displayColumnSource"] = display is null ? "none" : "convention"
            };

            result["items"] = new JsonObject
            {
                ["strategy"] = "checkboxList",
                ["method"] = $"Get{Str(property["targetEntity"])}ItemsAsync",
                ["property"] = $"{name}Items",
                ["orderBy"] = display
            };
        }

        if (kind == "owned")
        {
            result["ownedProperties"] = property["ownedProperties"]?.DeepClone();
            result["viewModelType"] = null;
            todos.Add(
                $"owned type on '{name}' has no typeMap entry. " +
                "Add one to render it with a complex editor template; otherwise it is flattened to scalars.");
        }

        if (kind == "scalar" && clrType == "string" && property["maxLength"] is null)
        {
            todos.Add(
                $"{name} has no configured max length and maps to an unbounded column. " +
                "Set \"maxLength\" here to emit [StringLength].");
        }

        // A list, because a property can hit more than one. Previously each new
        // note overwrote the last and only the final one survived.
        if (todos.Count > 0)
        {
            result["todo"] = new JsonArray(todos.Select(t => (JsonNode?)$"TODO(scaffold): {t}").ToArray());
        }

        return result;
    }

    private static JsonObject BuildInclude(JsonNode property, string kind)
    {
        // A getter-only collection can never be model-bound, so it is read-only
        // everywhere -- shown on Details, absent from Create and Edit.
        var hasSetter = property["hasSetter"]?.GetValue<bool>() ?? true;

        var isKey = property["isKey"]?.GetValue<bool>() ?? kind == "key";
        var generated = property["valueGenerated"]?.GetValue<string?>() is "OnAdd" or "OnAddOrUpdate";

        // A key that is also a foreign key -- the composite key of an explicit
        // join entity -- must be CHOSEN on create, and is immutable afterwards
        // because changing it means a different row. Edit renders it in the
        // hidden key block instead of as a second dropdown.
        if (isKey && kind == "reference")
        {
            return new JsonObject
            {
                ["create"] = true,
                ["edit"] = false,
                ["details"] = true,
                ["list"] = true
            };
        }

        return kind switch
        {
            // A generated key is never typed in. A natural key is, or nothing
            // supplies it and the insert fails.
            "key" => new JsonObject
            {
                ["create"] = !generated,
                ["edit"] = "hidden",
                ["details"] = true,
                ["list"] = true
            },
            "collection" => new JsonObject
            {
                ["create"] = false,
                ["edit"] = false,
                ["details"] = true,
                ["list"] = false
            },
            // A skip navigation IS editable: a CheckboxList of target ids posts
            // back as a flat list and the controller reconciles it. hasSetter is
            // irrelevant here -- `ICollection<Genre> Genres { get; } = []` is
            // mutated with Add and Remove, never assigned.
            "manyToMany" => new JsonObject
            {
                ["create"] = true,
                ["edit"] = true,
                ["details"] = true,
                ["list"] = false
            },
            _ => new JsonObject
            {
                ["create"] = hasSetter,
                ["edit"] = hasSetter,
                ["details"] = true,
                ["list"] = true
            }
        };
    }

    private static JsonObject BuildIndex(JsonArray properties, JsonNode entity, HintRules rules, string? displayColumn)
    {
        var excluded = rules.ExcludedListTemplates;

        var columns = new List<JsonNode?>();

        foreach (var property in properties)
        {
            if (columns.Count >= rules.MaxListColumns)
            {
                break;
            }

            var kind = property?["kind"]?.GetValue<string>();

            if (property?["include"]?["list"]?.GetValue<bool>() != true || kind == "key")
            {
                continue;
            }

            if (excluded.Contains(property?["editor"]?["template"]?.GetValue<string>() ?? ""))
            {
                continue;
            }

            // A grid should show the principal's label, not its id. The list
            // projection resolves Employee.Name, so the column is named for the
            // navigation rather than for the foreign key scalar.
            if (kind == "reference" && property?["navigation"]?.GetValue<string?>() is { Length: > 0 } navigation)
            {
                columns.Add(navigation);
                continue;
            }

            columns.Add(property?["name"]?.GetValue<string>() ?? "");
        }

        // Properties arrive in declaration order, so "first date column" now
        // means the first one the developer wrote rather than the alphabetically
        // earliest. The type name is compared after normalisation because the
        // probe emits DateTime, not System.DateTime.
        var dateColumn = properties
            .FirstOrDefault(p =>
                p?["kind"]?.GetValue<string>() == "scalar" &&
                (p?["clrType"]?.GetValue<string>() ?? "").TrimEnd('?') is "DateTime" or "DateOnly" or "DateTimeOffset")
            ?["name"]?.GetValue<string>();

        var key = entity["key"]?["properties"]?[0]?.GetValue<string>();

        // Lookup tables have no date to sort by, and sorting them by surrogate
        // key shows insertion order. The display column is what a person scans.
        return new JsonObject
        {
            ["defaultSort"] = dateColumn ?? displayColumn ?? key,
            ["defaultSortDescending"] = dateColumn is not null,
            ["pageSize"] = rules.DefaultPageSize,
            ["listColumns"] = new JsonArray(columns.ToArray())
        };
    }

    private static string? Str(JsonNode? node) =>
        node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null
            ? null
            : node.GetValue<string?>();

    private static bool IsHintTemplate(string template) => template is
        "Dropdown" or "RadioGroup" or "CheckboxList" or "Tags" or
        "Color" or "Rating" or "Range" or "FileUpload" or "UserName";

    private static string FallbackTemplate(string? clrType) => clrType?.TrimEnd('?') switch
    {
        "int" => "Int32",
        "long" => "Int64",
        "decimal" => "Decimal",
        "bool" => "Boolean",
        "DateTime" or "System.DateTime" => "DateTime",
        "DateOnly" or "System.DateOnly" => "DateOnly",
        "TimeOnly" or "System.TimeOnly" => "TimeOnly",
        _ => "String"
    };

    /// <summary>
    /// StartAt -> "Start at", IsCompleted -> "Is completed". Only a starting
    /// point: the label is in the IR so it can be edited once and kept.
    /// </summary>
    private static string Humanise(string name, string kind)
    {
        var spaced = System.Text.RegularExpressions.Regex
            .Replace(name, "(?<!^)([A-Z])", " $1")
            .Trim();

        // EmployeeId -> "Employee" reads well for a dropdown label. Doing the
        // same to a primary key turns ActivityId into "Activity", which is the
        // entity name, not the field. Only foreign keys lose the suffix.
        if (kind == "reference")
        {
            spaced = spaced.EndsWith(" Id", StringComparison.Ordinal)
                ? spaced[..^3]
                : spaced;
        }
        else if (spaced.EndsWith(" Id", StringComparison.Ordinal))
        {
            // A key keeps its suffix but as an initialism: "Activity id" reads
            // like a typo in a grid header.
            spaced = spaced[..^3] + " ID";
        }

        if (spaced.Length == 0)
        {
            return name;
        }

        // Lowercase the tail so "Is Completed" becomes "Is completed", but keep
        // a trailing initialism intact.
        var tail = spaced[1..];
        var suffix = "";

        if (tail.EndsWith(" ID", StringComparison.Ordinal))
        {
            tail = tail[..^3];
            suffix = " ID";
        }

        return char.ToUpperInvariant(spaced[0]) + tail.ToLowerInvariant() + suffix;
    }
}
