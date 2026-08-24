using System.Text.Json.Nodes;

namespace Scaffold.Tests;

/// <summary>
/// Builds the JSON the probe would emit, in code.
///
/// Everything downstream of the probe -- rules, IR, plan, render -- is a pure
/// function over this shape. Constructing it directly means the bulk of the
/// suite runs in milliseconds with no database, no EF and no build, and a
/// regression can be reproduced from a three-line test rather than a schema.
/// </summary>
public sealed class RawModel
{
    private readonly JsonArray _entities = [];
    private readonly JsonArray _skipped = [];

    public static RawModel New() => new();

    public RawModel Entity(string name, Action<RawEntity> configure)
    {
        var entity = new RawEntity(name);
        configure(entity);
        _entities.Add(entity.Build());

        return this;
    }

    public RawModel Skipped(string name, string reason)
    {
        _skipped.Add(new JsonObject { ["entity"] = name, ["reason"] = reason });

        return this;
    }

    public JsonNode Build() => new JsonObject
    {
        ["probeVersion"] = 1,
        ["dbContext"] = "Fixture.Data.AppDbContext",
        ["rootNamespace"] = "Fixture",
        ["providerName"] = "Microsoft.EntityFrameworkCore.SqlServer",
        ["entities"] = _entities.DeepClone(),
        ["skipped"] = _skipped.DeepClone()
    };
}

public sealed class RawEntity(string name)
{
    private readonly JsonArray _properties = [];
    private string _key = "Id";
    private string _keyType = "int";
    private string? _concurrencyToken;
    private string[] _displayColumns = [];

    private readonly List<(string Name, string ClrType)> _keyParts = [];

    public RawEntity Key(string propertyName, string clrType = "int")
    {
        _key = propertyName;
        _keyType = clrType;
        _keyParts.Add((propertyName, clrType));

        _properties.Add(new JsonObject
        {
            ["name"] = propertyName,
            ["clrType"] = clrType,
            ["kind"] = "key",
            ["isKey"] = true,
            ["nullable"] = false,
            ["valueGenerated"] = "OnAdd",
            ["attributes"] = new JsonObject(),
            ["initializerKnown"] = false
        });

        return this;
    }

    /// <summary>
    /// A key part that is ALSO a foreign key -- the composite key of an explicit
    /// join entity. It must be chosen on create and is immutable afterwards.
    /// </summary>
    public RawEntity KeyReference(
        string propertyName,
        string? navigation,
        string principalEntity,
        string principalKey = "Id",
        string clrType = "int",
        params string[] principalDisplayColumns)
    {
        _key = propertyName;
        _keyType = clrType;
        _keyParts.Add((propertyName, clrType));

        _properties.Add(new JsonObject
        {
            ["name"] = propertyName,
            ["clrType"] = clrType,
            ["kind"] = "reference",
            ["isKey"] = true,

            // Never generated: the value comes from the form, not the database.
            ["valueGenerated"] = "Never",
            ["nullable"] = false,
            ["navigation"] = navigation,
            ["navigationRequired"] = false,
            ["deleteBehavior"] = "Cascade",
            ["attributes"] = new JsonObject(),
            ["initializerKnown"] = false,
            ["principal"] = new JsonObject
            {
                ["entity"] = principalEntity,
                ["key"] = principalKey,
                ["candidateDisplayColumns"] = new JsonArray(
                    (principalDisplayColumns.Length > 0 ? principalDisplayColumns : ["Name"])
                    .Select(c => (JsonNode?)c).ToArray())
            }
        });

        return this;
    }

    /// <summary>A key the application supplies rather than the database.</summary>
    public RawEntity NaturalKey(string propertyName, string clrType = "string", int? maxLength = null)
    {
        _key = propertyName;
        _keyType = clrType;
        _keyParts.Add((propertyName, clrType));

        _properties.Add(new JsonObject
        {
            ["name"] = propertyName,
            ["clrType"] = clrType,
            ["kind"] = "key",
            ["isKey"] = true,
            ["valueGenerated"] = "Never",
            ["nullable"] = false,
            ["maxLength"] = maxLength,
            ["attributes"] = new JsonObject(),
            ["initializerKnown"] = false
        });

        return this;
    }

    /// <summary>
    /// Call Key twice for a composite key. Each part can have its own type.
    /// </summary>
    public RawEntity CompositeKey(params (string Name, string ClrType)[] parts)
    {
        foreach (var (name, clrType) in parts)
        {
            Key(name, clrType);
        }

        return this;
    }

    public RawEntity ConcurrencyToken(string propertyName)
    {
        _concurrencyToken = propertyName;

        return this;
    }

    public RawEntity DisplayColumns(params string[] names)
    {
        _displayColumns = names;

        return this;
    }

