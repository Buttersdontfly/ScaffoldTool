using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Scaffold.Tests;

/// <summary>
/// Golden files: the rendered output is committed, and a change to a template
/// shows up as a reviewable diff rather than a silent behaviour change.
///
/// Regenerate after an intended change with:
///     $env:SCAFFOLD_UPDATE_GOLDEN = "1"; dotnet test
/// then read the diff before committing. If a diff is not obviously an
/// improvement, it is a regression.
/// </summary>
public class GoldenTests
{
    private static readonly bool Update =
        Environment.GetEnvironmentVariable("SCAFFOLD_UPDATE_GOLDEN") == "1";

    private static string GoldenDirectory
    {
        get
        {
            // Walk up from the test binary to the project so writes land in the
            // repo, not in bin/.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scaffold.Tests.csproj")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName ?? AppContext.BaseDirectory;

            return Path.Combine(root, "Golden");
        }
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public GoldenTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A missing golden file is written and PASSES.
    ///
    /// Git is the review gate: a new file shows up in `git status` and a changed
    /// one shows up as a diff, so failing the run adds a confusing red build
    /// without adding any information. Only a MISMATCH fails, because that is
    /// the case where committed output changed without anyone saying so.
    ///
    /// On a mismatch the actual output is written alongside as `.actual` so it
    /// can be diffed in an editor rather than read out of console escaping.
    /// </summary>
    private void Verify(string name, string actual)
    {
        Directory.CreateDirectory(GoldenDirectory);

        var path = Path.Combine(GoldenDirectory, name);
        var normalised = actual.Replace("\r\n", "\n").TrimEnd() + "\n";

        if (Update || !File.Exists(path))
        {
            File.WriteAllText(path, normalised);

            _output.WriteLine(Update
                ? $"Golden file updated: {path}"
                : $"Golden file created: {path} -- review the diff before committing.");

            return;
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd() + "\n";

        if (expected == normalised)
        {
            // Clean up a stale .actual from an earlier failing run.
            var stale = path + ".actual";

            if (File.Exists(stale))
            {
                File.Delete(stale);
            }

            return;
        }

        File.WriteAllText(path + ".actual", normalised);

        _output.WriteLine($"Golden mismatch. Diff:{Environment.NewLine}  {path}{Environment.NewLine}  {path}.actual");
        _output.WriteLine("If the change is intended: $env:SCAFFOLD_UPDATE_GOLDEN = \"1\"; dotnet test");

        Assert.Equal(expected, normalised);
    }

    // The same fixture the integrity tests render, so a change shows up in
    // both places rather than drifting between two copies.
    private static JsonNode Ir() => TestContext.SampleIr();

    [Fact]
    public void Model()
    {
        var json = Ir().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Verify("model.json", json);
    }

    [Theory]
    [InlineData("ViewModelForm.scriban", "createModel", "BookCreateModel.cs")]
    [InlineData("ViewModelForm.scriban", "editModel", "BookEditModel.cs")]
    [InlineData("ViewModelListItem.scriban", "listItemModel", "BookListItemModel.cs")]
    [InlineData("ViewModelDetails.scriban", "detailsModel", "BookDetailsModel.cs")]
    [InlineData("ViewModelSearch.scriban", "searchModel", "BookSearchModel.cs")]
    [InlineData("ViewModelIndex.scriban", "indexModel", "BookIndexModel.cs")]
    public void ViewModels(string template, string modelKey, string golden)
    {
        var plan = TestContext.Plan(Ir(), "Book");

        Verify(golden, new Renderer(null).Render(template, plan, ("model", plan[modelKey])));
    }

    [Fact]
    public void Controller() =>
        Verify("BooksController.cs", TestContext.Render(Ir(), "Book", "Controller.scriban"));

    [Fact]
    public void CreateView()
    {
        var plan = TestContext.Plan(Ir(), "Book");

        Verify("Create.cshtml", new Renderer(null).Render("ViewForm.scriban", plan,
            ("model", plan["createModel"]), ("formAction", "Create"), ("pageTitle", "New book")));
    }

    [Fact]
    public void EditView()
    {
        var plan = TestContext.Plan(Ir(), "Book");

        Verify("Edit.cshtml", new Renderer(null).Render("ViewForm.scriban", plan,
            ("model", plan["editModel"]), ("formAction", "Edit"), ("pageTitle", "Edit book")));
    }

    [Fact]
    public void IndexView() =>
        Verify("Index.cshtml", TestContext.Render(Ir(), "Book", "ViewIndex.scriban"));

    [Fact]
    public void DetailsView() =>
        Verify("Details.cshtml", TestContext.Render(Ir(), "Book", "ViewDetails.scriban"));

    [Fact]
    public void DeleteView() =>
        Verify("Delete.cshtml", TestContext.Render(Ir(), "Book", "ViewDelete.scriban"));
}

public class ConcurrencyAndCascadeTests
{
    private static JsonNode WithToken(string? token) => TestContext.BuildIr(
        RawModel.New().Entity("Book", e =>
        {
            e.Key("BookId");
            e.DisplayColumns("Title");

            if (token is not null)
            {
                e.ConcurrencyToken(token);
            }

            e.Scalar("Title", "string", maxLength: 300, initializer: null);
            e.Collection("Reviews", "Review", deleteBehavior: "Cascade", foreignKey: "BookId");
        }));

    [Fact]
    public void ConcurrencyHandlingAppearsOnlyWhenATokenExists()
    {
        var with = TestContext.Render(WithToken("RowVersion"), "Book", "Controller.scriban");
        var without = TestContext.Render(WithToken(null), "Book", "Controller.scriban");

        Assert.Contains("DbUpdateConcurrencyException", with);
        Assert.Contains("OriginalValue", with);

        // Never forced: an entity with no token gets a plain SaveChanges.
        Assert.DoesNotContain("DbUpdateConcurrencyException", without);
        Assert.DoesNotContain("OriginalValue", without);
    }

    [Fact]
    public void ConcurrencyTokenIsEditOnly()
    {
        var plan = TestContext.Plan(WithToken("RowVersion"), "Book");
        var renderer = new Renderer(null);

        var create = renderer.Render("ViewModelForm.scriban", plan, ("model", plan["createModel"]));
        var edit = renderer.Render("ViewModelForm.scriban", plan, ("model", plan["editModel"]));

        // A new row has nothing to be concurrent with.
        Assert.DoesNotContain("RowVersion", create);
        Assert.Contains("RowVersion", edit);
    }

    [Fact]
    public void DeletePageCountsWhatCascadesAway()
    {
        var ir = WithToken(null);

        Assert.Contains("ReviewsCount", TestContext.Render(ir, "Book", "ViewDelete.scriban"));
        Assert.Contains("ReviewsCount = x.Reviews.Count()", TestContext.Render(ir, "Book", "Controller.scriban"));
    }

    [Fact]
    public void RestrictingCollectionAddsNoCount()
    {
        var ir = TestContext.BuildIr(
            RawModel.New().Entity("Book", e =>
            {
                e.Key("BookId");
                e.DisplayColumns("Title");
                e.Scalar("Title", "string", initializer: null);
                e.Collection("Reviews", "Review", deleteBehavior: "Restrict", foreignKey: "BookId");
            }));

        Assert.DoesNotContain("ReviewsCount", TestContext.Render(ir, "Book", "ViewDelete.scriban"));
    }
}
