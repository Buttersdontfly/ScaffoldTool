using System.Text.Json.Nodes;

namespace Scaffold;

/// <summary>
/// Turns model.json into flat data the templates can emit without deciding
/// anything. Every judgement -- which C# type, which attributes, which LINQ
/// expression -- happens here, so a template stays readable and a person editing
/// one cannot accidentally change semantics.
/// </summary>
public static class Planner
{
    public static Dictionary<string, object?> Plan(JsonNode ir, JsonNode entity)
    {
        var name = Str(entity["name"]) ?? "Unknown";
        var dbSet = Str(entity["dbSet"]) ?? name + "s";
        var route = Str(entity["route"]) ?? dbSet;
        var keys = KeyParts(entity);
        var keyName = keys[0].Name;
        var keyType = keys[0].ClrType;

        var properties = (entity["properties"] as JsonArray ?? []).Where(p => p is not null).Select(p => p!).ToList();

        var references = properties.Where(p => Str(p["kind"]) == "reference").ToList();
        var collections = properties.Where(p => Str(p["kind"]) == "collection").ToList();
        var manyToManyNavigations = properties.Where(p => Str(p["kind"]) == "manyToMany").ToList();

        return new Dictionary<string, object?>
        {
            ["rootNamespace"] = Str(ir["rootNamespace"]),
            ["viewModelNamespace"] = Str(ir["viewModelNamespace"]),
            ["controllerNamespace"] = Str(ir["controllerNamespace"]),
            ["dbContext"] = Str(ir["dbContext"]),
            ["dbContextType"] = Str(ir["dbContext"])?.Split('.').Last(),
            ["entityNamespace"] = NamespaceOf(Str(entity["clrType"])),

            // Deduped: AppDbContext and the entities often share a namespace, and
            // a repeated using directive is CS0105 -- an error under
            // TreatWarningsAsErrors.
            ["usings"] = new[]
                {
                    NamespaceOf(Str(ir["dbContext"])),
                    NamespaceOf(Str(entity["clrType"])),
                    Str(ir["viewModelNamespace"])
                }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(object? (n) => n)
                .ToList(),

            ["entity"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["dbSet"] = dbSet,
                ["route"] = route,
                ["controller"] = Str(entity["controller"]) ?? dbSet + "Controller",
                ["keyName"] = keyName,
                ["keyType"] = keyType,
                ["isComposite"] = keys.Count > 1,

                ["keys"] = keys.Select(object? (k) => new Dictionary<string, object?>
                {
                    ["name"] = k.Name,
                    ["csType"] = k.ClrType,
                    ["parameter"] = Camel(k.Name),

                    // asp-route-shelfId="@item.ShelfId". With the default
                    // {controller}/{action}/{id?} route, anything beyond the
                    // single id lands in the query string, which is fine.
                    ["routeAttribute"] = $"asp-route-{Camel(k.Name)}"
                }).ToList(),

                ["displayColumn"] = Str(entity["displayColumn"]),
                ["title"] = Sentence(name),
                ["titleLower"] = Sentence(name).ToLowerInvariant(),
                ["pluralTitle"] = Sentence(dbSet)
            },

            ["createModel"] = FormModel(name + "CreateModel", entity, properties, "create", keys),
            ["editModel"] = FormModel(name + "EditModel", entity, properties, "edit", keys),
            ["listItemModel"] = ListItemModel(name, entity, properties, keys),
            ["detailsModel"] = DetailsModel(name, entity, properties, keys),
            ["searchModel"] = SearchModel(name, properties),
            ["indexModel"] = new Dictionary<string, object?> { ["className"] = name + "IndexModel" },

            ["controller"] = Controller(ir, entity, properties, references, collections, name, dbSet, keys),

            ["itemProviders"] = ItemProviders(references.Concat(manyToManyNavigations).ToList()),

            // Gates the entire select-list section of the controller. A skip
            // navigation needs a provider just as much as a foreign key does, so
            // counting references alone left an entity whose only relationship
            // is many-to-many with no provider at all.
            ["hasItems"] = references.Count + manyToManyNavigations.Count > 0
        };
    }

    // --- ViewModels -------------------------------------------------------

