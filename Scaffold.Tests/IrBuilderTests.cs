using System.Text.Json.Nodes;
using Xunit;

namespace Scaffold.Tests;

public class IrBuilderTests
{
    private static RawModel Blog() => RawModel.New()
        .Entity("Post", e =>
        {
            e.Key("PostId");
            e.DisplayColumns("Title");
            e.Scalar("Title", "string", maxLength: 200, initializer: null);
            e.Scalar("Body", "string", maxLength: 4000, initializer: "");
            e.Scalar("PublishedAt", "DateTime", nullable: true);
            e.Scalar("IsFeatured", "bool");
            e.Reference("AuthorId", "Author", "Author", "AuthorId", navigationRequired: true);
            e.Collection("Comments", "Comment", deleteBehavior: "Cascade", foreignKey: "PostId");
            e.ManyToMany("Tags", "Tag", "TagId");
        })
        .Entity("Author", e =>
        {
            e.Key("AuthorId");
            e.DisplayColumns("Name");
            e.Scalar("Name", "string", maxLength: 100, initializer: null);
        });

    [Fact]
    public void PropertiesKeepDeclarationOrder()
    {
        var ir = TestContext.BuildIr(Blog());
        var names = (TestContext.EntityIn(ir, "Post")["properties"] as JsonArray)!
            .Select(p => p!["name"]!.GetValue<string>())
            .ToArray();

        // Not alphabetical: EF returns properties sorted, the probe restores the
        // order they were declared in, and form field order depends on it.
        Assert.Equal(
            ["PostId", "Title", "Body", "PublishedAt", "IsFeatured", "AuthorId", "Comments", "Tags"],
            names);
    }

    [Fact]
    public void OrderValuesAscendWithDeclaration()
    {
        var ir = TestContext.BuildIr(Blog());
        var orders = (TestContext.EntityIn(ir, "Post")["properties"] as JsonArray)!
            .Select(p => p!["order"]!.GetValue<int>())
            .ToArray();

        Assert.Equal(orders.OrderBy(o => o), orders);
    }

    [Theory]
    [InlineData("Title", "contains")]
    [InlineData("PublishedAt", "range")]
    [InlineData("IsFeatured", "choice")]
    [InlineData("AuthorId", "equals")]
    public void SearchOperatorMatchesPropertyShape(string property, string expected)
    {
        var ir = TestContext.BuildIr(Blog());
        var search = TestContext.PropertyIn(ir, "Post", property)["search"]!;

        Assert.True(search["enabled"]!.GetValue<bool>());
        Assert.Equal(expected, search["operator"]!.GetValue<string>());
    }

    [Fact]
    public void EveryEnabledSearchCarriesTheEnabledFlag()
    {
        // A rule that names an operator is enabled by definition. Relying on
        // rules.json to repeat the flag meant one missing line silently dropped
        // the field from the search panel.
        var ir = TestContext.BuildIr(Blog());

        foreach (var property in (TestContext.EntityIn(ir, "Post")["properties"] as JsonArray)!)
        {
            var search = property!["search"];

            if (search?["operator"] is not null)
            {
                Assert.True(search["enabled"]!.GetValue<bool>(),
                    $"{property["name"]} has an operator but is not enabled");
            }
        }
    }