    public RawEntity Scalar(
        string propertyName,
        string clrType,
        bool nullable = false,
        int? maxLength = null,
        string? uiHint = null,
        string? dataType = null,
        string? initializer = null,
        bool initializerKnown = true,
        string? displayName = null,
        (string Min, string Max)? range = null,
        JsonArray? enumMembers = null)
    {
        var attributes = new JsonObject
        {
            ["uiHint"] = uiHint,
            ["dataType"] = dataType,
            ["required"] = false
        };

        if (displayName is not null)
        {
            attributes["display"] = new JsonObject { ["name"] = displayName };
        }

        if (range is { } r)
        {
            attributes["range"] = new JsonObject { ["minimum"] = r.Min, ["maximum"] = r.Max };
        }

        _properties.Add(new JsonObject
        {
            ["name"] = propertyName,
            ["clrType"] = nullable ? clrType.TrimEnd('?') + "?" : clrType,
            ["kind"] = "scalar",
            ["nullable"] = nullable,
            ["maxLength"] = maxLength,
            ["isEnum"] = enumMembers is not null,
            ["enumMembers"] = enumMembers?.DeepClone(),
            ["attributes"] = attributes,
            ["initializer"] = initializer,
            ["initializerKnown"] = initializerKnown
        });

        return this;
    }

    public RawEntity Reference(
        string propertyName,
        string navigation,
        string principalEntity,
        string principalKey = "Id",
        bool nullable = false,
        bool navigationRequired = false,
        string deleteBehavior = "Cascade",
        // The FK type must match the principal key type or EF rejects the model,
        // so a Guid principal means a Guid foreign key.
        string clrType = "int",
        params string[] principalDisplayColumns)
    {
        _properties.Add(new JsonObject
        {
            ["name"] = propertyName,
            ["clrType"] = nullable ? clrType.TrimEnd('?') + "?" : clrType,
            ["kind"] = "reference",
            ["nullable"] = nullable,
            ["navigation"] = navigation,
            ["navigationRequired"] = navigationRequired,
            ["deleteBehavior"] = deleteBehavior,
            ["attributes"] = new JsonObject(),
            ["initializerKnown"] = false,
            ["principal"] = new JsonObject
            {
                ["entity"] = principalEntity,
                ["key"] = principalKey,
                ["candidateDisplayColumns"] = new JsonArray(
                    (principalDisplayColumns.Length > 0 ? principalDisplayColumns : ["Name"])
                    .Select(c => (JsonNode?)c).ToArray())
            }
        });

        return this;
    }

    public RawEntity Collection(
        string propertyName,
        string targetEntity,
        string deleteBehavior = "Cascade",
        string foreignKey = "ParentId",
        bool hasSetter = false)
    {
        _properties.Add(new JsonObject
        {
            ["name"] = propertyName,
            ["kind"] = "collection",
            ["targetEntity"] = targetEntity,
            ["deleteBehavior"] = deleteBehavior,
            ["foreignKey"] = foreignKey,
            ["hasSetter"] = hasSetter,
            ["attributes"] = new JsonObject(),
            ["initializerKnown"] = false
        });

        return this;
    }

    public RawEntity ManyToMany(
        string propertyName,
        string targetEntity,
        string targetKey = "Id",
        bool hasSetter = false,
        string targetKeyClrType = "int",
        params string[] displayColumns)
    {
        _properties.Add(new JsonObject
        {
            ["name"] = propertyName,
            ["kind"] = "manyToMany",
            ["targetEntity"] = targetEntity,
            ["targetKey"] = targetKey,
            ["targetKeyClrType"] = targetKeyClrType,
            ["targetDbSet"] = targetEntity + "s",
            ["isSkipNavigation"] = true,
            ["hasSetter"] = hasSetter,
            ["attributes"] = new JsonObject(),
            ["initializerKnown"] = false,
            ["candidateDisplayColumns"] = new JsonArray(
                (displayColumns.Length > 0 ? displayColumns : ["Name"])
                .Select(c => (JsonNode?)c).ToArray())
        });

        return this;
    }

    public JsonNode Build() => new JsonObject
    {
        ["name"] = name,
        ["clrType"] = $"Fixture.Entities.{name}",
        ["namespace"] = "Fixture.Entities",
        ["dbSet"] = name + "s",
        ["tableName"] = name + "s",
        ["key"] = new JsonObject
        {
            ["properties"] = new JsonArray(_keyParts.Select(k => (JsonNode?)k.Name).ToArray()),
            ["clrType"] = _keyType,
            ["valueGenerated"] = "OnAdd",
            ["isComposite"] = _keyParts.Count > 1,
            ["parts"] = new JsonArray(_keyParts.Select(k => (JsonNode?)new JsonObject
            {
                ["name"] = k.Name,
                ["clrType"] = k.ClrType,
                ["valueGenerated"] = "OnAdd"
            }).ToArray())
        },
        ["concurrencyToken"] = _concurrencyToken,
        ["candidateDisplayColumns"] = new JsonArray(_displayColumns.Select(c => (JsonNode?)c).ToArray()),
        ["properties"] = _properties.DeepClone()
    };

    public static JsonArray EnumMembers(params string[] names) =>
        new(names.Select((n, i) => (JsonNode?)new JsonObject
        {
            ["name"] = n,
            ["value"] = i,
            ["display"] = n
        }).ToArray());
}
