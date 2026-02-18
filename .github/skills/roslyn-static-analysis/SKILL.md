---
name: roslyn-static-analysis
description: Guide for writing quick, targeted Roslyn-based static analysis scripts to trace data flow, verify invariants, or audit code patterns in the MSBuild codebase. Use this when asked to do static analysis, trace data flow, audit code paths, or verify properties hold across the codebase.
---

# Writing Roslyn Static Analysis Scripts

Quick guide for building targeted, one-off Roslyn analysis tools to answer questions like "can X ever receive non-canonical input?" or "does every call to Y pass through Z first?"

## When to Use This

- Tracing data flow backwards from a specific API (e.g., "what values reach this setter?")
- Verifying an invariant holds across all callers (e.g., "is input always normalized?")
- Auditing all usages of a pattern (e.g., "every `ignoreRootedCheck: true`")
- Anything where grep/string-matching is too imprecise

## Setup (< 1 minute)

```powershell
# Create a throwaway console project
mkdir RoslynAnalysis && cd RoslynAnalysis
dotnet new console --force --no-restore
dotnet add package Microsoft.CodeAnalysis.CSharp --no-restore
dotnet restore
```

Only one NuGet package needed: `Microsoft.CodeAnalysis.CSharp`. No need for `Microsoft.CodeAnalysis.Workspaces` — syntax-only analysis is much simpler and faster.

## Key Design Decision: Syntax-Only vs Semantic Model

**Use syntax-only analysis** (recommended for one-off scripts):
- Parse files with `CSharpSyntaxTree.ParseText()` — no compilation needed
- Pros: Fast, simple, no reference resolution headaches
- Cons: Can't resolve types, overloads, or cross-assembly references
- Good enough for: tracing named properties/fields/parameters through explicit assignments

**Use semantic model** (only if you need type resolution):
- Requires `CSharpCompilation.Create()` with all references
- Needed for: resolving overloads, tracking through interfaces, type-based queries
- Much more setup work — usually not worth it for one-off analysis

## Architecture: Pre-Index Then Trace

The most important optimization: **build indices once, then look up during tracing**.

Scanning `DescendantNodes()` on a large file (e.g., 3000+ line ProjectInstance.cs) is expensive.
Doing it recursively in a trace function causes O(n^k) behavior and hangs.

```csharp
// ❌ BAD: Scanning inside recursive trace function
void Trace(string expr) {
    foreach (var (file, tree) in trees) {
        var root = tree.GetRoot();
        var props = root.DescendantNodes()  // Scans entire file EVERY call
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Identifier.Text == name);
    }
}

// ✅ GOOD: Pre-build index, then look up
var propertyIndex = new Dictionary<string, List<(string File, int Line, string GetterExpr)>>();
// Build once during setup...
foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>()) {
    propertyIndex[prop.Identifier.Text] = ...;
}
// Then O(1) lookup during trace
if (propertyIndex.TryGetValue(name, out var props)) { ... }
```

### What to Index

Build these indices once from all syntax trees:

1. **Properties**: name → getter expression text (from expression body, getter body, or return statements)
2. **Field assignments**: `_fieldName` → list of (file, line, RHS expression)
3. **Local variables**: (file, varName) → initializer expression
4. **Parameters**: (file, paramName) → (methodName, paramIndex, isConstructor)
5. **Call sites**: methodName → list of (file, line, argument expressions[])

## Trace Algorithm

Recursive backward trace from each "sink" expression:

```
Trace(expr, sourceFile, depth, visited):
  if depth > limit: return MAX_DEPTH
  if already visited: return CYCLE
  if expr contains safe terminal (e.g., "NormalizePath"): return SAFE ✅
  
  Handle special syntax:
    - "a ?? b" → trace both branches
    - "(field = expr)" → trace RHS
    - "Method(args)" → check if method is safe, or trace args
  
  Extract trailing identifier from expr (e.g., "obj.Foo" → "Foo")
  
  Look up in indices (in priority order):
    1. Property index → trace getter expression
    2. Field assignment index → trace each RHS
    3. Local variable index → trace initializer
    4. Parameter index → find callers via call site index → trace caller's argument
    5. "value" keyword → it's a setter parameter (stop or trace callers)
```

### Handling Common Expression Patterns

```csharp
// Null-coalesce: parse with SyntaxFactory then check BinaryExpressionSyntax
var parsed = SyntaxFactory.ParseExpression(expr);
if (parsed is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } bin)
    // Trace bin.Left and bin.Right

// Parenthesized assignment: "(_field = Path.GetDirName(x))"  
if (parsed is ParenthesizedExpressionSyntax { Expression: AssignmentExpressionSyntax ia })
    // Trace ia.Right

// Method invocation: check if it's a known normalizer
if (parsed is InvocationExpressionSyntax inv)
    // Check method name against safe terminals
```

### Extracting Trailing Identifiers

For `_requestEntry.TaskEnvironment.ProjectDirectory`, extract `ProjectDirectory`:

```csharp
string? ExtractId(string expr) {
    string c = expr.Trim();
    // Strip trailing parens: "Foo()" → "Foo"
    if (c.EndsWith(")")) { /* find matching open paren, strip */ }
    int dot = c.LastIndexOf('.');
    if (dot >= 0) return c[(dot + 1)..];
    if (char.IsLetter(c[0]) || c[0] == '_') return c;
    return null;
}
```