    [Fact]
    public void KeysAndCollectionsAreNotSearchable()
    {
        var ir = TestContext.BuildIr(Blog());

        Assert.False(TestContext.PropertyIn(ir, "Post", "PostId")["search"]!["enabled"]!.GetValue<bool>());
        Assert.False(TestContext.PropertyIn(ir, "Post", "Comments")["search"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void BoolSearchOffersThreeChoices()
    {
        var ir = TestContext.BuildIr(Blog());
        var search = TestContext.PropertyIn(ir, "Post", "IsFeatured")["search"]!;

        Assert.Equal("RadioGroup", search["template"]!.GetValue<string>());

        var choices = (search["choices"] as JsonArray)!;

        Assert.Equal(3, choices.Count);
        Assert.Equal("", choices[0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void RequiredNavigationsAreCollected()
    {
        var ir = TestContext.BuildIr(Blog());
        var navigations = (TestContext.EntityIn(ir, "Post")["requiredNavigations"] as JsonArray)!
            .Select(n => n!.GetValue<string>());

        Assert.Contains("Author", navigations);
    }

    [Fact]
    public void CollectionsAreDetailsOnlyAndGetNoTemplate()
    {
        var ir = TestContext.BuildIr(Blog());
        var comments = TestContext.PropertyIn(ir, "Post", "Comments");

        Assert.Null(TestContext.Str(comments["editor"]?["template"]));
        Assert.False(comments["include"]!["create"]!.GetValue<bool>());
        Assert.False(comments["include"]!["edit"]!.GetValue<bool>());
        Assert.True(comments["include"]!["details"]!.GetValue<bool>());
    }

    [Fact]
    public void CascadingCollectionLeavesAWarning()
    {
        var ir = TestContext.BuildIr(Blog());
        var todos = TestContext.PropertyIn(ir, "Post", "Comments")["todo"] as JsonArray;

        Assert.NotNull(todos);
        Assert.Contains(todos!, t => t!.GetValue<string>().Contains("cascade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestrictingCollectionDoesNotWarn()
    {
        var raw = RawModel.New().Entity("Post", e =>
        {
            e.Key("PostId");
            e.Collection("Comments", "Comment", deleteBehavior: "Restrict");
        });

        var ir = TestContext.BuildIr(raw);
        var todos = TestContext.PropertyIn(ir, "Post", "Comments")["todo"] as JsonArray;

        Assert.True(todos is null ||
            todos.All(t => !t!.GetValue<string>().Contains("cascade", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ManyToManyBecomesACheckboxList()
    {
        var ir = TestContext.BuildIr(Blog());
        var tags = TestContext.PropertyIn(ir, "Post", "Tags");

        Assert.Equal("Tag", TestContext.Str(tags["targetEntity"]));
        Assert.Equal("checkboxList", TestContext.Str(tags["items"]?["strategy"]));
        Assert.Equal("Name", TestContext.Str(tags["principal"]?["displayColumn"]));
    }

    [Fact]
    public void ListColumnsUseTheNavigationNotTheForeignKey()
    {
        var ir = TestContext.BuildIr(Blog());
        var columns = (TestContext.EntityIn(ir, "Post")["index"]!["listColumns"] as JsonArray)!
            .Select(c => c!.GetValue<string>())
            .ToArray();

        // A grid shows the principal's label, never its surrogate key.
        Assert.Contains("Author", columns);
        Assert.DoesNotContain("AuthorId", columns);
    }

    [Fact]
    public void MultilineColumnsAreExcludedFromTheGrid()
    {
        var ir = TestContext.BuildIr(Blog());
        var columns = (TestContext.EntityIn(ir, "Post")["index"]!["listColumns"] as JsonArray)!
            .Select(c => c!.GetValue<string>());

        Assert.DoesNotContain("Body", columns);
    }

    [Fact]
    public void DefaultSortPrefersADateThenTheDisplayColumn()
    {
        var ir = TestContext.BuildIr(Blog());

        Assert.Equal("PublishedAt", TestContext.Str(TestContext.EntityIn(ir, "Post")["index"]?["defaultSort"]));
        Assert.True(TestContext.EntityIn(ir, "Post")["index"]!["defaultSortDescending"]!.GetValue<bool>());

        // Author has no date, so it falls back to its display column rather than
        // the key, which would show insertion order.
        Assert.Equal("Name", TestContext.Str(TestContext.EntityIn(ir, "Author")["index"]?["defaultSort"]));
    }

    [Fact]
    public void KeysGetNoTemplateAndNoTodo()
    {
        // A key is a hidden field, never rendered by an editor template.
        // Resolving one is how a Guid key ended up asking for Guid.cshtml and
        // leaving a TODO nobody can act on.
        var ir = TestContext.BuildIr(
            RawModel.New().Entity("Doc", e =>
            {
                e.Key("DocId", "Guid");
                e.Scalar("Title", "string", initializer: null);
            }));

        var key = TestContext.PropertyIn(ir, "Doc", "DocId");

        Assert.Null(TestContext.Str(key["editor"]?["template"]));
        Assert.Null(key["todo"]);
    }

    [Fact]
    public void GuidForeignKeysStillResolveToDropdown()
    {
        // The FK type must match the principal key type, so a Guid principal
        // means a Guid FK. Structural rules run before type name, so the
        // dropdown is chosen regardless of there being no Guid.cshtml.
        var ir = TestContext.BuildIr(
            RawModel.New()
                .Entity("Book", e =>
                {
                    e.Key("BookId");
                    e.Reference("PublisherId", "Publisher", "Publisher", "PublisherId",
                        nullable: true, clrType: "Guid");
                })
                .Entity("Publisher", e =>
                {
                    e.Key("PublisherId", "Guid");
                    e.DisplayColumns("Name");
                    e.Scalar("Name", "string", initializer: null);
                }));

        var fk = TestContext.PropertyIn(ir, "Book", "PublisherId");

        Assert.Equal("Dropdown", TestContext.Str(fk["editor"]?["template"]));
        Assert.Equal("equals", TestContext.Str(fk["search"]?["operator"]));
    }

    [Fact]
    public void ForeignKeyLosesTheIdSuffixButTheKeyDoesNot()
    {
        var ir = TestContext.BuildIr(Blog());

        Assert.Equal("Author", TestContext.Str(TestContext.PropertyIn(ir, "Post", "AuthorId")["display"]?["name"]));
        Assert.Equal("Post ID", TestContext.Str(TestContext.PropertyIn(ir, "Post", "PostId")["display"]?["name"]));
    }
}

public class IrMergeTests
{
    private static JsonNode Ir() => TestContext.BuildIr(
        RawModel.New().Entity("Post", e =>
        {
            e.Key("PostId");
            e.DisplayColumns("Title");
            e.Scalar("Title", "string", maxLength: 200);
        }));

    [Fact]
    public void HandEditedLabelsSurviveReInspection()
    {
        var existing = Ir();

        TestContext.PropertyIn(existing, "Post", "Title")["display"]!["name"] = "Überschrift";

        var (merged, _) = IrMerge.Merge(existing, Ir());

        Assert.Equal("Überschrift", TestContext.Str(TestContext.PropertyIn(merged, "Post", "Title")["display"]?["name"]));
    }

    [Fact]
    public void SchemaFactsAreAlwaysRederived()
    {
        var existing = Ir();

        // Stale claim from an older schema.
        TestContext.PropertyIn(existing, "Post", "Title")["maxLength"] = 50;

        var fresh = Ir();
        var (merged, _) = IrMerge.Merge(existing, fresh);

        // Requiredness and lengths are schema, not intent: a stale answer emits
        // silently wrong validation attributes.
        Assert.Equal(200, TestContext.PropertyIn(merged, "Post", "Title")["maxLength"]!.GetValue<int>());
    }

    [Fact]
    public void DroppedPropertiesAreReported()
    {
        var existing = TestContext.BuildIr(
            RawModel.New().Entity("Post", e =>
            {
                e.Key("PostId");
                e.Scalar("Title", "string");
                e.Scalar("Subtitle", "string");
            }));

        var (_, report) = IrMerge.Merge(existing, Ir());

        // A rename reads as a drop plus an add, so it has to be visible.
        Assert.Contains("Post.Subtitle", report.DroppedProperties);
        Assert.True(report.HasLosses);
    }

    [Fact]
    public void AddedPropertiesAreReported()
    {
        var fresh = TestContext.BuildIr(
            RawModel.New().Entity("Post", e =>
            {
                e.Key("PostId");
                e.Scalar("Title", "string");
                e.Scalar("Slug", "string");
            }));

        var (_, report) = IrMerge.Merge(Ir(), fresh);

        Assert.Contains("Post.Slug", report.AddedProperties);
        Assert.False(report.HasLosses);
    }
}
