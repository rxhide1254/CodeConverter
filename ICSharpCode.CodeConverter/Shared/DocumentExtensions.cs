﻿using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.CodeConverter.CSharp;
using ICSharpCode.CodeConverter.Util;
using ICSharpCode.CodeConverter.VB;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;
using VBasic = Microsoft.CodeAnalysis.VisualBasic;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.CodeConverter.Shared
{
    internal static class DocumentExtensions
    {
        public static async Task<Document> SimplifyStatements<TUsingDirectiveSyntax, TExpressionSyntax>(this Document convertedDocument, string unresolvedTypeDiagnosticId)
        where TUsingDirectiveSyntax : SyntaxNode where TExpressionSyntax : SyntaxNode
        {
            Func<SyntaxNode, bool> wouldBeSimplifiedIncorrectly =
                convertedDocument.Project.Language == LanguageNames.VisualBasic
                    ? (Func<SyntaxNode, bool>) VbWouldBeSimplifiedIncorrectly
                    : CsWouldBeSimplifiedIncorrectly;
            var originalRoot = await convertedDocument.GetSyntaxRootAsync();
            var nodesWithUnresolvedTypes = (await convertedDocument.GetSemanticModelAsync()).GetDiagnostics()
                .Where(d => d.Id == unresolvedTypeDiagnosticId && d.Location.IsInSource)
                .Select(d => originalRoot.FindNode(d.Location.SourceSpan).GetAncestor<TUsingDirectiveSyntax>())
                .ToLookup(d => (SyntaxNode) d);
            var nodesToConsider = originalRoot
                .DescendantNodes(n =>
                    !(n is TExpressionSyntax) && !nodesWithUnresolvedTypes.Contains(n) &&
                    !wouldBeSimplifiedIncorrectly(n))
                .ToArray();
            var doNotSimplify = nodesToConsider
                .Where(n => nodesWithUnresolvedTypes.Contains(n) || wouldBeSimplifiedIncorrectly(n))
                .SelectMany(n => n.AncestorsAndSelf())
                .ToImmutableHashSet();
            var toSimplify = nodesToConsider.Where(n => !doNotSimplify.Contains(n));
            var newRoot = originalRoot.ReplaceNodes(toSimplify, (orig, rewritten) =>
                rewritten.WithAdditionalAnnotations(Simplifier.Annotation)
            );

            var document = await convertedDocument.WithReducedRootAsync(newRoot);
            return document;
        }

        private static bool VbWouldBeSimplifiedIncorrectly(SyntaxNode n)
        {
            //Roslyn bug: empty argument list gets removed and changes behaviour: https://github.com/dotnet/roslyn/issues/40442
            return n is VBSyntax.InvocationExpressionSyntax ies && !ies.ArgumentList.Arguments.Any()
                   // Roslyn bug: Tries to simplify to "InferredFieldInitializerSyntax" which cannot be placed within an ObjectCreationExpression https://github.com/icsharpcode/CodeConverter/issues/484
                   || n is VBSyntax.ObjectCreationExpressionSyntax;
        }

        private static bool CsWouldBeSimplifiedIncorrectly(SyntaxNode n)
        {
            return false;
        }

        public static async Task<Document> WithExpandedRootAsync(this Document document)
        {
            if (document.Project.Language == LanguageNames.VisualBasic) {
                document = await ExpandAsync(document, VbNameExpander.Instance);
            } else {
                document = await ExpandAsync(document, CsExpander.Instance);
            }

            return document;
        }

        private static async Task<Document> ExpandAsync(Document document, ISyntaxExpander expander)
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var workspace = document.Project.Solution.Workspace;
            var root = await document.GetSyntaxRootAsync();
            try {
                var newRoot = root.ReplaceNodes(root.DescendantNodes(n => expander.ShouldExpandWithinNode(n, root, semanticModel)).Where(n => expander.ShouldExpandNode(n, root, semanticModel)),
                    (node, rewrittenNode) => TryExpandNode(expander, node, root, semanticModel, workspace)
                );
                return document.WithSyntaxRoot(newRoot);
            } catch (Exception ex) {
                var warningText = "Conversion warning: Qualified name reduction failed for this file. " + ex;
                return document.WithSyntaxRoot(WithWarningAnnotation(root, warningText));
            }
        }

        private static SyntaxNode TryExpandNode(ISyntaxExpander expander, SyntaxNode node, SyntaxNode root, SemanticModel semanticModel, Workspace workspace)
        {
            try {
                return expander.ExpandNode(node, root, semanticModel, workspace);
            } catch (Exception ex) {
                var warningText = new ExceptionWithNodeInformation(ex, node, "Conversion warning").ToString();
                return WithWarningAnnotation(node, warningText);
            }
        }

        private static async Task<Document> WithReducedRootAsync(this Document doc, SyntaxNode syntaxRoot = null)
        {
            var root = syntaxRoot ?? await doc.GetSyntaxRootAsync();
            var withSyntaxRoot = doc.WithSyntaxRoot(root);
            try {
                return await Simplifier.ReduceAsync(withSyntaxRoot);
            } catch (Exception ex) {
                var warningText = "Conversion warning: Qualified name reduction failed for this file. " + ex;
                return doc.WithSyntaxRoot(WithWarningAnnotation(root, warningText));
            }
        }

        private static SyntaxNode WithWarningAnnotation(SyntaxNode node, string warningText)
        {
            return node.WithAdditionalAnnotations(new SyntaxAnnotation(AnnotationConstants.ConversionErrorAnnotationKind, warningText));
        }
    }
}