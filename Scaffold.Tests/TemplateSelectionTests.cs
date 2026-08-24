using System.Text.Json.Nodes;
using Xunit;

namespace Scaffold.Tests;

/// <summary>
/// The mapping from a property to an editor template is the tool's core claim.
/// Table-driven so a new convention costs one line, not one test.
/// </summary>
public class TemplateSelectionTests
{
    private static string? TemplateFor(Action<RawEntity> configure, params string[] available)
    {
        var raw = RawModel.New().Entity("Thing", e =>
        {
            e.Key("ThingId");
            configure(e);
        });

        var ir = TestContext.BuildIr(raw, available);
        var properties = (TestContext.EntityIn(ir, "Thing")["properties"] as JsonArray)!;

        // The property under test is whatever is not the key.
        var property = properties.First(p => p!["kind"]!.GetValue<string>() != "key")!;

        return TestContext.Str(property["editor"]?["template"]);
    }

    [Theory]
    // Resolved by type name
    // Not "Comment": that name matches the multiline convention, correctly.
    [InlineData("Label", "string", null, null, "String")]
    [InlineData("Count", "int", null, null, "Int32")]
    [InlineData("Total", "decimal", null, null, "Decimal")]
    [InlineData("Enabled", "bool", null, null, "Boolean")]
    [InlineData("StartsOn", "DateOnly", null, null, "DateOnly")]
    [InlineData("StartsAt", "TimeOnly", null, null, "TimeOnly")]
    [InlineData("CreatedAt", "DateTime", null, null, "DateTime")]
    // Resolved by [DataType]
    [InlineData("Contact", "string", null, "EmailAddress", "EmailAddress")]
    [InlineData("Secret", "string", null, "Password", "Password")]
    [InlineData("Link", "string", null, "Url", "Url")]
    [InlineData("Contact2", "string", null, "PhoneNumber", "PhoneNumber")]
    [InlineData("Blurb", "string", null, "MultilineText", "MultilineText")]
    // Forced by [UIHint], which outranks everything
    [InlineData("Anything", "string", "Color", null, "Color")]
    [InlineData("Email", "string", "Tags", null, "Tags")]
    [InlineData("Description", "string", "String", null, "String")]
    // Resolved by name convention
    [InlineData("Email", "string", null, null, "EmailAddress")]
    [InlineData("UserPassword", "string", null, null, "Password")]
    [InlineData("Website", "string", null, null, "Url")]
    [InlineData("MobilePhone", "string", null, null, "PhoneNumber")]
    [InlineData("BrandColour", "string", null, null, "Color")]
    [InlineData("Description", "string", null, null, "MultilineText")]
    [InlineData("Notes", "string", null, null, "MultilineText")]
    [InlineData("Comment", "string", null, null, "MultilineText")]
    [InlineData("Remarks", "string", null, null, "MultilineText")]
    [InlineData("UserName", "string", null, null, "UserName")]
    public void ChoosesExpectedTemplate(string name, string clrType, string? uiHint, string? dataType, string expected)
    {
        var actual = TemplateFor(e => e.Scalar(name, clrType, uiHint: uiHint, dataType: dataType));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnboundedStringIsNotMultilineOnLengthAlone()
    {
        // Activity.Title has no configured length and maps to nvarchar(max).
        // A textarea would be wrong for a title.
        Assert.Equal("String", TemplateFor(e => e.Scalar("Title", "string")));
    }

    [Fact]
    public void LongConfiguredStringIsMultiline()
    {
        Assert.Equal("MultilineText", TemplateFor(e => e.Scalar("Summary", "string", maxLength: 512)));
    }

    [Fact]
    public void ShortConfiguredStringIsNotMultiline()
    {
        Assert.Equal("String", TemplateFor(e => e.Scalar("Summary", "string", maxLength: 100)));
    }

    [Fact]
    public void ForeignKeyBecomesDropdown()
    {
        var actual = TemplateFor(e => e.Reference("AuthorId", "Author", "Author"));

        Assert.Equal("Dropdown", actual);
    }

    [Fact]
    public void SmallEnumBecomesRadioGroup()
    {
        var actual = TemplateFor(e =>
            e.Scalar("Format", "int", enumMembers: RawEntity.EnumMembers("A", "B", "C")));

        Assert.Equal("RadioGroup", actual);
    }

    [Fact]
    public void LargeEnumBecomesEnumTemplate()
    {
        var actual = TemplateFor(e =>
            e.Scalar("Status", "int", enumMembers: RawEntity.EnumMembers("A", "B", "C", "D", "E", "F")));

        Assert.Equal("Enum", actual);
    }

    [Fact]
    public void MissingTemplateFallsBackAndLeavesATodo()
    {
        // Color.cshtml deliberately absent from the catalog.
        var raw = RawModel.New().Entity("Thing", e =>
        {
            e.Key("ThingId");
            e.Scalar("BrandColor", "string", uiHint: "Color");
        });

        var ir = TestContext.BuildIr(raw, "String", "Int32");
        var property = TestContext.PropertyIn(ir, "Thing", "BrandColor");

        Assert.Equal("String", TestContext.Str(property["editor"]?["template"]));

        var todos = property["todo"] as JsonArray;

        Assert.NotNull(todos);
        Assert.Contains(todos!, t => t!.GetValue<string>().Contains("Color", StringComparison.Ordinal));
    }

    [Fact]
    public void NullableResolvesToTheUnderlyingTypeTemplate()
    {
        // MVC looks up Nullable<T> as T, which is why DateTime? lands on
        // DateTime.cshtml rather than falling through to Object.
        Assert.Equal("DateTime", TemplateFor(e => e.Scalar("EndAt", "DateTime", nullable: true)));
    }
}

public class RequirednessTests
{
    private static (bool Required, string Source) Requiredness(
        string clrType, bool nullable, string? initializer, bool initializerKnown = true)
    {
        var raw = RawModel.New().Entity("Thing", e =>
        {
            e.Key("ThingId");
            e.Scalar("Value", clrType, nullable: nullable,
                initializer: initializer, initializerKnown: initializerKnown);
        });

        var ir = TestContext.BuildIr(raw);
        var validation = TestContext.PropertyIn(ir, "Thing", "Value")["validation"]!;

        return (validation["required"]!.GetValue<bool>(), validation["requiredSource"]!.GetValue<string>());
    }

    [Fact]
    public void NullBangInitializerMeansRequired()
    {
        // public string Name { get; set; } = null!;
        var (required, source) = Requiredness("string", nullable: false, initializer: null);

        Assert.True(required);
        Assert.Equal("initializer", source);
    }

    [Fact]
    public void EmptyStringInitializerMeansNotRequired()
    {
        // public string Description { get; set; } = string.Empty;
        var (required, source) = Requiredness("string", nullable: false, initializer: "");

        Assert.False(required);
        Assert.Equal("initializer", source);
    }

    [Fact]
    public void NullableIsNeverRequired()
    {
        var (required, source) = Requiredness("string", nullable: true, initializer: null);

        Assert.False(required);
        Assert.Equal("efModel", source);
    }

    [Fact]
    public void NonNullableValueTypeIsRequired()
    {
        var (required, _) = Requiredness("int", nullable: false, initializer: null);

        Assert.True(required);
    }

    [Fact]
    public void UnreadableInitializerFallsBackToRequired()
    {
        // Entities with `required` members cannot be constructed reflectively,
        // so the probe reports nothing and the safe answer is required.
        var (required, source) = Requiredness("string", nullable: false, initializer: null, initializerKnown: false);

        Assert.True(required);
        Assert.Equal("default", source);
    }
}
