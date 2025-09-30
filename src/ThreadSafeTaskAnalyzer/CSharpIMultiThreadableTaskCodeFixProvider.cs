// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Microsoft.Build.Utilities.Analyzer
{
    /// <summary>
    /// Code fixer for IMultiThreadableTask banned API analyzer.
    /// Provides fixes for common path-related issues by wrapping calls with TaskEnvironment.GetAbsolutePath().
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CSharpIMultiThreadableTaskCodeFixProvider)), Shared]
    public class CSharpIMultiThreadableTaskCodeFixProvider : CodeFixProvider
    {
        private const string Title = "Wrap with TaskEnvironment.GetAbsolutePath()";

        public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("MSB4260");

        public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            // Find the node that triggered the diagnostic
            var node = root.FindNode(diagnosticSpan);

            // Only offer fix for string path arguments
            if (IsPathStringArgument(node, out var argumentSyntax))
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: Title,
                        createChangedDocument: c => WrapWithGetAbsolutePathAsync(context.Document, argumentSyntax!, c),
                        equivalenceKey: Title),
                    diagnostic);
            }
        }

        private bool IsPathStringArgument(SyntaxNode node, out ArgumentSyntax? argumentSyntax)
        {
            argumentSyntax = null;

            // Try to find the argument containing the path string
            var invocation = node as InvocationExpressionSyntax ?? node.Parent as InvocationExpressionSyntax;
            var objectCreation = node as ObjectCreationExpressionSyntax ?? node.Parent as ObjectCreationExpressionSyntax;

            ArgumentListSyntax? argumentList = null;

            if (invocation != null)
            {
                argumentList = invocation.ArgumentList;
            }
            else if (objectCreation != null)
            {
                argumentList = objectCreation.ArgumentList;
            }

            if (argumentList == null || argumentList.Arguments.Count == 0)
            {
                return false;
            }

            // Get the first argument (typically the path parameter)
            var firstArg = argumentList.Arguments[0];

            // Check if it's a string-type expression
            argumentSyntax = firstArg;
            return true;
        }

        private async Task<Document> WrapWithGetAbsolutePathAsync(
            Document document,
            ArgumentSyntax argumentSyntax,
            CancellationToken cancellationToken)
        {
            var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
            var generator = editor.Generator;

            // Get the expression from the argument
            var pathExpression = argumentSyntax.Expression;

            // Create: TaskEnvironment.GetAbsolutePath(pathExpression)
            var taskEnvironmentType = generator.IdentifierName("TaskEnvironment");
            var getAbsolutePathMember = generator.MemberAccessExpression(
                taskEnvironmentType,
                "GetAbsolutePath");

            var wrappedExpression = generator.InvocationExpression(
                getAbsolutePathMember,
                pathExpression);

            // Replace the argument expression with the wrapped version
            var newArgument = argumentSyntax.WithExpression((ExpressionSyntax)wrappedExpression);
            editor.ReplaceNode(argumentSyntax, newArgument);

            return editor.GetChangedDocument();
        }
    }
}