## Advanced: Class Hierarchy and Injection Analysis

When a trace terminates at an abstract property (e.g., `elementLocation.File`), you can extend the analysis with structural checks to answer: "could a malicious subclass inject bad values here?"

### What to Check

1. **Is the class sealed?** If `public abstract` and not `sealed`, external subclassing is possible.
2. **Are concrete subclasses private/internal?** If all built-in implementations are private, external subclasses can only come through public API.
3. **Is the field storing the abstract type private?** If yes, can external code inject a custom instance?
4. **Is the property exposing the field read-only?** No public setter = harder to inject.
5. **Do all assignments go through a factory?** (e.g., `ElementLocation.Create()`) If yes, the factory controls what types are created.
6. **Are there public "link" or "remoting" APIs?** These can bypass normal construction paths.

### Example: Checking for Injection Paths

```csharp
// Check class sealedness
var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
    .First(c => c.Identifier.Text == "ElementLocation");
bool isSealed = classDecl.Modifiers.Any(SyntaxKind.SealedKeyword);
bool isPublic = classDecl.Modifiers.Any(SyntaxKind.PublicKeyword);

// Find all subclasses within the file
var subclasses = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
    .Where(c => c.BaseList?.Types.Any(t => t.ToString().Contains("ElementLocation")) == true);

// Check field visibility
var field = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
    .SelectMany(f => f.Declaration.Variables.Select(v => (f, v)))
    .First(x => x.v.Identifier.Text == "_projectFileLocation");
bool isPrivate = field.f.Modifiers.Any(SyntaxKind.PrivateKeyword);

// Check for public factory/remoting paths
// Look for LinkedObjectsFactory, Link patterns, etc.
```

### Real-World Lesson: The Object Model Remoting Gap

In our `ProjectDirectory` analysis, syntax-only tracing correctly found all assignments safe. But deeper investigation revealed:

1. `ElementLocation` is `public abstract` (not sealed) — subclassable externally
2. `ProjectRootElementLink` is `public abstract` — its `ProjectFileLocation` returns `ElementLocation`
3. `LinkedObjectsFactory` is public — `factory.Create(customLink)` creates a linked `ProjectRootElement`
4. `ProjectInstance(ProjectRootElement xml)` is a public constructor that passes `xml.ProjectFileLocation` directly to `_projectFileLocation`
5. This chains through to `ProjectDirectory` without normalization

**The takeaway**: When a trace stops at an abstract property boundary, check whether there's a public API path to inject a custom implementation. Look especially for "Link", "Remote", "External", or "Factory" patterns in the codebase — these are common bypass vectors.

## Known Limitations of Syntax-Only Analysis

These are expected stopping points — they don't mean the analysis failed:

1. **Abstract/interface property access** (e.g., `elementLocation.File`): Can't resolve through dispatch without semantic model. Use the class hierarchy analysis above to verify safety.

2. **Ternary/conditional with Link checks**: `Link != null ? RootLink.FullPath : _field?.File` — both branches need separate analysis. The `Link` branch often bypasses normal validation paths.

3. **LINQ / lambdas**: `items.Select(x => x.Path)` — trace stops at lambda boundary.

4. **Serialization round-trips**: `translator.Translate(ref _field)` — the field gets deserialized to the same value, but syntax analysis can't prove that.

5. **Named arguments / optional parameters**: Call site index uses positional matching, which fails with named args like `NormalizePath(path: value)`.

6. **Public factory/remoting APIs**: `LinkedObjectsFactory`, link constructors, and similar patterns can create objects that bypass normal construction validation. These are easy to miss if you only trace the "normal" code paths.

**Mitigation**: When the trace stops at a leaf node, add it to a "review list" and verify manually. A hybrid approach (Roslyn traces the obvious paths, human reviews the leaves) is the most practical. For abstract type boundaries, always check the class hierarchy for injection vectors.

## Example: Full Analysis Script Structure

```csharp
// 1. Configuration
string repoRoot = @"Q:\m3";
string[] filesToParse = [ /* relevant files only */ ];

// 2. Parse
var trees = filesToParse.ToDictionary(
    f => f,
    f => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(repoRoot, f)), path: f));

// 3. Build indices (properties, fields, locals, params, call sites)
var propertyIndex = new Dictionary<string, List<(string, int, string)>>();
// ... build all indices in single pass over each tree ...

// 4. Find sinks (e.g., all assignments to .ProjectDirectory)
var sinks = /* find via DescendantNodes().OfType<AssignmentExpressionSyntax>() */;

// 5. Trace each sink
foreach (var sink in sinks) {
    var chain = new List<string>();
    Trace(sink.RhsExpression, sink.File, depth: 1, new HashSet<string>(), chain);
    bool safe = chain.Any(c => c.Contains("✅"));
    Console.WriteLine(safe ? "SAFE" : "NEEDS REVIEW");
}
```

## Tips

- **Keep file list small**: Only include files that participate in the data flow. Parsing the entire repo is unnecessary and slow.
- **Use `visited` set keyed on `"file:expression"`**: Prevents infinite loops through circular property references.
- **Depth limit of 12-15**: Usually enough for MSBuild's call chains. Higher risks hangs if cycles aren't caught.
- **Print the full trace tree**: Even "NEEDS REVIEW" results are valuable — they show exactly where the trace stopped and why.
- **Run with `dotnet run`**: Top-level statements make iteration fast. No need for a proper project structure.
