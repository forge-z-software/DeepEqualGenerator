using DeepEqual.Generator.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DeepEqual;

public sealed class ComparisonCodeFixTests
{
    [Theory]
    [InlineData("EQ003", "KeyMembers = new[] { \"SkuCodee\" }", "Invalid keys", "\"SkuCode\"")]
    [InlineData("EQ004", "Kind = CompareKind.Reference", "Reference comparison requires a reference type",
        "global::DeepEqual.Generator.Shared.CompareKind.Shallow")]
    public async Task OffersAConcreteEditForCommonRuleErrors(string id, string argument, string message, string expected)
    {
        const string Source = """
            class Item { public string SkuCode { get; set; } = ""; }
            class Container {
                [DeepCompare(__ARG__)]
                public Item[] Items { get; set; } = new Item[0];
            }
            """;
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Example", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var document = project.AddDocument("Example.cs", SourceText.From(Source.Replace("__ARG__", argument)));
        var root = (await document.GetSyntaxRootAsync(TestCancellation))!;
        var attribute = root.DescendantNodes().OfType<AttributeSyntax>().Single();
        var diagnostic = Diagnostic.Create(new DiagnosticDescriptor(id, "Comparison rule", message, "DeepEqual",
            DiagnosticSeverity.Error, true), attribute.GetLocation());
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), TestCancellation);

        await new ComparisonRuleCodeFix().RegisterCodeFixesAsync(context);
        var operations = await Assert.Single(actions).GetOperationsAsync(TestCancellation);
        var fixedDocument = Assert.Single(operations.OfType<ApplyChangesOperation>()).ChangedSolution.GetDocument(document.Id)!;
        Assert.Contains(expected, (await fixedDocument.GetTextAsync(TestCancellation)).ToString());
    }

    private static CancellationToken TestCancellation => CancellationToken.None;
}
