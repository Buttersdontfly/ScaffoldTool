using System.Text.Json.Nodes;
using Xunit;

namespace Scaffold.Tests;

/// <summary>
/// The one layer the fast tests cannot cover: reading a real EF IModel.
///
/// Runs the probe against Scaffold.TestModel, which is built for this purpose
/// and carries the shapes a single real project never has all at once --
/// many-to-many, owned types, composite keys, Guid keys, cascade AND restrict,
/// and a concurrency token.
///
/// Slow: it builds a project. Filter it out during normal work with
///     dotnet test --filter Category!=Integration
/// </summary>
[Trait("Category", "Integration")]
public class ProbeIntegrationTests
{
    private static string TestModelProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scaffold.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "Scaffold.TestModel", "Scaffold.TestModel.csproj");
    }

    private static JsonNode Raw()
    {
        var runner = new ProbeRunner(TestModelProject(), verbose: false);

        return runner.RunAsync(contextName: null, provider: null, connection: null, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static JsonNode EntityIn(JsonNode raw, string name) =>
        (raw["entities"] as JsonArray)!.First(e => e!["name"]!.GetValue<string>() == name)!;

    private static JsonNode PropertyIn(JsonNode raw, string entity, string property) =>
        (EntityIn(raw, entity)["properties"] as JsonArray)!
            .First(p => p!["name"]!.GetValue<string>() == property)!;

    [Fact]
    public void ProbeSurvivesTypesItCannotLoad()
    {
        // Scaffold.TestModel carries a design-time factory, and the Design
        // package ships with PrivateAssets="all" in the standard EF template, so
        // that one type cannot be loaded from the probe. Assembly.GetTypes()
        // throws for the whole assembly in that case; everything else must still
        // come through.
        var entities = (Raw()["entities"] as JsonArray)!;

        Assert.NotEmpty(entities);
    }

    [Fact]
    public void ProbeReadsEveryEntityWithADbSet()
    {
        var names = (Raw()["entities"] as JsonArray)!
            .Select(e => e!["name"]!.GetValue<string>())
            .ToList();

        Assert.Contains("Author", names);
        Assert.Contains("Book", names);
        Assert.Contains("Genre", names);
        Assert.Contains("Publisher", names);
        Assert.Contains("ShelfPosition", names);

        // The implicit many-to-many join entity has no DbSet and is not a root.
        Assert.DoesNotContain(names, n => n.Contains("BookGenre", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredMembersAreDetected()
    {
        // public required Author Author { get; set; }
        var property = PropertyIn(Raw(), "Book", "AuthorId");

        Assert.True(property["navigationRequired"]!.GetValue<bool>());
    }

    [Fact]
    public void InitializersDistinguishNullBangFromEmptyString()
    {
        var raw = Raw();

        // Author has no required members, so it can be constructed reflectively.
        Assert.True(PropertyIn(raw, "Author", "Name")["initializerKnown"]!.GetValue<bool>());
        Assert.Null(PropertyIn(raw, "Author", "Name")["initializer"]);
        Assert.Equal("", PropertyIn(raw, "Author", "Pseudonym")["initializer"]!.GetValue<string>());
    }

    [Fact]
    public void DeleteBehavioursSurvive()
    {
        var raw = Raw();

        Assert.Equal("Cascade", PropertyIn(raw, "Book", "Reviews")["deleteBehavior"]!.GetValue<string>());
        Assert.Equal("Restrict", PropertyIn(raw, "Book", "PublisherId")["deleteBehavior"]!.GetValue<string>());
    }

    [Fact]
    public void SkipNavigationsAreReported()
    {
        var genres = PropertyIn(Raw(), "Book", "Genres");

        Assert.Equal("manyToMany", genres["kind"]!.GetValue<string>());
        Assert.Equal("Genre", genres["targetEntity"]!.GetValue<string>());
    }

    [Fact]
    public void ConcurrencyTokenIsFound()
    {
        Assert.Equal("RowVersion", EntityIn(Raw(), "Book")["concurrencyToken"]!.GetValue<string>());
    }

    [Fact]
    public void OwnedTypesAreDescribedInlineNotAsRoots()
    {
        var raw = Raw();
        var names = (raw["entities"] as JsonArray)!.Select(e => e!["name"]!.GetValue<string>());

        Assert.DoesNotContain("PostalAddress", names);

        var owned = PropertyIn(raw, "Publisher", "HeadOffice");

        Assert.Equal("owned", owned["kind"]!.GetValue<string>());
        Assert.NotEmpty((owned["ownedProperties"] as JsonArray)!);
    }

    [Fact]
    public void GuidKeysAreReported()
    {
        Assert.Equal("Guid", EntityIn(Raw(), "Publisher")["key"]!["clrType"]!.GetValue<string>());
    }

    [Fact]
    public void CompositeKeysAreReportedInFull()
    {
        var key = (EntityIn(Raw(), "ShelfPosition")["key"]!["properties"] as JsonArray)!
            .Select(p => p!.GetValue<string>())
            .ToArray();

        Assert.Equal(["ShelfId", "Slot"], key);

        // Per-part types: a composite key has no single type, and the generator
        // needs each one to build action parameters.
        var parts = (EntityIn(Raw(), "ShelfPosition")["key"]!["parts"] as JsonArray)!;

        Assert.Equal(2, parts.Count);
        Assert.Equal("int", parts[0]!["clrType"]!.GetValue<string>());
    }

    [Fact]
    public void JoinEntityKeyPartsAreReportedAsForeignKeys()
    {
        // BookAward's key is (BookId, AwardId), and both are foreign keys.
        // Reporting them as plain keys loses the relationship and leaves the
        // Create form with nothing that can set them.
        var property = PropertyIn(Raw(), "BookAward", "BookId");

        Assert.Equal("reference", property["kind"]!.GetValue<string>());
        Assert.True(property["isKey"]!.GetValue<bool>());
        Assert.Equal("Book", property["principal"]!["entity"]!.GetValue<string>());
    }

    [Fact]
    public void GeneratedAndSuppliedKeysAreDistinguishable()
    {
        var raw = Raw();

        Assert.Equal("OnAdd", PropertyIn(raw, "Book", "BookId")["valueGenerated"]!.GetValue<string>());

        // A key that is also a foreign key is supplied, never generated.
        Assert.NotEqual("OnAdd", PropertyIn(raw, "BookAward", "BookId")["valueGenerated"]!.GetValue<string>());
    }

    [Fact]
    public void BclTypeNamesAreNormalised()
    {
        // "System.DateTime" would silently miss every rule keyed on "DateTime".
        var property = PropertyIn(Raw(), "Book", "CreatedAt");

        Assert.Equal("DateTime", property["clrType"]!.GetValue<string>());
    }

    [Fact]
    public void DeclarationOrderIsPreserved()
    {
        var names = (EntityIn(Raw(), "Book")["properties"] as JsonArray)!
            .Select(p => p!["name"]!.GetValue<string>())
            .ToList();

        Assert.True(names.IndexOf("Title") < names.IndexOf("Price"));
        Assert.True(names.IndexOf("Price") < names.IndexOf("AuthorId"));
    }
}
