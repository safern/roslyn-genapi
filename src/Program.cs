using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TryRoslynCompilation;

namespace RoslynGenApi
{
    class Program
    {
        static int Main(string[] args)
        {
            string assemblyArg = args[0];
            string libPaths = args[1];
            string outFile = null;
            if (args.Length > 2)
            {
                outFile = args[2];
            }

            AssemblyLoader loader = new AssemblyLoader();
            loader.LoadReferences(libPaths);
            IEnumerable<IAssemblySymbol> assemblies = loader.LoadAssemblies(assemblyArg);
            if (!assemblies.Any())
            {
                Console.WriteLine($"No assemblies were found in: {assemblyArg}");
                return 1;
            }

            if (loader.HasDiagnostics(out IEnumerable<Diagnostic> diagnostics))
            {
                foreach (Diagnostic diagnostic in diagnostics)
                {
                    Console.WriteLine(diagnostic.ToString());
                }
                return 1;
            }

            using TextWriter outStream = outFile != null ? new StreamWriter(outFile) : Console.Out;
            SourceWriter writer = new SourceWriter(outStream);

            foreach (IAssemblySymbol assembly in assemblies)
                writer.WriteAssembly(assembly);

            return 0;
        }

        public class SourceWriter
        {
            private readonly TextWriter _outStream;
            private readonly AdhocWorkspace _adhocWorkspace;
            private readonly SyntaxGenerator _generator;
            private readonly CSharpRewriterVisitor _rewriterVisitor;

            public SourceWriter() : this(null) { }
            
            public SourceWriter(TextWriter outStream)
            {
                _outStream = outStream ?? Console.Out;
                _adhocWorkspace = new AdhocWorkspace();
                _generator = SyntaxGenerator.GetGenerator(_adhocWorkspace, LanguageNames.CSharp);
                _rewriterVisitor = new CSharpRewriterVisitor();
            }

            public void WriteAssembly(IAssemblySymbol symbol) => WriteNamespaces(symbol.GlobalNamespace.GetNamespaceMembers());

            private void WriteNamespaces(IEnumerable<INamespaceSymbol> namespaceSymbols)
            {
                foreach (INamespaceSymbol namespaceSymbol in namespaceSymbols)
                    WriteNamespace(namespaceSymbol);
            }

            private void WriteNamespace(INamespaceSymbol namespaceSymbol)
            {
                IEnumerable<INamedTypeSymbol> types = namespaceSymbol.GetTypeMembers().Where(t => t.IsVisibleOutsideAssembly());

                if (types.Any())
                {
                    SyntaxNode namespaceDeclaration = _generator.NamespaceDeclaration(namespaceSymbol.ToDisplayString(), GetTypeNodes(types))
                                                                .NormalizeWhitespace()
                                                                .WithTrailingTrivia(Environment.NewLine);

                    _rewriterVisitor.Visit(namespaceDeclaration).WriteTo(_outStream);
                }

                WriteNamespaces(namespaceSymbol.GetNamespaceMembers());
            }

            private IEnumerable<SyntaxNode> GetTypeNodes(IEnumerable<INamedTypeSymbol> namedTypeSymbols)
            {
                List<SyntaxNode> list = new List<SyntaxNode>();
                foreach (INamedTypeSymbol namedTypeSymbol in namedTypeSymbols)
                {
                    list.Add(GetTypeNode(namedTypeSymbol));
                }

                return list;
            }

            private SyntaxNode GetTypeNode(INamedTypeSymbol namedTypeSymbol) =>
                _generator.Declaration(namedTypeSymbol);
        }

        public class CSharpRewriterVisitor : CSharpSyntaxRewriter
        {
            private const string Space = " ";

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                // Visit base list and members first as we could remove the base list.
                node = (ClassDeclarationSyntax)base.VisitClassDeclaration(node.WithTrailingTrivia(Environment.NewLine));
                
                if (node.BaseList == null)
                {
                    // we returned null when removing System.Object, let's add the new line again.
                    if (node.TypeParameterList == null)
                    {
                        node = node.WithIdentifier(node.Identifier.WithTrailingTrivia(Environment.NewLine));
                    }
                    else if (node.ConstraintClauses.Count <=0)
                    {
                        node = node.WithTypeParameterList(node.TypeParameterList.WithTrailingTrivia(Environment.NewLine));
                    }
                }

                return node;
            }

            public override SyntaxNode VisitEnumDeclaration(EnumDeclarationSyntax node)
            {
                return base.VisitEnumDeclaration(node.WithTrailingTrivia(Environment.NewLine));
            }

            public override SyntaxNode VisitBaseList(BaseListSyntax node)
            {
                ChildSyntaxList.Enumerator enumerator = node.ChildNodesAndTokens().GetEnumerator();
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is { IsNode: true } current)
                    {
                        SyntaxNode currentNode = current.AsNode();
                        if (currentNode is SimpleBaseTypeSyntax nodeToRemove && (nodeToRemove.Type.ToString() == "global::System.Object" || nodeToRemove.Type.ToString() == "System.Object"))
                        {
                            node = node.RemoveNode(nodeToRemove, SyntaxRemoveOptions.KeepNoTrivia);
                            break;
                        }
                    }
                }

