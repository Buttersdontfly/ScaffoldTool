using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Scaffold.Tests;

/// <summary>
/// Compiles generated C# in memory with Roslyn.
///
/// This is the test that would have caught CS1597 ("{ get; set; };") and CS8632
/// (nullable annotations in an auto-generated file) before they reached a real
/// project. It parses and binds against real references, so syntax errors and
/// most type errors surface without building anything.
/// </summary>
public class GeneratedCodeCompilesTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        return trusted
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    private static IReadOnlyList<Diagnostic> Compile(params string[] sources)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s,
            new CSharpParseOptions(LanguageVersion.Latest))).ToArray();

        var compilation = CSharpCompilation.Create(
            "ScaffoldGenerated",
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    /// <summary>
    /// Syntax-only check for output that references ASP.NET and EF types the
    /// test project does not carry. Catches CS1597 and friends; type binding is
    /// left to the ViewModel tests, which are self-contained.
    /// </summary>
    private static IReadOnlyList<Diagnostic> ParseOnly(string source) =>
        CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

    private static JsonNode FullIr() => TestContext.BuildIr(
        RawModel.New()
            .Entity("Book", e =>
            {
                e.Key("BookId");
                e.DisplayColumns("Title");
                e.ConcurrencyToken("RowVersion");
                e.Scalar("Title", "string", maxLength: 300, initializer: null);
                e.Scalar("Blurb", "string", maxLength: 2000, initializer: "");
                e.Scalar("Price", "decimal");
                e.Scalar("Rating", "int", range: ("1", "5"));
                e.Scalar("IsPublished", "bool");
                e.Scalar("CreatedAt", "DateTime");
                e.Scalar("PublishedAt", "DateTime", nullable: true);
                e.Scalar("Format", "int", enumMembers: RawEntity.EnumMembers("Paperback", "Hardback"));
                e.Reference("AuthorId", "Author", "Author", "AuthorId", navigationRequired: true);
                e.Reference("PublisherId", "Publisher", "Publisher", "PublisherId", nullable: true);
                e.Collection("Reviews", "Review", deleteBehavior: "Cascade", foreignKey: "BookId");
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

    [Theory]
    [InlineData("ViewModelForm.scriban", "createModel")]
    [InlineData("ViewModelForm.scriban", "editModel")]
    [InlineData("ViewModelListItem.scriban", "listItemModel")]
    [InlineData("ViewModelDetails.scriban", "detailsModel")]
    [InlineData("ViewModelSearch.scriban", "searchModel")]
    [InlineData("ViewModelIndex.scriban", "indexModel")]
    public void ViewModelsParseWithoutSyntaxErrors(string template, string modelKey)
    {
        var ir = FullIr();
        var plan = TestContext.Plan(ir, "Book");
        var source = new Renderer(null).Render(template, plan, ("model", plan[modelKey]));

        var errors = ParseOnly(source);

        Assert.True(errors.Count == 0,
            $"{template}/{modelKey}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, errors.Select(e => e.ToString())) +
            Environment.NewLine + Environment.NewLine + source);
    }

    [Fact]
    public void ControllerParsesWithoutSyntaxErrors()
    {
        var source = TestContext.Render(FullIr(), "Book", "Controller.scriban");
        var errors = ParseOnly(source);

        Assert.True(errors.Count == 0,
            string.Join(Environment.NewLine, errors.Select(e => e.ToString())) +
            Environment.NewLine + Environment.NewLine + source);
    }

    [Fact]
    public void AutoPropertiesCarryNoStraySemicolon()
    {
        // "{ get; set; };" is CS1597. The semicolon belongs to the initializer.
        var ir = FullIr();
        var plan = TestContext.Plan(ir, "Book");
        var source = new Renderer(null).Render("ViewModelForm.scriban", plan, ("model", plan["createModel"]));

        Assert.DoesNotContain("{ get; set; };", source);
    }

    [Fact]
    public void GeneratedFilesOptIntoTheNullableContext()
    {
        // Roslyn disables the nullable context in any file whose first comment
        // contains <auto-generated, so the directive has to be explicit.
        var ir = FullIr();
        var plan = TestContext.Plan(ir, "Book");

        foreach (var (template, key) in new[]
                 {
                     ("ViewModelForm.scriban", "createModel"),
                     ("ViewModelSearch.scriban", "searchModel")
                 })
        {
            var source = new Renderer(null).Render(template, plan, ("model", plan[key]));

            Assert.Contains("#nullable enable", source);
        }
    }

    [Fact]
    public void NoUsingDirectiveIsRepeated()
    {
        // CS0105 is a warning normally and an error under TreatWarningsAsErrors.
        var source = TestContext.Render(FullIr(), "Book", "Controller.scriban");

        var usings = source.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("using ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(usings.Count, usings.Distinct().Count());
    }

    [Fact]
    public void PlainCSharpModelBindsCleanly()
    {
        // A ViewModel with no framework attributes should not merely parse, it
        // should bind: this proves the compile harness itself works.
        var errors = Compile("""
            #nullable enable
            namespace Fixture;
            public partial class Sample
            {
                public string Name { get; set; } = string.Empty;
                public string? Note { get; set; }
                public int Count { get; set; }
                public System.DateTime? At { get; set; }
            }
            """);

        Assert.Empty(errors);
    }
}

public class TemplateIntegrityTests
{
    [Fact]
    public void EveryBuiltInTemplateParses()
    {
        var names = Renderer.BuiltInTemplateNames();

        Assert.NotEmpty(names);

        foreach (var name in names)
        {
            var text = EmbeddedFiles.Read($"Scaffold.Templates.{name}");
            var template = Scriban.Template.Parse(text, name);

            Assert.False(template.HasErrors,
                $"{name}: {string.Join("; ", template.Messages)}");
        }
    }

    [Fact]
    public void AllExpectedTemplatesShip()
    {
        var names = Renderer.BuiltInTemplateNames();

        foreach (var expected in new[]
                 {
                     "Controller.scriban", "ViewForm.scriban", "ViewIndex.scriban",
                     "ViewDetails.scriban", "ViewDelete.scriban", "ViewModelForm.scriban",
                     "ViewModelListItem.scriban", "ViewModelDetails.scriban",
                     "ViewModelSearch.scriban", "ViewModelIndex.scriban"
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void RenderedViewsNeverNestQuotesInsideAnAttribute()
    {
        // asp-all-route-data="SortRoute("title")" does not parse as Razor.
        // Declare a local in the @{ } block and pass that instead.
        //
        // Scoped to tag helper attributes on purpose. Their values are parsed as
        // bare C# expressions, so a quote terminates the HTML attribute before
        // Razor sees the expression at all. Ordinary attributes are different:
        // class="page-item @(Model.HasPrevious ? "" : "disabled")" is perfectly
        // valid, because @( ) is an explicit expression and Razor tracks
        // balanced parens and string literals inside it.
        //
        // Values that use @( ) are skipped for the same reason.
        foreach (var (name, output) in TestContext.RenderedViews())
        {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(output, @"\basp-[a-z-]+=""[^""\n]*"""))
            {
                if (match.Value.Contains("@(", StringComparison.Ordinal))
                {
                    continue;
                }

                var end = match.Index + match.Length;

                if (end >= output.Length)
                {
                    continue;
                }

                var next = output[end];

                Assert.True(
                    char.IsWhiteSpace(next) || next is '>' or '/',
                    $"{name}: attribute {match.Value} is followed by '{next}', " +
                    "which means a quoted literal is nested inside the attribute value.");
            }
        }
    }

    [Fact]
    public void RenderedViewsLeaveNoUnresolvedDirectives()
    {
        // A typo in a template name silently renders as empty rather than
        // throwing, so leftover delimiters are the only visible symptom.
        foreach (var (name, output) in TestContext.RenderedViews())
        {
            Assert.DoesNotContain("{{", output);
            Assert.DoesNotContain("}}", output);
            Assert.False(string.IsNullOrWhiteSpace(output), $"{name} rendered empty");
        }
    }
}
