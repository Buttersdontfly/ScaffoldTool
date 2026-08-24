using System.Text.Json.Nodes;
using Xunit;

namespace Scaffold.Tests;

/// <summary>
/// A composite key has no single "id", so the default {controller}/{action}/{id?}
/// route cannot carry it. Every action parameter, every EF predicate and every
/// link has to name each part.
/// </summary>
public class CompositeKeyTests
{
    private static JsonNode Ir() => TestContext.BuildIr(
        RawModel.New().Entity("ShelfPosition", e =>
        {
            e.CompositeKey(("ShelfId", "int"), ("Slot", "int"));
            e.DisplayColumns("Note");
            e.Scalar("Note", "string", maxLength: 50, nullable: true);
            e.Reference("BookId", "Book", "Book", "BookId", nullable: true);
        })
        .Entity("Book", e =>
        {
            e.Key("BookId");
            e.DisplayColumns("Title");
            e.Scalar("Title", "string", initializer: null);
        }));

    private static string Controller() =>
        TestContext.Render(Ir(), "ShelfPosition", "Controller.scriban");

    [Fact]
    public void EveryKeyPartReachesTheIr()
    {
        var key = TestContext.EntityIn(Ir(), "ShelfPosition")["key"]!;

        Assert.True(key["isComposite"]!.GetValue<bool>());

        var names = (key["properties"] as JsonArray)!.Select(p => p!.GetValue<string>());

        Assert.Equal(["ShelfId", "Slot"], names);
    }

    [Fact]
    public void ActionsTakeOneParameterPerKeyPart()
    {
        var source = Controller();

        // Not "int? id": there is no single id to bind.
        Assert.Contains("DetailsAsync(int? shelfId, int? slot", source);
        Assert.Contains("EditAsync(int? shelfId, int? slot", source);
        Assert.Contains("EditAsync(int shelfId, int slot,", source);
        Assert.Contains("DeleteAsync(int shelfId, int slot,", source);
        Assert.DoesNotContain("int? id,", source);
    }

    [Fact]
    public void LookupsCompareEveryPart()
    {
        var source = Controller();

        Assert.Contains("x.ShelfId == shelfId && x.Slot == slot", source);
    }

    [Fact]
    public void EditRejectsAMismatchOnAnyPart()
    {
        // Route values are authoritative; a posted hidden field must not be able
        // to redirect the write at a different row.
        Assert.Contains("shelfId != vm.ShelfId || slot != vm.Slot", Controller());
    }

    [Fact]
    public void PagingTiebreakerCoversEveryPart()
    {
        // A tiebreaker on only the first part still lets rows repeat between
        // pages when that part has duplicates -- which for a composite key is
        // the normal case, not an edge case.
        Assert.Contains(".ThenBy(x => x.ShelfId).ThenBy(x => x.Slot)", Controller());
    }

    [Fact]
    public void ViewModelsCarryEveryPart()
    {
        var plan = TestContext.Plan(Ir(), "ShelfPosition");
        var renderer = new Renderer(null);

        var edit = renderer.Render("ViewModelForm.scriban", plan, ("model", plan["editModel"]));
        var list = renderer.Render("ViewModelListItem.scriban", plan, ("model", plan["listItemModel"]));
        var details = renderer.Render("ViewModelDetails.scriban", plan, ("model", plan["detailsModel"]));

        foreach (var source in new[] { edit, list, details })
        {
            Assert.Contains("public int ShelfId { get; set; }", source);
            Assert.Contains("public int Slot { get; set; }", source);
        }

        // A create model has no key: the row does not exist yet.
        var create = renderer.Render("ViewModelForm.scriban", plan, ("model", plan["createModel"]));

        Assert.DoesNotContain("public int Slot { get; set; }", create);
    }

    [Fact]
    public void LinksCarryEveryPartAsARouteValue()
    {
        var index = TestContext.Render(Ir(), "ShelfPosition", "ViewIndex.scriban");

        Assert.Contains(@"asp-route-shelfId=""@item.ShelfId"" asp-route-slot=""@item.Slot""", index);
        Assert.DoesNotContain("asp-route-id=", index);
    }

    [Fact]
    public void DetailsAndDeleteLinksUseTheModel()
    {
        var details = TestContext.Render(Ir(), "ShelfPosition", "ViewDetails.scriban");
        var delete = TestContext.Render(Ir(), "ShelfPosition", "ViewDelete.scriban");

        Assert.Contains(@"asp-route-shelfId=""@Model.ShelfId"" asp-route-slot=""@Model.Slot""", details);
        Assert.Contains(@"asp-route-shelfId=""@Model.ShelfId"" asp-route-slot=""@Model.Slot""", delete);
    }

    [Fact]
    public void EditFormPostsEveryPartAsAHiddenField()
    {
        var plan = TestContext.Plan(Ir(), "ShelfPosition");

        var edit = new Renderer(null).Render("ViewForm.scriban", plan,
            ("model", plan["editModel"]), ("formAction", "Edit"), ("pageTitle", "Edit shelf position"));

        Assert.Contains(@"<input type=""hidden"" asp-for=""ShelfId"" />", edit);
        Assert.Contains(@"<input type=""hidden"" asp-for=""Slot"" />", edit);
    }

    [Fact]
    public void SingleKeyEntitiesAreUnaffected()
    {
        // The composite path must not change output for the common case.
        var source = TestContext.Render(Ir(), "Book", "Controller.scriban");

        // Still "id", so /Books/Details/5 keeps binding on the conventional
        // {controller}/{action}/{id?} route.
        Assert.Contains("DetailsAsync(int? id, CancellationToken ct)", source);
        Assert.Contains("x.BookId == id", source);
        Assert.Contains(".ThenBy(x => x.BookId)", source);

        var index = TestContext.Render(Ir(), "Book", "ViewIndex.scriban");

        Assert.Contains(@"asp-route-id=""@item.BookId""", index);
    }
}