                return node.ChildNodes().Any() ? base.VisitBaseList(node) : null;
            }

            public override SyntaxNode VisitQualifiedName(QualifiedNameSyntax node)
            {
                string qualifiedName = node.ToFullString();
                const string globalPrefix = "global::";
                if (qualifiedName.StartsWith(globalPrefix, StringComparison.Ordinal))
                {
                    node = node.WithLeft(SyntaxFactory.ParseName(node.Left.ToFullString().Substring(globalPrefix.Length)));
                }

                return base.VisitQualifiedName(node);
            }

            public override SyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
            {
                node = node.WithBody(GetEmptyBody())
                           .WithParameterList((ParameterListSyntax)node.ParameterList.WithTrailingTrivia(Space));
                return base.VisitConstructorDeclaration(node);
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                // visit subtree first to normalize type names.
                node = base.VisitMethodDeclaration(node) as MethodDeclarationSyntax;

                if (node.Modifiers.Where(token => token.IsKind(SyntaxKind.AbstractKeyword)).Any())
                {
                    return node;
                }

                if (node.ExpressionBody != null)
                {
                    node = node.WithExpressionBody(null);
                }

                if (node.ReturnType.ToString() != "System.Void")
                {
                    node = node.WithBody(GetThrowNullBody());
                }
                else
                {
                    node = node.WithBody(GetEmptyBody());
                }

                return node.WithParameterList((ParameterListSyntax)node.ParameterList.WithTrailingTrivia(Space));
            }

            public override SyntaxNode VisitPropertyDeclaration(PropertyDeclarationSyntax node)
            {
                SyntaxToken identifier = node.Identifier.WithTrailingTrivia(Space);
                return base.VisitPropertyDeclaration(node.WithIdentifier(identifier).WithAccessorList(node.AccessorList.RemoveLineFeed()));
            }

            public override SyntaxNode VisitIndexerDeclaration(IndexerDeclarationSyntax node)
            {
                BracketedParameterListSyntax parameterList = node.ParameterList;
                parameterList = parameterList.WithCloseBracketToken(parameterList.CloseBracketToken.WithTrailingTrivia(Space));

                return base.VisitIndexerDeclaration(node.WithParameterList(parameterList).WithAccessorList(node.AccessorList.RemoveLineFeed()));
            }

            public override SyntaxNode VisitAccessorDeclaration(AccessorDeclarationSyntax node)
            {
                return node.Kind() switch
                {

                    SyntaxKind.GetAccessorDeclaration => node.WithSemicolonToken(default)
                                                             .WithKeyword(node.Keyword.WithTrailingTrivia(Space).WithLeadingTrivia(string.Empty))
                                                             .WithBody(GetThrowNullBody(newLine: false)),
                    SyntaxKind.SetAccessorDeclaration => node.WithSemicolonToken(default)
                                                             .WithKeyword(node.Keyword.WithTrailingTrivia(Space).WithLeadingTrivia(string.Empty))
                                                             .WithBody(GetEmptyBody(newLine: false)),
                    _ => base.VisitAccessorDeclaration(node)
                };
            }

            public override SyntaxNode VisitTypeArgumentList(TypeArgumentListSyntax node) =>
                node.Update(node.LessThanToken, VisitList(node.Arguments), node.GreaterThanToken);

            private BlockSyntax GetThrowNullBody(bool newLine = true) => GetMethodBodyFromText(" throw null; ", newLine);

            private BlockSyntax GetEmptyBody(bool newLine = true)
            {
                BlockSyntax node = GetMethodBodyFromText(Space, newLine);
                return node.WithOpenBraceToken(node.OpenBraceToken.WithTrailingTrivia(Space));
            }

            private BlockSyntax GetMethodBodyFromText(string text, bool newLine = true) =>
                SyntaxFactory.Block(SyntaxFactory.ParseStatement(text))
                             .WithTrailingTrivia(newLine ? Environment.NewLine : Space);
        }
    }

    internal static class SyntaxNodeExtensions
    {
        public static T WithLeadingTrivia<T>(this T node, string text) where T : SyntaxNode =>
            node.WithLeadingTrivia(GetTrivia(text));

        public static T WithTrailingTrivia<T>(this T node, string text) where T : SyntaxNode =>
            node.WithTrailingTrivia(GetTrivia(text));

        public static SyntaxToken WithLeadingTrivia(this SyntaxToken token, string text) =>
            token.WithLeadingTrivia(GetTrivia(text));

        public static SyntaxToken WithTrailingTrivia(this SyntaxToken token, string text) =>
            token.WithTrailingTrivia(GetTrivia(text));

        public static AccessorListSyntax RemoveLineFeed(this AccessorListSyntax node) =>
            node.WithOpenBraceToken(node.OpenBraceToken.WithLeadingTrivia(string.Empty).WithTrailingTrivia(" "))
                .WithCloseBraceToken(node.CloseBraceToken.WithLeadingTrivia(string.Empty).WithTrailingTrivia(Environment.NewLine));

        private static SyntaxTrivia GetTrivia(string text) =>
            SyntaxFactory.Whitespace(text);

    }

    internal static class SymbolExtensions
    {
        public static bool IsVisibleOutsideAssembly(this ISymbol symbol) =>
            symbol.DeclaredAccessibility == Accessibility.Public ||
            symbol.DeclaredAccessibility == Accessibility.Protected ||
            symbol.DeclaredAccessibility == Accessibility.ProtectedOrInternal;
    }
}