    private static Dictionary<string, object?> FormModel(
        string className, JsonNode entity, List<JsonNode> properties, string surface, List<KeyPart> keys)
    {
        var fields = new List<object?>();
        var itemsProperties = new List<object?>();

        foreach (var property in properties.OrderBy(Order))
        {
            var include = property["include"]?[surface];
            var kind = Str(property["kind"]) ?? "scalar";

            if (kind == "collection")
            {
                continue;
            }

            // Keys are rendered as hidden fields from model.keys, so they are
            // not editors -- unless the key is natural rather than generated, in
            // which case create has to ask for it.
            if (kind == "key" && !(surface == "create" && Bool(property["include"]?["create"])))
            {
                continue;
            }

            if (kind == "manyToMany")
            {
                var field = Field(property);

                fields.Add(field);
                itemsProperties.Add(field["itemsProperty"]);

                continue;
            }

            if (include is null || (include.GetValueKind() == System.Text.Json.JsonValueKind.False))
            {
                continue;
            }

            fields.Add(Field(property));

            if (kind == "reference")
            {
                itemsProperties.Add(Str(property["items"]?["property"]) ?? Str(property["name"]) + "Items");
            }
        }

        return new Dictionary<string, object?>
        {
            ["className"] = className,
            ["surface"] = surface,
            ["includeKey"] = surface == "edit",
            ["keys"] = keys.Select(object? (k) => new Dictionary<string, object?>
            {
                ["name"] = k.Name,
                ["csType"] = k.ClrType
            }).ToList(),
            ["concurrencyToken"] = surface == "edit" ? Str(entity["concurrencyToken"]) : null,
            ["fields"] = fields,
            ["itemsProperties"] = itemsProperties
        };
    }

