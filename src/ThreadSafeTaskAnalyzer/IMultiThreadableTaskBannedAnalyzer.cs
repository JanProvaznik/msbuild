// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

#pragma warning disable RS1012 // Start action has no registered actions
#pragma warning disable RS2008 // Start action has no registered actions
namespace Microsoft.Build.Utilities.Analyzer
{
    /// <summary>
    /// Analyzer that bans APIs when used within implementations of IMultiThreadableTask.
    /// Multithreadable tasks are run in parallel and must not use APIs that depend on process-global state
    /// such as current working directory, environment variables, or process-wide culture settings.
    /// </summary>
    public abstract class IMultiThreadableTaskBannedAnalyzer<TSyntaxKind> : DiagnosticAnalyzer
        where TSyntaxKind : struct
    {
        private const string IMultiThreadableTaskInterfaceName = "Microsoft.Build.Framework.IMultiThreadableTask";

        /// <summary>
        /// Diagnostic rule for detecting banned API usage in IMultiThreadableTask implementations.
        /// </summary>
        public static readonly DiagnosticDescriptor MultiThreadableTaskSymbolIsBannedRule = new DiagnosticDescriptor(
            id: "MSB4260",
            title: "Symbol is banned in IMultiThreadableTask implementations",
            messageFormat: "Symbol '{0}' is banned in IMultiThreadableTask implementations{1}",
            category: "Usage",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "This symbol is banned when used within types that implement IMultiThreadableTask due to threading concerns. Multithreadable tasks should not use APIs that depend on process-global state such as current working directory, environment variables, or process-wide culture settings.");

        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(MultiThreadableTaskSymbolIsBannedRule);

        protected abstract SymbolDisplayFormat SymbolDisplayFormat { get; }
        protected abstract bool IsTypeDeclaration(SyntaxNode node);

        /// <summary>
        /// Gets types whose methods with string path parameters should be checked.
        /// These are safe IF called with absolute paths (wrapped with TaskEnvironment.GetAbsolutePath).
        /// </summary>
        private static ImmutableArray<string> GetTypesWithPathMethods()
        {
            return ImmutableArray.Create(
                "System.IO.File",
                "System.IO.Directory",
                "System.IO.FileInfo",
                "System.IO.DirectoryInfo",
                "System.IO.FileStream",
                "System.IO.StreamReader",
                "System.IO.StreamWriter");
        }

        /// <summary>
        /// Gets the hardcoded list of APIs that are ALWAYS banned, regardless of arguments.
        /// These are APIs that should NEVER be used in IMultiThreadableTask implementations.
        /// </summary>
        private static ImmutableArray<(string DeclarationId, string Message)> GetBannedApiDefinitions()
        {
            return ImmutableArray.Create(
                // System.IO.Path - GetFullPath relies on current directory
                ("M:System.IO.Path.GetFullPath(System.String)", "Uses current working directory - use TaskEnvironment.GetAbsolutePath instead"),
                ("M:System.IO.Path.GetFullPath(System.String,System.String)", "Base path parameter may cause issues - use TaskEnvironment.GetAbsolutePath instead"),

                // System.Environment Class - Modifies or accesses process-level state
                ("P:System.Environment.CurrentDirectory", "Accesses process-level state - use TaskEnvironment.ProjectDirectory instead"),
                ("M:System.Environment.SetEnvironmentVariable(System.String,System.String)", "Modifies process-level state - use TaskEnvironment.SetEnvironmentVariable instead"),
                ("M:System.Environment.SetEnvironmentVariable(System.String,System.String,System.EnvironmentVariableTarget)", "Modifies process-level state - use TaskEnvironment.SetEnvironmentVariable instead"),
                ("M:System.Environment.Exit(System.Int32)", "Terminates entire process - return false from task or throw exception instead"),
                ("M:System.Environment.FailFast(System.String)", "Terminates entire process - return false from task or throw exception instead"),
                ("M:System.Environment.FailFast(System.String,System.Exception)", "Terminates entire process - return false from task or throw exception instead"),
                ("M:System.Environment.FailFast(System.String,System.Exception,System.String)", "Terminates entire process - return false from task or throw exception instead"),

                // System.Diagnostics.Process Class - Process control and startup
                ("M:System.Diagnostics.Process.Kill", "Terminates process"),
                ("M:System.Diagnostics.Process.Kill(System.Boolean)", "Terminates process"),
                ("M:System.Diagnostics.Process.Start(System.String)", "May inherit environment and working directory - use TaskEnvironment.GetProcessStartInfo instead"),
                ("M:System.Diagnostics.Process.Start(System.String,System.String)", "May inherit environment and working directory - use TaskEnvironment.GetProcessStartInfo instead"),

                // System.Diagnostics.ProcessStartInfo Class - All constructors may inherit process state
                ("M:System.Diagnostics.ProcessStartInfo.#ctor", "May inherit process state - use TaskEnvironment.GetProcessStartInfo instead"),
                ("M:System.Diagnostics.ProcessStartInfo.#ctor(System.String)", "May inherit process state - use TaskEnvironment.GetProcessStartInfo instead"),
                ("M:System.Diagnostics.ProcessStartInfo.#ctor(System.String,System.String)", "May inherit process state - use TaskEnvironment.GetProcessStartInfo instead"),

                // System.Threading.ThreadPool Class - Modifies process-wide settings
                ("M:System.Threading.ThreadPool.SetMinThreads(System.Int32,System.Int32)", "Modifies process-wide settings"),
                ("M:System.Threading.ThreadPool.SetMaxThreads(System.Int32,System.Int32)", "Modifies process-wide settings"),

                // System.Globalization.CultureInfo Class - Culture modification
                ("P:System.Globalization.CultureInfo.DefaultThreadCurrentCulture", "Affects new threads - modify thread culture instead"),
                ("P:System.Globalization.CultureInfo.DefaultThreadCurrentUICulture", "Affects new threads - modify thread culture instead"),

                // Assembly Loading - May cause version conflicts in multithreaded environments
                ("M:System.Reflection.Assembly.LoadFrom(System.String)", "May cause version conflicts - use absolute paths and be aware of potential conflicts"),
                ("M:System.Reflection.Assembly.LoadFile(System.String)", "May cause version conflicts - be aware of potential conflicts"),
                ("M:System.Reflection.Assembly.Load(System.String)", "May cause version conflicts - be aware of potential conflicts"),
                ("M:System.Reflection.Assembly.Load(System.Byte[])", "May cause version conflicts - be aware of potential conflicts"),
                ("M:System.Reflection.Assembly.Load(System.Byte[],System.Byte[])", "May cause version conflicts - be aware of potential conflicts"),
                ("M:System.Reflection.Assembly.LoadWithPartialName(System.String)", "May cause version conflicts - be aware of potential conflicts"),
                ("M:System.Activator.CreateInstanceFrom(System.String,System.String)", "May cause version conflicts - use absolute paths and be aware of potential conflicts"),
                ("M:System.Activator.CreateInstance(System.String,System.String)", "May cause version conflicts - be aware of potential conflicts"),

                // Console operations - May interfere with build output and logging
                ("P:System.Console.Out", "May interfere with build logging - use task logging methods instead"),
                ("P:System.Console.Error", "May interfere with build logging - use task logging methods instead"),
                ("P:System.Console.In", "May cause deadlocks in automated builds"),
                ("M:System.Console.Write(System.String)", "May interfere with build output - use task logging methods instead"),
                ("M:System.Console.WriteLine(System.String)", "May interfere with build output - use task logging methods instead"),
                ("M:System.Console.ReadLine", "May cause deadlocks in automated builds"),
                ("M:System.Console.ReadKey", "May cause deadlocks in automated builds"),

                // AppDomain operations - May cause version conflicts
                ("M:System.AppDomain.Load(System.String)", "May cause version conflicts - be aware of potential conflicts"),
                ("M:System.AppDomain.Load(System.Byte[])", "May cause version conflicts - be aware of potential conflicts"),
                ("M:System.AppDomain.CreateInstanceFrom(System.String,System.String)", "May cause version conflicts - use absolute paths and be aware of potential conflicts"),
                ("M:System.AppDomain.CreateInstance(System.String,System.String)", "May cause version conflicts - be aware of potential conflicts")
            );
        }

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private void OnCompilationStart(CompilationStartAnalysisContext compilationContext)
        {
            var bannedApis = BuildBannedApisDictionary(compilationContext.Compilation);
            if (bannedApis == null || bannedApis.Count == 0)
            {
                return;
            }

            // Register operation analysis
            compilationContext.RegisterOperationAction(
                context => AnalyzeOperationInContext(context, bannedApis),
                OperationKind.ObjectCreation,
                OperationKind.Invocation,
                OperationKind.EventReference,
                OperationKind.FieldReference,
                OperationKind.MethodReference,
                OperationKind.PropertyReference);
        }

        private Dictionary<string, BanFileEntry>? BuildBannedApisDictionary(Compilation compilation)
        {
            var result = new Dictionary<string, BanFileEntry>();
            var bannedApiDefinitions = GetBannedApiDefinitions();

            foreach (var (declarationId, message) in bannedApiDefinitions)
            {
                var symbols = GetSymbolsFromDeclarationId(compilation, declarationId);
                if (symbols.Any())
                {
                    result[declarationId] = new BanFileEntry(declarationId, message, symbols);
                }
            }

            return result.Count > 0 ? result : null;
        }

        private ImmutableArray<ISymbol> GetSymbolsFromDeclarationId(Compilation compilation, string declarationId)
        {
            // Simple implementation using DocumentationCommentId
            try
            {
                var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(declarationId, compilation);
                return symbols.ToArray().ToImmutableArray();
            }
            catch
            {
                // If parsing fails, return empty array
                return ImmutableArray<ISymbol>.Empty;
            }
        }

        private void AnalyzeOperationInContext(
            OperationAnalysisContext context,
            Dictionary<string, BanFileEntry> bannedApis)
        {
            // Check if we're in a class that implements IThreadSafeTask
            var containingType = GetContainingType(context.Operation);
            if (containingType == null || !IsIThreadSafeTaskImplementation(containingType))
            {
                return;
            }

            // Analyze the operation
            context.CancellationToken.ThrowIfCancellationRequested();

            switch (context.Operation)
            {
                case IInvocationOperation invocation:
                    // Check if this is a File/Directory method with unwrapped path argument
                    if (IsPathMethodWithUnwrappedArgument(invocation.TargetMethod, invocation.Arguments))
                    {
                        var diagnostic = Diagnostic.Create(
                            MultiThreadableTaskSymbolIsBannedRule,
                            invocation.Syntax.GetLocation(),
                            invocation.TargetMethod.ToDisplayString(SymbolDisplayFormat),
                            ": Uses current working directory - use absolute paths with TaskEnvironment.GetAbsolutePath");
                        context.ReportDiagnostic(diagnostic);
                    }
                    else
                    {
                        // Check if it's in the always-banned list
                        VerifySymbol(context.ReportDiagnostic, invocation.TargetMethod, context.Operation.Syntax, bannedApis);
                    }
                    break;

                case IMemberReferenceOperation memberReference:
                    VerifySymbol(context.ReportDiagnostic, memberReference.Member, context.Operation.Syntax, bannedApis);
                    break;

                case IObjectCreationOperation objectCreation:
                    if (objectCreation.Constructor != null)
                    {
                        // Check if this is a FileInfo/DirectoryInfo/FileStream/StreamReader/StreamWriter constructor with unwrapped path
                        if (IsPathMethodWithUnwrappedArgument(objectCreation.Constructor, objectCreation.Arguments))
                        {
                            var diagnostic = Diagnostic.Create(
                                MultiThreadableTaskSymbolIsBannedRule,
                                objectCreation.Syntax.GetLocation(),
                                objectCreation.Constructor.ContainingType.ToDisplayString(SymbolDisplayFormat),
                                ": Constructor uses current working directory - use absolute paths with TaskEnvironment.GetAbsolutePath");
                            context.ReportDiagnostic(diagnostic);
                        }
                        else
                        {
                            // Check if it's in the always-banned list
                            VerifySymbol(context.ReportDiagnostic, objectCreation.Constructor, context.Operation.Syntax, bannedApis);
                        }
                    }
                    break;
            }
        }

        private INamedTypeSymbol? GetContainingType(IOperation operation)
        {
            var current = operation;
            while (current != null)
            {
                if (current.SemanticModel != null)
                {
                    var typeDeclaration = current.Syntax.Ancestors().FirstOrDefault(IsTypeDeclaration);
                    if (typeDeclaration != null)
                    {
                        var symbol = current.SemanticModel.GetDeclaredSymbol(typeDeclaration);
                        if (symbol is INamedTypeSymbol typeSymbol)
                        {
                            return typeSymbol;
                        }
                    }
                }
                current = current.Parent;
            }
            return null;
        }

        private bool IsIThreadSafeTaskImplementation(INamedTypeSymbol typeSymbol)
        {
            return typeSymbol.AllInterfaces.Any(i => i.ToDisplayString() == IMultiThreadableTaskInterfaceName);
        }

        /// <summary>
        /// Checks if a method/constructor is on a path-related type with a string parameter
        /// that is NOT wrapped with TaskEnvironment.GetAbsolutePath().
        /// </summary>
        private bool IsPathMethodWithUnwrappedArgument(IMethodSymbol method, ImmutableArray<IArgumentOperation> arguments)
        {
            var containingTypeName = method.ContainingType?.ToDisplayString();
            if (containingTypeName == null)
            {
                return false;
            }

            // Check if this is one of the path-related types
            var pathTypes = GetTypesWithPathMethods();
            if (!pathTypes.Contains(containingTypeName))
            {
                return false;
            }

            // Check if method has at least one string parameter (likely a path)
            var hasStringParameter = method.Parameters.Any(p => p.Type.SpecialType == SpecialType.System_String);
            if (!hasStringParameter)
            {
                return false;
            }

            // Find the first string argument and check if it's wrapped
            foreach (var arg in arguments)
            {
                if (arg.Parameter?.Type.SpecialType == SpecialType.System_String)
                {
                    // Check if this argument is a call to TaskEnvironment.GetAbsolutePath()
                    if (IsWrappedWithGetAbsolutePath(arg.Value))
                    {
                        // Path is already wrapped - no warning needed
                        return false;
                    }
                    // Found unwrapped string argument - this needs a warning
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if an operation is a call to TaskEnvironment.GetAbsolutePath().
        /// </summary>
        private bool IsWrappedWithGetAbsolutePath(IOperation operation)
        {
            // Check for direct call: TaskEnvironment.GetAbsolutePath(...)
            if (operation is IInvocationOperation invocation)
            {
                var methodName = invocation.TargetMethod.Name;
                var containingType = invocation.TargetMethod.ContainingType?.ToDisplayString();
                
                if (methodName == "GetAbsolutePath" && 
                    containingType == "Microsoft.Build.Framework.TaskEnvironment")
                {
                    return true;
                }
            }

            // Check for conversion from AbsolutePath to string
            if (operation is IConversionOperation conversion)
            {
                var sourceType = conversion.Operand.Type?.ToDisplayString();
                if (sourceType == "Microsoft.Build.Framework.AbsolutePath")
                {
                    return true;
                }
                
                // Recursively check the converted expression
                return IsWrappedWithGetAbsolutePath(conversion.Operand);
            }

            return false;
        }

        private void VerifySymbol(
            Action<Diagnostic> reportDiagnostic,
            ISymbol symbol,
            SyntaxNode syntaxNode,
            Dictionary<string, BanFileEntry> bannedApis)
        {
            foreach (var kvp in bannedApis)
            {
                var declarationId = kvp.Key;
                var entry = kvp.Value;
                if (entry.Symbols.Any(bannedSymbol => SymbolEqualityComparer.Default.Equals(symbol, bannedSymbol)))
                {
                    var diagnostic = Diagnostic.Create(
                        MultiThreadableTaskSymbolIsBannedRule,
                        syntaxNode.GetLocation(),
                        symbol.ToDisplayString(SymbolDisplayFormat),
                        string.IsNullOrWhiteSpace(entry.Message) ? "" : ": " + entry.Message);

                    reportDiagnostic(diagnostic);
                    return;
                }
            }
        }

        private sealed class BanFileEntry
        {
            public string DeclarationId { get; }
            public string Message { get; }
            public ImmutableArray<ISymbol> Symbols { get; }

            public BanFileEntry(string declarationId, string message, ImmutableArray<ISymbol> symbols)
            {
                DeclarationId = declarationId;
                Message = message;
                Symbols = symbols;
            }
        }
    }
}
