using System.Text.Json.Nodes;
using Xunit;

namespace Scaffold.Tests;

/// <summary>
/// A skip navigation is the one relationship a form cannot express directly:
/// there is no scalar to bind. It posts back as a flat list of target ids on a
/// CheckboxList, and the controller reconciles that list against the tracked
/// collection.
/// </summary>
public class ManyToManyTests
{
    private static JsonNode Ir() => TestContext.BuildIr(
        RawModel.New()
            .Entity("Book", e =>
            {
                e.Key("BookId");
                e.DisplayColumns("Title");
                e.Scalar("Title", "string", maxLength: 300, initializer: null);
                e.ManyToMany("Genres", "Genre", "GenreId");
            })
            .Entity("Genre", e =>
            {
                e.Key("GenreId");
                e.DisplayColumns("Name");
                e.Scalar("Name", "string", maxLength: 80, initializer: null);
            }));

    private static string Controller() => TestContext.Render(Ir(), "Book", "Controller.scriban");

    private static string FormModel(string surface)
    {
        var plan = TestContext.Plan(Ir(), "Book");

        return new Renderer(null).Render("ViewModelForm.scriban", plan, ("model", plan[surface]));
    }

    [Fact]
    public void SkipNavigationIsEditableDespiteAGetterOnlyCollection()
    {
        // `ICollection<Genre> Genres { get; } = []` has no setter, but it is
        // still editable: you Add and Remove, you never assign.
        var include = TestContext.PropertyIn(Ir(), "Book", "Genres")["include"]!;

        Assert.True(include["create"]!.GetValue<bool>());
        Assert.True(include["edit"]!.GetValue<bool>());
    }

    [Fact]
    public void ViewModelHoldsAListOfTargetIds()
    {
        var create = FormModel("createModel");

        // Named for the target, not the navigation: GenreIds over GenresIds.
        Assert.Contains("public List<int> GenreIds { get; set; } = [];", create);
        Assert.Contains(@"[UIHint(""CheckboxList"")]", create);
        Assert.Contains("public IEnumerable<SelectListItem> GenreItems { get; set; } = [];", create);
    }

    [Fact]
    public void BothSurfacesGetTheField()
    {
        Assert.Contains("GenreIds", FormModel("createModel"));
        Assert.Contains("GenreIds", FormModel("editModel"));
    }

    [Fact]
    public void ItemsProviderQueriesTheTargetSet()
    {
        var source = Controller();

        Assert.Contains("GetGenreItemsAsync", source);
        Assert.Contains("_context.Genres", source);
        Assert.Contains("Text = x.Name", source);
    }

    [Fact]
    public void SkipNavigationAloneStillProducesSelectListPlumbing()
    {
        // Book has no foreign keys at all -- only the skip navigation. The
        // controller's whole select-list section is gated on there being
        // something to populate, and counting only foreign keys left this entity
        // with checkboxes and no options.
        var source = Controller();

        Assert.Contains("PopulateSelectListsAsync", source);
        Assert.Contains("vm.GenreItems = await GetGenreItemsAsync(ct);", source);
    }

    [Fact]
    public void CreateAddsRatherThanAssigns()
    {
        var source = Controller();

        Assert.Contains("entity.Genres.Add(item)", source);
        Assert.DoesNotContain("entity.Genres =", source);
    }

    [Fact]
    public void EditLoadsTheCollectionBeforeReconciling()
    {
        // Without the Include the change tracker cannot know which links to
        // drop, and the removal loop silently does nothing.
        Assert.Contains(".Include(x => x.Genres)", Controller());
    }

    [Fact]
    public void EditProjectsCurrentIdsSoCheckboxesStartTicked()
    {
        Assert.Contains("GenreIds = x.Genres.Select(t => t.GenreId).ToList()", Controller());
    }

    [Fact]
    public void EditReconcilesInsteadOfClearing()
    {
        var source = Controller();

        // Clear-and-re-add deletes every join row and reinserts it. It churns
        // the table and would discard any payload an explicit join entity
        // carries later.
        Assert.DoesNotContain("entity.Genres.Clear()", source);

        Assert.Contains("entity.Genres.Remove(item)", source);
        Assert.Contains("entity.Genres.Add(item)", source);
    }

    [Fact]
    public void OnlyMissingTargetsAreFetchedOnEdit()
    {
        // Re-fetching the whole selection on every save would be a query per
        // edit for no reason.
        Assert.Contains("ToAdd.Contains(x.GenreId)", Controller());
    }

    [Fact]
    public void DetailsShowsLabelsNotIds()
    {
        var plan = TestContext.Plan(Ir(), "Book");
        var model = new Renderer(null).Render("ViewModelDetails.scriban", plan, ("model", plan["detailsModel"]));

        Assert.Contains("public List<string> GenreNames { get; set; } = [];", model);
        Assert.Contains("GenreNames = x.Genres.Select(t => t.Name).ToList()", Controller());

        var view = TestContext.Render(Ir(), "Book", "ViewDetails.scriban");

        Assert.Contains(@"string.Join("", "", Model.GenreNames)", view);
    }

    [Fact]
    public void FormPassesItemsToTheCheckboxListTemplate()
    {
        var plan = TestContext.Plan(Ir(), "Book");

        var create = new Renderer(null).Render("ViewForm.scriban", plan,
            ("model", plan["createModel"]), ("formAction", "Create"), ("pageTitle", "New book"));

        Assert.Contains("@Html.EditorFor(m => m.GenreIds, new { items = Model.GenreItems", create);
    }

    [Fact]
    public void ManyToManyIsNotAListColumn()
    {
        // A grid cell holding an unbounded list of labels is not useful.
        var columns = (TestContext.EntityIn(Ir(), "Book")["index"]!["listColumns"] as JsonArray)!
            .Select(c => c!.GetValue<string>());

        Assert.DoesNotContain("Genres", columns);
        Assert.DoesNotContain("GenreIds", columns);
    }

    [Fact]
    public void ManyToManyIsNotSearchable()
    {
        // Filtering by a linked set needs an Any() predicate and a different
        // control. Out of scope, and silently generating a broken filter would
        // be worse than leaving it out.
        Assert.False(TestContext.PropertyIn(Ir(), "Book", "Genres")["search"]!["enabled"]!.GetValue<bool>());
    }
}
