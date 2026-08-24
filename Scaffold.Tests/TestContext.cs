using System.Text.Json.Nodes;

namespace Scaffold.Tests;

/// <summary>
/// Shared setup: a template catalog that pretends the project ships the full
/// StarterSeam set, and helpers to run raw -> IR -> plan -> rendered text.
/// </summary>
public static class TestContext
{
    /// <summary>Every template in the reference StarterSeam project.</summary>
    public static readonly string[] AllTemplates =
    [
        "String", "Int32", "Int64", "Decimal", "Boolean", "DateOnly", "TimeOnly", "DateTime",
        "Enum", "EmailAddress", "Password", "Url", "PhoneNumber", "MultilineText",
        "Dropdown", "RadioGroup", "CheckboxList", "Tags", "Color", "Rating", "Range",
        "FileUpload", "UserName", "PersonNameInputModel", "AddressInputModel", "LineItem"
    ];

    public static TemplateCatalog Catalog(params string[] names)
    {
        var directory = Path.Combine(Path.GetTempPath(), "scaffold-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        foreach (var name in names.Length > 0 ? names : AllTemplates)
        {
            File.WriteAllText(Path.Combine(directory, name + ".cshtml"), "@* fixture *@");
        }

        return TemplateCatalog.Discover(directory);
    }

    public static JsonNode BuildIr(RawModel raw, params string[] templates)
    {
        var ir = IrBuilder.Build(raw.Build(), Catalog(templates), HintRules.Load(null), Path.GetTempPath());

        // The fixture catalog lives in a per-run temp folder, so the recorded
        // path is different every time. Pinned here or no IR could ever be a
        // golden file.
        ir["templateDirectory"] = "Views/Shared/EditorTemplates";

        return ir;
    }

    public static JsonNode EntityIn(JsonNode ir, string name) =>
        (ir["entities"] as JsonArray)!.First(e => e!["name"]!.GetValue<string>() == name)!;

    public static JsonNode PropertyIn(JsonNode ir, string entity, string property) =>
        (EntityIn(ir, entity)["properties"] as JsonArray)!
            .First(p => p!["name"]!.GetValue<string>() == property)!;

    public static Dictionary<string, object?> Plan(JsonNode ir, string entity) =>
        Planner.Plan(ir, EntityIn(ir, entity));

    public static string Render(JsonNode ir, string entity, string template,
        params (string Key, object? Value)[] extras) =>
        new Renderer(null).Render(template, Plan(ir, entity), extras);

    /// <summary>
    /// One entity exercising every generator decision at once: required and
    /// optional navigations, an enum, a bool, a nullable date, a decimal, a
    /// concurrency token, a cascading collection and a many-to-many.
    /// </summary>
    public static JsonNode SampleIr() => BuildIr(
        RawModel.New()
            .Entity("Book", e =>
            {
                e.Key("BookId");
                e.DisplayColumns("Title");
                e.ConcurrencyToken("RowVersion");
                e.Scalar("Title", "string", maxLength: 300, initializer: null);
                e.Scalar("Blurb", "string", maxLength: 2000, initializer: "");
                e.Scalar("Price", "decimal");
                e.Scalar("IsPublished", "bool");
                e.Scalar("PublishedAt", "DateTime", nullable: true);
                e.Reference("AuthorId", "Author", "Author", "AuthorId", navigationRequired: true);
                e.Reference("PublisherId", "Publisher", "Publisher", "PublisherId", nullable: true);
                e.Collection("Reviews", "Review", deleteBehavior: "Cascade", foreignKey: "BookId");
                e.ManyToMany("Genres", "Genre", "GenreId");
            })
            .Entity("Author", e =>
            {
                e.Key("AuthorId");
                e.DisplayColumns("Name");
                e.Scalar("Name", "string", maxLength: 100, initializer: null);
            })
            .Entity("Publisher", e =>
            {
                e.Key("PublisherId");
                e.DisplayColumns("Name");
                e.Scalar("Name", "string", maxLength: 150, initializer: null);
            }));

    /// <summary>Every view template rendered against the sample entity.</summary>
    public static IEnumerable<(string Name, string Output)> RenderedViews()
    {
        var ir = SampleIr();
        var plan = Plan(ir, "Book");
        var renderer = new Renderer(null);

        yield return ("Create.cshtml", renderer.Render("ViewForm.scriban", plan,
            ("model", plan["createModel"]), ("formAction", "Create"), ("pageTitle", "New book")));

        yield return ("Edit.cshtml", renderer.Render("ViewForm.scriban", plan,
            ("model", plan["editModel"]), ("formAction", "Edit"), ("pageTitle", "Edit book")));

        yield return ("Index.cshtml", renderer.Render("ViewIndex.scriban", plan));
        yield return ("Details.cshtml", renderer.Render("ViewDetails.scriban", plan));
        yield return ("Delete.cshtml", renderer.Render("ViewDelete.scriban", plan));
    }

    public static string? Str(JsonNode? node) =>
        node is null || node.GetValueKind() == System.Text.Json.JsonValueKind.Null
            ? null
            : node.GetValue<string?>();
}
