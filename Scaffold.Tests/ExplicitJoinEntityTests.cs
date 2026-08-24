using System.Text.Json.Nodes;
using Xunit;

namespace Scaffold.Tests;

/// <summary>
/// An explicit join entity is the case that breaks two assumptions at once: a
/// key is never a foreign key, and a key is always database-generated. Both are
/// false here, and getting either wrong produces a Create form with no way to
/// set the relationship -- EF then refuses the insert with "the value of X is
/// unknown when attempting to save changes".
/// </summary>
public class ExplicitJoinEntityTests
{
    private static JsonNode Ir() => TestContext.BuildIr(
        RawModel.New()
            .Entity("EmployeeProject", e =>
            {
                e.KeyReference("EmployeeId", navigation: null, "Employee", "Id", "int", "FirstName");
                e.KeyReference("ProjectId", navigation: null, "Project", "Id", "int", "Name");
                e.Scalar("AssignedAt", "DateTime");
                e.Scalar("Role", "string", maxLength: 100, initializer: "");
            })
            .Entity("Employee", e =>
            {
                e.Key("Id");
                e.DisplayColumns("FirstName");
                e.Scalar("FirstName", "string", initializer: null);
            })
            .Entity("Project", e =>
            {
                e.Key("Id");
                e.DisplayColumns("Name");
                e.Scalar("Name", "string", initializer: null);
            }));

    private static string Controller() =>
        TestContext.Render(Ir(), "EmployeeProject", "Controller.scriban");

    private static string Model(string surface)
    {
        var plan = TestContext.Plan(Ir(), "EmployeeProject");

        return new Renderer(null).Render("ViewModelForm.scriban", plan, ("model", plan[surface]));
    }

    [Fact]
    public void AKeyThatIsAlsoAForeignKeyStaysAForeignKey()
    {
        // Reporting it as a plain key discards the relationship, and the form
        // then has no control that can set it.
        var property = TestContext.PropertyIn(Ir(), "EmployeeProject", "EmployeeId");

        Assert.Equal("reference", TestContext.Str(property["kind"]));
        Assert.True(property["isKey"]!.GetValue<bool>());
        Assert.Equal("Dropdown", TestContext.Str(property["editor"]?["template"]));
    }

    [Fact]
    public void CreateAsksForBothKeyParts()
    {
        var create = Model("createModel");

        Assert.Contains("public int EmployeeId { get; set; }", create);
        Assert.Contains("public int ProjectId { get; set; }", create);
        Assert.Contains("public IEnumerable<SelectListItem> EmployeeIdItems { get; set; } = [];", create);
    }

    [Fact]
    public void CreateSetsBothKeyPartsOnTheEntity()
    {
        // This is the assignment whose absence caused
        // "the value of EmployeeProject.EmployeeId is unknown".
        var source = Controller();

        Assert.Contains("EmployeeId = vm.EmployeeId", source);
        Assert.Contains("ProjectId = vm.ProjectId", source);
    }

    [Fact]
    public void EditNeverRewritesTheKey()
    {
        var source = Controller();

        // Changing a key part means a different row, not an edit of this one.
        Assert.DoesNotContain("entity.EmployeeId = vm.EmployeeId;", source);
        Assert.DoesNotContain("entity.ProjectId = vm.ProjectId;", source);

        // Payload columns are still updated.
        Assert.Contains("entity.Role = vm.Role;", source);
        Assert.Contains("entity.AssignedAt = vm.AssignedAt;", source);
    }

    [Fact]
    public void EditCarriesTheKeyAsHiddenFieldsNotAsASecondDropdown()
    {
        var edit = Model("editModel");

        Assert.Contains("public int EmployeeId { get; set; }", edit);

        // Only once: the hidden key block, not also an editor.
        Assert.DoesNotContain(@"[UIHint(""Dropdown"")]", edit);
    }

    [Fact]
    public void DetailsShowsTheRawKeyWhenThereIsNoNavigationToWalk()
    {
        // No navigation property on the join entity means no path to a display
        // column. Emitting x.EmployeeId.FirstName would not compile.
        var source = Controller();

        Assert.DoesNotContain("x.EmployeeId.FirstName", source);
        Assert.Contains("EmployeeId = x.EmployeeId", source);
    }

    [Fact]
    public void CompositeKeyRoutingStillApplies()
    {
        var source = Controller();

        Assert.Contains("x.EmployeeId == employeeId && x.ProjectId == projectId", source);
        Assert.Contains(".ThenBy(x => x.EmployeeId).ThenBy(x => x.ProjectId)", source);
    }
}

public class NaturalKeyTests
{
    private static JsonNode Ir() => TestContext.BuildIr(
        RawModel.New().Entity("Country", e =>
        {
            e.NaturalKey("Code", "string", maxLength: 2);
            e.DisplayColumns("Name");
            e.Scalar("Name", "string", maxLength: 100, initializer: null);
        }));

    [Fact]
    public void ANaturalKeyIsAskedForOnCreate()
    {
        // Nothing generates it, so if the form does not ask, the insert fails.
        var include = TestContext.PropertyIn(Ir(), "Country", "Code")["include"]!;

        Assert.True(include["create"]!.GetValue<bool>());
    }

    [Fact]
    public void ANaturalKeyGetsAnEditorTemplate()
    {
        var property = TestContext.PropertyIn(Ir(), "Country", "Code");

        Assert.Equal("String", TestContext.Str(property["editor"]?["template"]));
    }

    [Fact]
    public void AGeneratedKeyIsStillNeverAskedFor()
    {
        var ir = TestContext.BuildIr(
            RawModel.New().Entity("Book", e =>
            {
                e.Key("BookId");
                e.Scalar("Title", "string", initializer: null);
            }));

        var include = TestContext.PropertyIn(ir, "Book", "BookId")["include"]!;

        Assert.False(include["create"]!.GetValue<bool>());
        Assert.Null(TestContext.Str(TestContext.PropertyIn(ir, "Book", "BookId")["editor"]?["template"]));
    }

    [Fact]
    public void CreateSetsTheNaturalKey()
    {
        Assert.Contains("Code = vm.Code", TestContext.Render(Ir(), "Country", "Controller.scriban"));
    }
}