    private static Dictionary<string, object?> Field(JsonNode property)
    {
        var name = Str(property["name"])!;
        var kind = Str(property["kind"]) ?? "scalar";

        if (kind == "manyToMany")
        {
            return ManyToManyField(property);
        }

        var clr = Str(property["clrType"]) ?? "string";
        var nullable = Bool(property["nullable"]);
        var required = RequiredOf(property);
        var template = Str(property["editor"]?["template"]);
        var maxLength = Int(property["maxLength"]);

        var attributes = new List<object?>();

        if (Str(property["display"]?["name"]) is { Length: > 0 } display)
        {
            var prompt = Str(property["display"]?["prompt"]);
            var description = Str(property["display"]?["description"]);

            var parts = new List<string> { $"Name = \"{Escape(display)}\"" };

            if (prompt is { Length: > 0 })
            {
                parts.Add($"Prompt = \"{Escape(prompt)}\"");
            }

            if (description is { Length: > 0 })
            {
                parts.Add($"Description = \"{Escape(description)}\"");
            }

            attributes.Add($"Display({string.Join(", ", parts)})");
        }

        if (Str(property["editor"]?["uiHint"]) is { Length: > 0 } hint)
        {
            attributes.Add($"UIHint(\"{hint}\")");
        }

        if (Str(property["editor"]?["dataType"]) is { Length: > 0 } dataType)
        {
            attributes.Add($"DataType(DataType.{dataType})");
        }

        if (required)
        {
            attributes.Add(kind == "reference"
                ? $"Required(ErrorMessage = \"Select a {Sentence(name).ToLowerInvariant().Replace(" id", "")}.\")"
                : "Required");
        }

        if (kind == "reference" && required && clr is "int" or "long")
        {
            // A non-nullable int defaults to 0 and would pass [Required] on its
            // own, so an unchosen dropdown has to be rejected by range.
            attributes.Add($"Range(1, {clr}.MaxValue, ErrorMessage = \"Select a {Sentence(name).ToLowerInvariant().Replace(" id", "")}.\")");
        }

        if (clr == "string" && maxLength is int length)
        {
            attributes.Add($"StringLength({length})");
        }

        if (property["validation"]?["range"] is { } range &&
            Str(range["minimum"]) is { } min && Str(range["maximum"]) is { } max)
        {
            attributes.Add($"Range({min}, {max})");
        }

        var csType = CsType(clr, nullable);

        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["kind"] = kind,
            ["csType"] = csType,
            ["template"] = template,
            ["required"] = required,
            ["attributes"] = attributes,
            ["initializer"] = InitializerFor(csType),
            ["itemsProperty"] = kind == "reference"
                ? Str(property["items"]?["property"]) ?? name + "Items"
                : null,
            ["itemsMethod"] = kind == "reference"
                ? Str(property["items"]?["method"]) ?? $"Get{Str(property["principal"]?["entity"])}ItemsAsync"
                : null,
            ["placeholder"] = kind == "reference"
                ? $"-- select {Sentence(name).ToLowerInvariant().Replace(" id", "")} --"
                : null,
            ["todos"] = ToList(property["todo"])
        };
    }

    /// <summary>
    /// A skip navigation posts back as a flat list of target ids on a
    /// CheckboxList, which is the only shape a form can express. Named after the
    /// TARGET entity rather than the navigation: GenreIds reads better than
    /// GenresIds, and matches the key it actually holds.
    /// </summary>
    private static Dictionary<string, object?> ManyToManyField(JsonNode property)
    {
        var navigation = Str(property["name"])!;
        var target = Str(property["targetEntity"]) ?? navigation;
        var keyType = Str(property["targetKeyClrType"]) ?? "int";

        var name = target + "Ids";
        var label = Str(property["display"]?["name"]) ?? Sentence(navigation);

        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["kind"] = "manyToMany",
            ["csType"] = $"List<{keyType}>",
            ["template"] = "CheckboxList",
            ["required"] = false,
            ["attributes"] = new List<object?>
            {
                $"Display(Name = \"{Escape(label)}\")",
                "UIHint(\"CheckboxList\")"
            },
            ["initializer"] = " = [];",
            ["itemsProperty"] = target + "Items",
            ["itemsMethod"] = Str(property["items"]?["method"]) ?? $"Get{target}ItemsAsync",
            ["placeholder"] = null,
            ["todos"] = ToList(property["todo"])
        };
    }

    private static Dictionary<string, object?> ListItemModel(
        string name, JsonNode entity, List<JsonNode> properties, List<KeyPart> keys)
    {
        var columns = new List<object?>();

        foreach (var column in entity["index"]?["listColumns"] as JsonArray ?? [])
        {
            var columnName = Str(column);

            if (columnName is null)
            {
                continue;
            }

            var reference = properties.FirstOrDefault(p => Str(p["navigation"]) == columnName);

            if (reference is not null)
            {
                // The grid shows the principal's label, so the projection walks
                // the navigation and the property holds a plain string.
                var display = Str(reference["principal"]?["displayColumn"]) ?? "Id";

                columns.Add(new Dictionary<string, object?>
                {
                    ["name"] = columnName + "Name",
                    ["csType"] = "string",
                    ["initializer"] = " = string.Empty;",
                    ["displayName"] = Sentence(columnName),
                    ["source"] = $"x.{columnName}.{display}",
                    ["sortKey"] = columnName.ToLowerInvariant(),
                    ["sortExpr"] = $"x.{columnName}.{display}",
                    ["isBool"] = false,
                    ["isDate"] = false,
                    ["nullable"] = false
                });

                continue;
            }

            var property = properties.FirstOrDefault(p => Str(p["name"]) == columnName);

            if (property is null)
            {
                continue;
            }

            var clr = Str(property["clrType"]) ?? "string";
            var nullable = Bool(property["nullable"]);
            var csType = CsType(clr, nullable);

            columns.Add(new Dictionary<string, object?>
            {
                ["name"] = columnName,
                ["csType"] = csType,
                ["initializer"] = InitializerFor(csType),
                ["displayName"] = Str(property["display"]?["name"]) ?? Sentence(columnName),
                ["source"] = $"x.{columnName}",
                ["sortKey"] = columnName.ToLowerInvariant(),
                ["sortExpr"] = $"x.{columnName}",
                ["isBool"] = clr.TrimEnd('?') == "bool",
                ["isDate"] = clr.TrimEnd('?') is "DateTime" or "DateOnly" or "DateTimeOffset",
                ["nullable"] = nullable
            });
        }

        return new Dictionary<string, object?>
        {
            ["className"] = name + "ListItemModel",
            ["keys"] = keys.Select(object? (k) => new Dictionary<string, object?>
            {
                ["name"] = k.Name,
                ["csType"] = k.ClrType
            }).ToList(),
            ["columns"] = columns
        };
    }

    private static Dictionary<string, object?> DetailsModel(
        string name, JsonNode entity, List<JsonNode> properties, List<KeyPart> keys)
    {
        var fields = new List<object?>();

        foreach (var property in properties.OrderBy(Order))
        {
            var kind = Str(property["kind"]) ?? "scalar";

            if (kind is "key" or "collection" || !Bool(property["include"]?["details"]))
            {
                continue;
            }

            var propertyName = Str(property["name"])!;

            // An explicit join entity often has no navigation property at all --
            // just the foreign key column. Without one there is nothing to walk,
            // so the raw key is shown rather than emitting x.EmployeeId.Name.
            if (kind == "reference" && Str(property["navigation"]) is { Length: > 0 })
            {
                var navigation = Str(property["navigation"])!;
                var display = Str(property["principal"]?["displayColumn"]) ?? "Id";

                fields.Add(new Dictionary<string, object?>
                {
                    ["name"] = navigation + "Name",
                    ["csType"] = "string",
                    ["initializer"] = " = string.Empty;",
                    ["displayName"] = Sentence(navigation),
                    ["source"] = $"x.{navigation}.{display}",
                    ["isBool"] = false,
                    ["isDate"] = false,
                    ["nullable"] = false
                });

                continue;
            }

            var clr = Str(property["clrType"]) ?? "string";
            var nullable = Bool(property["nullable"]);
            var csType = CsType(clr, nullable);

            fields.Add(new Dictionary<string, object?>
            {
                ["name"] = propertyName,
                ["csType"] = csType,
                ["initializer"] = InitializerFor(csType),
                ["displayName"] = Str(property["display"]?["name"]) ?? Sentence(propertyName),
                ["source"] = $"x.{propertyName}",
                ["isBool"] = clr.TrimEnd('?') == "bool",
                ["isDate"] = clr.TrimEnd('?') is "DateTime" or "DateOnly" or "DateTimeOffset",
                ["nullable"] = nullable
            });
        }

        // A skip navigation shows as the linked labels. Ids on a details page
        // tell a person nothing.
        foreach (var property in properties.Where(p => Str(p["kind"]) == "manyToMany"))
        {
            var navigation = Str(property["name"])!;
            var target = Str(property["targetEntity"]) ?? navigation;
            var display = Str(property["principal"]?["displayColumn"]) ?? Str(property["targetKey"]) ?? "Id";

            fields.Add(new Dictionary<string, object?>
            {
                ["name"] = target + "Names",
                ["csType"] = "List<string>",
                ["initializer"] = " = [];",
                ["displayName"] = Str(property["display"]?["name"]) ?? Sentence(navigation),
                ["source"] = $"x.{navigation}.Select(t => t.{display}).ToList()",
                ["isBool"] = false,
                ["isDate"] = false,
                ["isList"] = true,
                ["nullable"] = false
            });
        }

        // Cascade dependents become count fields. "This cannot be undone" is
        // not a warning if it does not say how much will go with it.
        var cascades = new List<object?>();

        foreach (var property in properties.Where(p => Str(p["kind"]) == "collection"))
        {
            if (Str(property["deleteBehavior"]) is not ("Cascade" or "ClientCascade"))
            {
                continue;
            }

            var navigation = Str(property["name"])!;

            cascades.Add(new Dictionary<string, object?>
            {
                ["name"] = navigation + "Count",
                ["navigation"] = navigation,
                ["label"] = Sentence(navigation).ToLowerInvariant(),
                ["source"] = $"x.{navigation}.Count()"
            });
        }

        return new Dictionary<string, object?>
        {
            ["className"] = name + "DetailsModel",
            ["keys"] = keys.Select(object? (k) => new Dictionary<string, object?>
            {
                ["name"] = k.Name,
                ["csType"] = k.ClrType
            }).ToList(),
            ["fields"] = fields,
            ["cascades"] = cascades,
            ["hasCascades"] = cascades.Count > 0
        };
    }

    /// <summary>
    /// Search operators expand onto templates the project already has:
    /// contains -> one nullable string, range -> a From/To pair, equals -> a
    /// nullable key on Dropdown, choice -> a nullable string on RadioGroup.
    /// </summary>
    private static Dictionary<string, object?> SearchModel(string name, List<JsonNode> properties)
    {
        var fields = new List<object?>();
        var choiceSets = new List<object?>();
        var itemsProperties = new List<object?>();
        var routeValues = new List<object?>();

        foreach (var property in properties.OrderBy(Order))
        {
            var search = property["search"];

            if (search is null || !Bool(search["enabled"]))
            {
                continue;
            }

            var propertyName = Str(property["name"])!;
            var clr = Str(property["clrType"])?.TrimEnd('?') ?? "string";
            var label = Str(property["display"]?["name"]) ?? Sentence(propertyName);
            var op = Str(search["operator"]) ?? "contains";

            switch (op)
            {
                case "contains":
                    fields.Add(SearchField(propertyName, "string?", $"{label} contains", null, null, null));
                    routeValues.Add(new Dictionary<string, object?> { ["name"] = propertyName, ["expr"] = propertyName });
                    break;

                case "range":
                    var rangeType = CsType(clr, nullable: true);

                    // Round-trip format for dates: the default ToString() is
                    // culture-dependent and would not re-bind on the way back.
                    var format = clr is "DateTime" or "DateOnly" or "DateTimeOffset" ? "\"O\"" : "";

                    fields.Add(SearchField($"{propertyName}From", rangeType, $"{label} from", null, null, null));
                    fields.Add(SearchField($"{propertyName}To", rangeType, $"{label} to", null, null, null));

                    routeValues.Add(new Dictionary<string, object?>
                    {
                        ["name"] = $"{propertyName}From",
                        ["expr"] = $"{propertyName}From?.ToString({format})"
                    });
                    routeValues.Add(new Dictionary<string, object?>
                    {
                        ["name"] = $"{propertyName}To",
                        ["expr"] = $"{propertyName}To?.ToString({format})"
                    });
                    break;

                case "equals":
                    var isReference = Str(property["kind"]) == "reference";
                    var itemsProperty = isReference
                        ? Str(property["items"]?["property"]) ?? propertyName + "Items"
                        : null;

                    var equalsField = SearchField(
                        propertyName,
                        CsType(clr, nullable: true),
                        label,
                        Str(search["template"]),
                        itemsProperty,
                        isReference ? $"-- any {Sentence(propertyName).ToLowerInvariant().Replace(" id", "")} --" : null);

                    equalsField["itemsMethod"] = isReference
                        ? Str(property["items"]?["method"]) ?? $"Get{Str(property["principal"]?["entity"])}ItemsAsync"
                        : null;

                    fields.Add(equalsField);

                    if (itemsProperty is not null)
                    {
                        itemsProperties.Add(itemsProperty);
                    }

                    routeValues.Add(new Dictionary<string, object?> { ["name"] = propertyName, ["expr"] = $"{propertyName}?.ToString()" });
                    break;

                case "choice":
                    var choiceName = propertyName + "Choices";

                    choiceSets.Add(new Dictionary<string, object?>
                    {
                        ["name"] = choiceName,
                        ["items"] = (search["choices"] as JsonArray ?? []).Select(object? (c) => new Dictionary<string, object?>
                        {
                            ["text"] = Str(c?["text"]),
                            ["value"] = Str(c?["value"]) ?? ""
                        }).ToList()
                    });

                    var choiceField = SearchField(propertyName, "string?", label, Str(search["template"]), null, null);
                    choiceField["staticChoices"] = choiceName;

                    // string.Empty rather than null: RadioGroup only builds its
                    // checked set when Model is not null, so a null here would
                    // leave every radio unchecked instead of landing on "Any".
                    choiceField["initializer"] = " = string.Empty;";

                    fields.Add(choiceField);
                    routeValues.Add(new Dictionary<string, object?> { ["name"] = propertyName, ["expr"] = propertyName });
                    break;
            }
        }

        return new Dictionary<string, object?>
        {
            ["className"] = name + "SearchModel",
            ["fields"] = fields,
            ["choiceSets"] = choiceSets,
            ["itemsProperties"] = itemsProperties,
            ["routeValues"] = routeValues,
            ["any"] = fields.Count > 0
        };
    }

    private static Dictionary<string, object?> SearchField(
        string name, string csType, string label, string? template, string? itemsProperty, string? placeholder) =>
        new()
        {
            ["name"] = name,
            ["csType"] = csType,
            ["label"] = label,
            ["template"] = template,
            ["itemsProperty"] = itemsProperty,
            ["placeholder"] = placeholder,
            ["initializer"] = "",
            ["staticChoices"] = null,
            ["itemsMethod"] = null
        };

    // --- controller -------------------------------------------------------

    private sealed record KeyPart(string Name, string ClrType);

    /// <summary>
    /// A composite key has no single type, so each part is read separately.
    /// Falls back to the flat form for an IR written before parts existed.
    /// </summary>
    private static List<KeyPart> KeyParts(JsonNode entity)
    {
        if (entity["key"]?["parts"] is JsonArray parts && parts.Count > 0)
        {
            return parts
                .Select(p => new KeyPart(Str(p?["name"]) ?? "Id", Str(p?["clrType"]) ?? "int"))
                .ToList();
        }

        var names = entity["key"]?["properties"] as JsonArray;
        var clrType = Str(entity["key"]?["clrType"]) ?? "int";

        if (names is null || names.Count == 0)
        {
            return [new KeyPart("Id", clrType)];
        }

        return names.Select(n => new KeyPart(Str(n) ?? "Id", clrType)).ToList();
    }

    /// <summary>
    /// Everything the templates need to talk about a key without knowing how
    /// many parts it has. Precomputed here so a template never has to join a
    /// list with " &amp;&amp; " -- that is logic, and logic belongs in C#.
    /// </summary>
    private static Dictionary<string, object?> KeyForms(List<KeyPart> keys, string vmVariable)
    {
        // A single key keeps the parameter name "id" so the conventional
        // {controller}/{action}/{id?} route still binds /Books/Details/5.
        // Renaming it to "bookId" would quietly break every existing link.
        // Composite keys cannot use that route at all, so their parts are named
        // and travel as query string values.
        string Parameter(KeyPart part) => keys.Count == 1 ? "id" : Camel(part.Name);

        var parameters = keys.Select(k => $"{k.ClrType} {Parameter(k)}");
        var nullableParameters = keys.Select(k => $"{k.ClrType}? {Parameter(k)}");
        var arguments = keys.Select(Parameter);

        return new Dictionary<string, object?>
        {
            ["isComposite"] = keys.Count > 1,
            ["names"] = keys.Select(object? (k) => k.Name).ToList(),

            ["parameters"] = string.Join(", ", parameters),
            ["nullableParameters"] = string.Join(", ", nullableParameters),
            ["arguments"] = string.Join(", ", arguments),

            // shelfId is null || slot is null
            ["nullCheck"] = string.Join(" || ", keys.Select(k => $"{Parameter(k)} is null")),

            // shelfId.Value, slot.Value
            ["valueArguments"] = string.Join(", ", keys.Select(k => $"{Parameter(k)}.Value")),

            // x.ShelfId == shelfId && x.Slot == slot
            ["predicate"] = string.Join(" && ", keys.Select(k => $"x.{k.Name} == {Parameter(k)}")),

            // shelfId != vm.ShelfId || slot != vm.Slot
            ["mismatch"] = string.Join(" || ", keys.Select(k => $"{Parameter(k)} != {vmVariable}.{k.Name}")),

            // FindAsync takes key values in key order.
            ["findArguments"] = "[" + string.Join(", ", arguments.Select(a => (object)a)) + "]",

            // .ThenBy(x => x.ShelfId).ThenBy(x => x.Slot) -- a stable tiebreaker
            // needs every part, or paging can still repeat rows.
            ["thenBy"] = string.Concat(keys.Select(k => $".ThenBy(x => x.{k.Name})")),

            // asp-route-shelfId="@item.ShelfId" asp-route-slot="@item.Slot"
            ["routeAttributesForItem"] = string.Join(" ",
                keys.Select(k => $"asp-route-{Parameter(k)}=\"@item.{k.Name}\"")),
            ["routeAttributesForModel"] = string.Join(" ",
                keys.Select(k => $"asp-route-{Parameter(k)}=\"@Model.{k.Name}\""))
        };
    }

    private static Dictionary<string, object?> Controller(
        JsonNode ir, JsonNode entity, List<JsonNode> properties, List<JsonNode> references, List<JsonNode> collections,
        string name, string dbSet, List<KeyPart> keys)
    {
        var requiredNavigations = (entity["requiredNavigations"] as JsonArray ?? [])
            .Select(Str).Where(n => n is not null).Select(n => n!).ToHashSet(StringComparer.Ordinal);

        var principalLoads = new List<object?>();
        var referenceChecks = new List<object?>();

        foreach (var reference in references)
        {
            var propertyName = Str(reference["name"])!;
            var navigation = Str(reference["navigation"]) ?? propertyName;
            var principal = Str(reference["principal"]?["entity"]) ?? navigation;
            var principalKey = Str(reference["principal"]?["key"]) ?? "Id";
            var principalSet = principal + "s";
            var label = Sentence(navigation).ToLowerInvariant();

            if (requiredNavigations.Contains(navigation))
            {
                principalLoads.Add(new Dictionary<string, object?>
                {
                    ["variable"] = Camel(navigation),
                    ["dbSet"] = principalSet,
                    ["property"] = propertyName,
                    ["navigation"] = navigation,
                    ["label"] = label
                });
            }

            referenceChecks.Add(new Dictionary<string, object?>
            {
                ["dbSet"] = principalSet,
                ["principalKey"] = principalKey,
                ["property"] = propertyName,
                ["label"] = label
            });
        }

        // Create and Edit no longer share one list. The composite key of an
        // explicit join entity is set on insert and immutable afterwards, so
        // assigning it on edit would rewrite the row's identity.
        List<object?> Assignments(string surface) => properties
            .Where(p => Str(p["kind"]) is "scalar" or "reference" or "key")
            .Where(p => Bool(p["include"]?[surface]))
            .Where(p => !requiredNavigations.Contains(Str(p["navigation"]) ?? ""))
            .OrderBy(Order)
            .Select(object? (p) => Str(p["name"]))
            .ToList();

        var createAssignments = Assignments("create");
        var editAssignments = Assignments("edit");

        var filters = properties
            .Where(p => Bool(p["search"]?["enabled"]))
            .OrderBy(Order)
            .Select(object? (p) => new Dictionary<string, object?>
            {
                ["property"] = Str(p["name"]),
                ["op"] = Str(p["search"]?["operator"]),
                ["clrType"] = Str(p["clrType"])?.TrimEnd('?'),
                ["nullable"] = Bool(p["nullable"]),
                ["variable"] = Camel(Str(p["name"])!)
            })
            .ToList();

        var sortEntries = new List<object?>();

        foreach (var column in entity["index"]?["listColumns"] as JsonArray ?? [])
        {
            var columnName = Str(column);

            if (columnName is null)
            {
                continue;
            }

            var reference = properties.FirstOrDefault(p => Str(p["navigation"]) == columnName);

            if (reference is not null)
            {
                sortEntries.Add(new Dictionary<string, object?>
                {
                    ["constant"] = columnName,
                    ["key"] = columnName.ToLowerInvariant(),
                    ["expr"] = $"x.{columnName}.{Str(reference["principal"]?["displayColumn"]) ?? "Id"}"
                });

                continue;
            }

            if (properties.Any(p => Str(p["name"]) == columnName && Bool(p["sortable"])))
            {
                sortEntries.Add(new Dictionary<string, object?>
                {
                    ["constant"] = columnName,
                    ["key"] = columnName.ToLowerInvariant(),
                    ["expr"] = $"x.{columnName}"
                });
            }
        }

        // Everything needed to reconcile a posted id list against the tracked
        // collection on save.
        var manyToMany = properties
            .Where(p => Str(p["kind"]) == "manyToMany")
            .Select(object? (p) =>
            {
                var navigation = Str(p["name"])!;
                var target = Str(p["targetEntity"]) ?? navigation;

                return new Dictionary<string, object?>
                {
                    ["navigation"] = navigation,
                    ["target"] = target,
                    ["dbSet"] = Str(p["targetDbSet"]) ?? target + "s",
                    ["targetKey"] = Str(p["targetKey"]) ?? "Id",
                    ["vmProperty"] = target + "Ids",
                    ["variable"] = Camel(target) + "s",
                    ["displayColumn"] = Str(p["principal"]?["displayColumn"]) ?? Str(p["targetKey"]) ?? "Id"
                };
            })
            .ToList();

        var defaultSort = Str(entity["index"]?["defaultSort"]) ?? keys[0].Name;

        return new Dictionary<string, object?>
        {
            ["className"] = Str(entity["controller"]) ?? dbSet + "Controller",
            ["dbSet"] = dbSet,
            ["concurrencyToken"] = Str(entity["concurrencyToken"]),
            ["key"] = KeyForms(keys, "vm"),
            ["manyToMany"] = manyToMany,
            ["hasManyToMany"] = manyToMany.Count > 0,
            ["keyNames"] = keys.Select(object? (k) => k.Name).ToList(),
            ["hasRequiredNavigations"] = principalLoads.Count > 0,
            ["principalLoads"] = principalLoads,
            ["referenceChecks"] = referenceChecks,
            ["createAssignments"] = createAssignments,
            ["editAssignments"] = editAssignments,
            ["filters"] = filters,
            ["sortEntries"] = sortEntries,
            ["defaultSortExpr"] = $"x.{defaultSort}",
            ["defaultSortDescending"] = Bool(entity["index"]?["defaultSortDescending"]),
            ["pageSize"] = Int(entity["index"]?["pageSize"]) ?? 25,
            ["collections"] = collections.Select(object? (c) => new Dictionary<string, object?>
            {
                ["name"] = Str(c["name"]),
                ["targetEntity"] = Str(c["targetEntity"]),

                // The child's own route from the IR, never the type name plus
                // "s" -- that yields Activitys, and breaks outright on any
                // irregular plural.
                ["targetRoute"] = RouteOf(ir, Str(c["targetEntity"])),
                ["label"] = Sentence(Str(c["name"]) ?? "").ToLowerInvariant()
            }).ToList()
        };
    }

    /// <summary>Looks up an entity's configured route by name.</summary>
    private static string? RouteOf(JsonNode ir, string? entityName)
    {
        if (entityName is null)
        {
            return null;
        }

        foreach (var candidate in ir["entities"] as JsonArray ?? [])
        {
            if (Str(candidate?["name"]) == entityName)
            {
                return Str(candidate?["route"]) ?? Str(candidate?["dbSet"]);
            }
        }

        return null;
    }

    private static List<object?> ItemProviders(List<JsonNode> references) =>
        references
            .Select(r => new
            {
                Method = Str(r["items"]?["method"]) ?? $"Get{Str(r["principal"]?["entity"])}ItemsAsync",
                Principal = Str(r["principal"]?["entity"]) ?? "Unknown",
                Key = Str(r["principal"]?["key"]) ?? "Id",
                Display = Str(r["principal"]?["displayColumn"]) ?? "Id",

                // Recorded by the probe for a skip navigation; a plain reference
                // falls back to the naive plural.
                DbSet = Str(r["targetDbSet"])
            })
            // Two foreign keys to the same principal share one provider.
            .GroupBy(r => r.Method)
            .Select(object? (g) => new Dictionary<string, object?>
            {
                ["method"] = g.Key,
                ["dbSet"] = g.First().DbSet ?? g.First().Principal + "s",
                ["textExpr"] = $"x.{g.First().Display}",
                ["valueExpr"] = $"x.{g.First().Key}.ToString()",
                ["orderExpr"] = $"x.{g.First().Display}"
            })
            .ToList();

    // --- helpers ----------------------------------------------------------

    private static bool RequiredOf(JsonNode property) =>
        property["validation"]?["requiredOverride"] is { } overridden
            ? overridden.GetValue<bool>()
            : Bool(property["validation"]?["required"]);

    /// <summary>
    /// Mirrors the entity's CLR nullability and nothing else.
    ///
    /// Nullability is a type concern; requiredness is a validation concern. A
    /// non-nullable string that carries no [Required] -- because its initializer
    /// is string.Empty -- is still `string`. Widening it to `string?` produced a
    /// possible-null-assignment warning the moment the controller mapped it back
    /// onto the entity.
    /// </summary>
    private static string CsType(string clr, bool nullable)
    {
        var bare = clr.TrimEnd('?');

        return nullable ? bare + "?" : bare;
    }

    /// <summary>
    /// Carries its own semicolon. An auto-property needs none, so the templates
    /// must not append one -- "{ get; set; };" is CS1597.
    /// </summary>
    private static string InitializerFor(string csType) =>
        csType == "string" ? " = string.Empty;" : "";

    private static int Order(JsonNode property) => Int(property["order"]) ?? int.MaxValue;

    private static string? Str(JsonNode? node) =>
        node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null
            ? null
            : node.GetValue<string?>();

    private static bool Bool(JsonNode? node) =>
        node is not null && node.GetValueKind() == System.Text.Json.JsonValueKind.True;

    private static int? Int(JsonNode? node) =>
        node is null || node.GetValueKind() != System.Text.Json.JsonValueKind.Number
            ? null
            : node.GetValue<int>();

    private static List<object?> ToList(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(object? (n) => Str(n)).Where(n => n is not null).ToList()
            : [];

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    /// <summary>ActivityId -> "Activity id", IsCompleted -> "Is completed".</summary>
    private static string Sentence(string value)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(value, "(?<!^)([A-Z])", " $1").Trim();

        return spaced.Length == 0
            ? value
            : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }

    private static string? NamespaceOf(string? clrType)
    {
        var index = clrType?.LastIndexOf('.') ?? -1;

        return index > 0 ? clrType![..index] : null;
    }
}
