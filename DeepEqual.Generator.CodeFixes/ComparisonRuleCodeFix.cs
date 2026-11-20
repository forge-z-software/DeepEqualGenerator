using System;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeepEqual.Generator.CodeFixes;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ComparisonRuleCodeFix)), Shared]
public sealed class ComparisonRuleCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("EQ003", "EQ004");

    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            var attribute = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                .FirstAncestorOrSelf<AttributeSyntax>();
            if (attribute?.ArgumentList is null) continue;

            if (diagnostic.Id == "EQ004" &&
                diagnostic.GetMessage().StartsWith("Reference comparison requires", StringComparison.Ordinal))
            {
                var kind = attribute.ArgumentList.Arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "Kind");
                if (kind is not null)
                {
                    var replacement = SyntaxFactory.ParseExpression("global::DeepEqual.Generator.Shared.CompareKind.Shallow");
                    Register("Use shallow value comparison", kind.Expression, replacement, diagnostic);
                }
            }
            else if (diagnostic.Id == "EQ003" && await context.Document.GetSemanticModelAsync(context.CancellationToken)
                         .ConfigureAwait(false) is { } model)
            {
                var fieldOrProperty = attribute.Parent?.Parent;
                var type = fieldOrProperty switch
                {
                    PropertyDeclarationSyntax property => model.GetTypeInfo(property.Type, context.CancellationToken).Type,
                    FieldDeclarationSyntax field => model.GetTypeInfo(field.Declaration.Type, context.CancellationToken).Type,
                    _ => null
                };
                var element = type is IArrayTypeSymbol array ? array.ElementType :
                    type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                        ? named.TypeArguments[0]
                        : type?.AllInterfaces.FirstOrDefault(candidate => candidate.OriginalDefinition.SpecialType ==
                            SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0];
                if (element is null) continue;

                var names = element.GetMembers()
                    .Where(member => !member.IsStatic && member.DeclaredAccessibility == Accessibility.Public &&
                        (member is IFieldSymbol || member is IPropertySymbol
                            { IsIndexer: false, GetMethod.DeclaredAccessibility: Accessibility.Public }))
                    .Select(member => member.Name).Distinct(StringComparer.Ordinal).ToArray();
                var keys = attribute.ArgumentList.Arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "KeyMembers");
                if (keys is null) continue;
                foreach (var literal in keys.Expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
                {
                    if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                    var key = literal.Token.ValueText;
                    if (names.Contains(key, StringComparer.Ordinal)) continue;
                    var ranked = names.Select(name => (Name: name, Distance: Distance(key, name)))
                        .OrderBy(candidate => candidate.Distance).Take(2).ToArray();
                    if (ranked.Length == 0 || ranked[0].Distance > 2 ||
                        ranked.Length == 2 && ranked[0].Distance == ranked[1].Distance) continue;
                    var best = ranked[0].Name;
                    var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(best));
                    Register($"Change key to '{best}'", literal, replacement, diagnostic);
                }
            }
        }

        void Register(string title, SyntaxNode original, SyntaxNode replacement, Diagnostic diagnostic)
        {
            context.RegisterCodeFix(CodeAction.Create(title,
                _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(original, replacement.WithTriviaFrom(original)))),
                title), diagnostic);
        }
    }

    private static int Distance(string first, string second)
    {
        var row = new int[second.Length + 1];
        for (var j = 0; j < row.Length; j++) row[j] = j;
        for (var i = 1; i <= first.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j < row.Length; j++)
            {
                var next = row[j];
                var substitution = previous + (first[i - 1] == second[j - 1] ? 0 : 1);
                row[j] = Math.Min(substitution, 1 + Math.Min(row[j], row[j - 1]));
                previous = next;
            }
        }
        return row[second.Length];
    }
}
