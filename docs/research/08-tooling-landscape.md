# Tooling Landscape Analysis for Cobalt

This document surveys the tooling ecosystem relevant to Cobalt's two candidate approaches: augmenting C# with borrow-checking via Roslyn analyzers, or building a new language that compiles to .NET IL. It covers the compiler pipeline, parser technologies, IL emission libraries, and IDE integration strategies.

---

## 1. Roslyn Analyzer Pipeline (for the Augmented C# Approach)

### How the Roslyn Compilation Pipeline Works

The Roslyn compiler processes source code through four distinct phases:

1. **Parse phase.** Source files are tokenized and parsed into `SyntaxTree` objects. Each `SyntaxTree` is an immutable, hierarchical representation of a single source file. At this stage the compiler understands structure only -- it does not know whether a referenced type exists or whether a method call is valid.

2. **Declaration phase.** Declarations from all syntax trees are analyzed to form named symbols (types, methods, properties, etc.) that populate the symbol tables.

3. **Bind phase.** Identifiers in the code are matched to symbols. Type checking, overload resolution, and semantic validation happen here. The result is an `IOperation`-based intermediate representation that reflects the semantic meaning of each statement.

4. **Emit phase.** All information accumulated by the compiler is emitted as a .NET assembly (IL + metadata).

A `Compilation` object is the central entry point. It holds all syntax trees, assembly references, and compiler options. From a `Compilation`, you can obtain a `SemanticModel` for any contained `SyntaxTree`. The `SemanticModel` exposes the same information IntelliSense uses: resolved types, symbol lookups, constant folding results, data flow analysis, and control flow analysis.

### Analyzer Actions and the Analyzer Driver

Analyzers register *actions* that the analyzer driver invokes at specific points in the compilation pipeline:

| Action Kind | Trigger | Typical Use |
|---|---|---|
| `SyntaxNode` | Each node matching a specified `SyntaxKind` | Pattern-matching on code structure |
| `Symbol` | Each symbol matching a specified `SymbolKind` | Validating naming conventions, type constraints |
| `SemanticModel` | After binding completes for a syntax tree | Whole-file semantic checks |
| `CodeBlock` / `CodeBlockStart`/`End` | Method/accessor bodies | Intra-method flow analysis |
| `OperationAction` | Each `IOperation` node | Semantic-level pattern matching |
| `CompilationStart` / `CompilationEnd` | Start/end of entire compilation | Cross-file state accumulation |
| `SyntaxTree` | Each parsed syntax tree | File-level structural checks |

The driver ensures that analyzers run incrementally in the IDE: when a single file changes, only analyzers relevant to that file are re-run (though `CompilationEnd` analyzers must wait for the full compilation).

### Incremental Analysis and Performance

Roslyn's IDE layer performs incremental analysis, re-analyzing only modified code and its transitive dependents rather than the entire solution. However, analyzer performance is a well-documented challenge:

- **Dataflow analysis is expensive.** The `ControlFlowGraph` and `DataFlowAnalysis` APIs traverse every basic block and branch in a method body. For large methods with complex control flow, this is computationally intensive. The Roslyn team's own guidance is to gate dataflow analysis behind cheaper syntactic/semantic prechecks to limit invocation.
- **Incremental source generator caching pitfalls.** Types like `ImmutableArray<T>` are not structurally equatable across compilations, so naive pipelines break incrementality. Most generator authors must create their own `EquatableArray<T>` wrappers. The lesson for a hypothetical borrow-checker analyzer: intermediate models must be simple, equatable value types to avoid re-running analysis on unchanged code.
- **Per-keystroke execution.** In the IDE, analyzers run on every edit. A borrow-checker that performs whole-method or cross-method analysis on every keystroke would degrade responsiveness noticeably.

### Diagnostic Severity Levels

Roslyn diagnostics have five severity levels, each with different build and IDE behavior:

| Severity | Editor Squiggle | Error List | Breaks Build? |
|---|---|---|---|
| **Error** | Red | Yes | Yes |
| **Warning** | Green | Yes | No (unless `/warnaserror`) |
| **Info** | None | Yes | No |
| **Suggestion** | Gray dots | No | No |
| **Hidden** | None | No | No |

For a borrow-checker analyzer, severity mapping is critical. Ownership violations that represent genuine unsafety should be `Error` to block compilation. Stylistic suggestions (e.g., "consider making this parameter `in`") should be `Suggestion` or `Info`. Users can override severities in `.editorconfig` or `.globalconfig`, and `<TreatWarningsAsErrors>` in the project file promotes all warnings to errors.

### Source Generators vs. Analyzers

Source generators and analyzers are both packaged and deployed the same way (as "Analyzers" in a NuGet package), but they serve fundamentally different purposes:

| Aspect | Analyzer | Source Generator |
|---|---|---|
| **Output** | Diagnostics (errors, warnings, suggestions) | New C# source files added to the compilation |
| **Can modify existing code?** | No (only report diagnostics; code fixes are separate) | No (can only *add* new files) |
| **Access to semantic model?** | Yes, full | Yes, full |
| **Typical use** | Code quality rules, style enforcement, bug detection | Boilerplate generation, serialization code, DI registration |

**Incremental source generators** (introduced in .NET 6 via `IIncrementalGenerator`) replaced the original `ISourceGenerator` interface. They expose a pipeline model where each transformation step caches its output and only re-executes when inputs change. This is essential for IDE performance -- the original generators re-ran on every compilation change.

Source generators cannot replace language features. They cannot alter the syntax or semantics of C#; they can only produce additional C# source that participates in the normal compilation. For Cobalt's augmented approach, source generators could potentially *emit* ownership-tracking helper types or attribute definitions, but the actual borrow-checking logic would live in an analyzer.

### Examples of Complex Roslyn Analyzers

**Nullable reference type flow analysis** is the most instructive precedent. It is built directly into the Roslyn compiler (not as an external analyzer) and performs intra-procedural flow analysis to track null state through each method body. Key characteristics:

- It tracks "flow state" (not-null, maybe-null, etc.) per variable at each program point.
- It handles conditionals, null-coalescing, pattern matching, and null-forgiving operators.
- It is explicitly **not inter-procedural**: calling a method that assigns a field does not update the caller's flow state for that field. The compiler documents that "no inter-procedural analysis will be performed to determine if methods called from constructors definitely assign static or instance variables."
- Special cases exist for `Interlocked.CompareExchange` and other framework methods that defy standard annotation.

**IDisposableAnalyzers** (from the `DotNetAnalyzers` community) track disposable resource lifetimes across assignments, field storage, and return values. They must reason about ownership transfer -- when a disposable is passed to a constructor, does the receiving type take ownership? This is conceptually close to borrow-checking and demonstrates both the potential and the limits of Roslyn-based analysis.

**Infer#** (Microsoft's adaptation of Facebook's Infer for .NET) is the closest existing tool to inter-procedural ownership analysis. It translates .NET bytecode to Infer's SIL intermediate language and then runs bi-abduction analysis to detect null dereferences, resource leaks, and thread-safety violations across method boundaries. Critically, Infer# does **not** run as a Roslyn analyzer -- it operates on compiled assemblies as a separate post-build step. This architectural choice is telling: the Roslyn analyzer framework was not sufficient for the inter-procedural analysis Infer# requires.

**Thread-safety analyzers** (e.g., `Microsoft.VisualStudio.Threading.Analyzers`) detect async/await misuse, blocking calls on the UI thread, and incorrect `lock` usage. These analyzers combine syntactic pattern matching with semantic checks but generally do not perform deep dataflow analysis.

### Could a Full Borrow Checker Be Implemented as a Roslyn Analyzer?

This is the central feasibility question for the augmented C# approach. The analysis must address several dimensions:

**What Roslyn provides that helps:**
- The `ControlFlowGraph` API exposes an `IOperation`-based CFG with basic blocks, conditional branches, and explicit representations of exception regions. This is the right foundation for dataflow analysis.
- The `DataFlowAnalysis` API reports variables read, written, captured, and flowing in/out of a region.
- `PointsToAnalysis` (in the `roslyn-analyzers` dataflow framework) tracks what storage locations a reference might point to, which is conceptually similar to alias analysis needed by a borrow checker.
- The `CompilationStartAction` / `CompilationEndAction` pattern allows building cross-file state, which could accumulate ownership annotations across an entire project.

**What makes it difficult or infeasible:**
- **No inter-procedural analysis framework.** A borrow checker must reason about what a called method does with its arguments (does it store the reference? does it return it? does it alias it?). Roslyn's flow analysis is intra-procedural. You would have to build your own inter-procedural summary system, which is a substantial compiler-engineering effort that would run inside the analyzer driver -- a context not designed for it.
- **No lifetime tracking in the language.** Rust's borrow checker relies on lifetime parameters (`'a`, `'b`) that are part of the type system. C# has no equivalent. An analyzer could use custom attributes (`[Owned]`, `[Borrowed]`, `[Lifetime("a")]`) as annotations, but these are invisible to the compiler's type checker, meaning the analyzer must independently re-derive type relationships that Rust gets from its type system.
- **Performance at scale.** Even nullable flow analysis, which is intra-procedural and built into the compiler with maximum optimization, has measurable compile-time cost. An analyzer performing borrow-checking across all methods in a project, with alias analysis and lifetime resolution, would likely be significantly slower. The per-keystroke IDE execution model makes this worse.
- **Expressiveness limits.** Roslyn analyzers cannot reject code at the type-system level. They can only emit diagnostics. A user who ignores or suppresses warnings gets no safety guarantees. In contrast, Rust's borrow checker is part of the compiler -- you cannot suppress it.
- **Fundamental semantic mismatch.** C# is garbage-collected. Heap references are never dangling (the GC ensures this). The value proposition of borrow-checking in C# is therefore different from Rust: it would enforce *resource* ownership (files, connections, locks) rather than *memory* ownership. This narrows the scope but also means the analysis must understand domain-specific ownership transfer patterns rather than a uniform memory model.

**Verdict:** A Roslyn analyzer could enforce a *subset* of ownership discipline -- tracking `IDisposable` ownership, preventing aliasing of `ref struct` values, flagging use-after-move patterns for annotated types. It cannot replicate the full guarantees of Rust's borrow checker because C# lacks the language-level constructs (lifetimes, move semantics, exclusive mutability) that make those guarantees possible. The analysis would be best-effort: valuable for catching bugs, but not a compile-time proof of memory safety.

### Code Fixes and Refactorings

Roslyn code fix providers can suggest transformations tied to analyzer diagnostics. For a borrow-checking analyzer, code fixes could:

- Add `[Owned]` or `[Borrowed]` attributes to parameters and return types.
- Insert `using` blocks around owned disposable resources.
- Convert a stored reference to a copied value when aliasing is detected.
- Suggest changing a method parameter from `T` to `in T` or `ref readonly T`.

The syntax transformation API operates on immutable syntax trees using the "with" pattern, producing new trees that can be applied as document edits. Code fixes integrate with the IDE's lightbulb menu and can be applied individually or in bulk across a solution.

---

## 2. Compiler Frontend Options (for the New Language Approach)

### ANTLR4

**Architecture.** ANTLR4 uses Adaptive LL(\*) (ALL(\*)) parsing, which pushes grammar analysis to runtime rather than performing it statically at grammar-compile time. The parser dynamically computes lookahead based on the actual input, adapting its prediction strategy as it encounters new input patterns. This is a significant departure from ANTLR3's static LL(\*) analysis.

**Performance.** ALL(\*) guarantees O(n) parsing for non-ambiguous grammars. The parser "warms up" like a JIT compiler -- repeated encounters with the same grammar decision patterns are resolved from a cache, making subsequent parses faster. Benchmarks show ANTLR4 is 5-10x faster than regex-based lexers for structured input.

**Error recovery.** ANTLR4 provides built-in error handling with a pluggable `ErrorStrategy` class. The default strategy attempts single-token insertion, single-token deletion, and sync-and-consume recovery. Custom error strategies can be implemented by extending `DefaultErrorStrategy`. Error recovery is adequate for batch compilation but may not produce the fine-grained partial parse trees needed for IDE integration.

**Language support.** ANTLR4 has runtime targets for Java, C#, Python, JavaScript, TypeScript, Go, C++, Swift, PHP, and Dart. The C# target is mature and well-maintained. Grammars are defined in a separate `.g4` file and compiled to parser/lexer source code.

**Strengths for Cobalt:**
- Rapid grammar prototyping -- changes to `.g4` files are immediately regenerated.
- The C# runtime integrates naturally into a .NET-based compiler.
- Visitor and listener patterns for AST construction are generated automatically.
- Large grammar ecosystem (grammars for C#, Java, SQL, etc. available as references).

**Weaknesses for Cobalt:**
- Generated parser code is a black box, making it harder to customize error messages per-production.
- The parse tree is a concrete syntax tree (CST), not an AST; a separate CST-to-AST transformation pass is required.
- Grammar ambiguities are resolved silently at runtime rather than flagged at grammar-compile time, which can hide language design issues.
- Incremental parsing is not supported; the entire file must be re-parsed on each edit (relevant for IDE integration).

### Custom Recursive-Descent Parsers

**Why production compilers use them.** Rust (`rustc`), Go, GCC (modern C++ frontend), Clang, TypeScript, Swift, and V8's JavaScript parser all use hand-written recursive-descent parsers. The reasons are consistent:

- **Error messages.** A hand-written parser can produce error messages tailored to each production. When parsing a `for` loop, the parser can say "expected `;` after loop initializer" rather than a generic "unexpected token." This is extremely difficult to achieve with generated parsers.
- **Performance.** No abstraction layers between the parser and the language. A recursive-descent parser directly calls functions for each grammar rule, with no interpreter overhead and minimal memory allocation. Recursive-descent parsers are typically the fastest approach for LL grammars.
- **No external dependencies.** The parser is just code in the implementation language. No separate grammar file, no code generation step, no version-mismatch risk with a parser generator runtime.
- **Flexibility.** Context-sensitive parsing decisions (e.g., distinguishing between a type and an expression in generics like `a<b,c>d`) can be resolved with arbitrary logic. Parser generators constrain you to the formalism of the grammar.
- **Incremental parsing.** Hand-written parsers can be engineered for incremental re-parsing by tracking which syntax tree nodes are affected by an edit. This is how rust-analyzer achieves sub-millisecond parse updates.

**Disadvantages:**
- **More code to maintain.** A parser for a non-trivial language is thousands of lines of code. Grammar changes require manual updates to potentially many functions.
- **Left recursion.** Recursive-descent cannot directly handle left-recursive productions; the grammar must be transformed (usually to iterative loops for left-recursive operator precedence).
- **Hidden ambiguities.** A parser generator will warn about shift-reduce or reduce-reduce conflicts. A hand-written parser silently chooses one interpretation, and ambiguities may only be discovered much later through bug reports.
- **No formal grammar validation.** There is no tool that checks whether the hand-written parser actually accepts the intended language.

**Recommendation for Cobalt:** Given that Cobalt aims to produce a production compiler with excellent error messages and eventual IDE integration, a hand-written recursive-descent parser is the strongest choice. The upfront investment in parser code pays off in error quality, performance, and incremental parsing support.

### Tree-sitter

**Design goals.** Tree-sitter is an incremental parsing library designed specifically for programming tools (editors, linters, code browsers), not compilers. It generates parsers from a grammar specification (written in a JavaScript DSL) and produces concrete syntax trees that can be updated incrementally as the source text changes.

**Incremental parsing.** When a source file is edited, Tree-sitter re-parses only the changed region and splices the new subtree into the existing tree. This enables sub-millisecond parse updates even for large files, which is why editors like Zed, Neovim (via nvim-treesitter), Helix, and Atom use it for syntax highlighting and structural navigation.

**Error recovery.** Tree-sitter handles syntax errors gracefully, producing a partial tree where error nodes mark the boundaries of unparsable regions. The rest of the tree remains valid and usable. This is essential for IDE use where the file is frequently in an invalid state.

**Limitations as a compiler parser:**
- **No error messages.** Tree-sitter's error recovery is designed for resilience, not diagnostics. It identifies *where* errors are but does not produce messages about *what* was expected. Extracting the position where an error initiated and getting a list of expected tokens is non-trivial with the current API.
- **CST-to-AST conversion is painful.** Users report that converting Tree-sitter's CST to an idiomatic AST requires traversing the parse tree and building up a separate AST, which "feels like maintaining a second, redundant parser."
- **Not designed as a compiler frontend.** The Tree-sitter maintainers themselves note it is not usable as a compiler parser "unless you can assume that the input is already valid."

**Potential role in Cobalt:** Tree-sitter should not be the compiler's parser, but it could be a valuable *second* parser for IDE integration. A Tree-sitter grammar for Cobalt could power syntax highlighting, code folding, and structural selection in editors, while the compiler's own parser (hand-written recursive-descent) handles compilation with proper error messages. The grammars would need to be kept in sync, which is a maintenance burden.

### Parser Combinator Libraries for .NET

Several .NET libraries enable building parsers compositionally from smaller parsing functions:

**Sprache** is the original .NET parser combinator library. It provides a `Parser<T>` delegate type and LINQ-style composition. It automatically backtracks on failure, which simplifies grammar definition but hurts performance. Sprache is the slowest of the major .NET parser combinator libraries.

**Superpower** (by Nicholas Blumhardt, also the author of Serilog) builds on Sprache's concepts but adds a separate tokenization step, which improves error messages and performance. Superpower is 2-3x faster than Sprache due to reduced backtracking and fewer allocations, but still significantly slower than dedicated parser generators.

**Pidgin** (by Benjamin Hodgson) is designed for performance. It uses a special `Try` combinator to control backtracking explicitly (rather than backtracking by default like Sprache), reducing wasted work. Pidgin is faster and allocates less memory than both Sprache and Superpower.

**Parlot** (by Sebastien Ros) is the newest and fastest entrant. Benchmarks show Parlot's fluent API is ~10x faster than Pidgin, and its raw API offers another ~2x on top of that, for expression parsing workloads. Parlot achieves this through zero-allocation parsing paths and direct cursor manipulation.

**Assessment for Cobalt:** Parser combinators are excellent for prototyping and for parsing small DSLs (configuration files, query languages). For a full programming language compiler, they are generally inferior to hand-written parsers in error message quality and inferior to ANTLR in grammar clarity. Parlot is fast enough for production use, but the lack of incremental parsing support and the difficulty of producing good error messages from combinator chains make them a second-tier choice for Cobalt.

### PEG Parsers

**Parsing Expression Grammars (PEGs)** are an alternative to context-free grammars where the choice operator is ordered (prioritized). The first matching alternative wins, which eliminates ambiguity by definition.

**Advantages:**
- **Unambiguous by construction.** Every input has exactly one parse tree (or no parse tree). This eliminates an entire class of grammar engineering problems.
- **Linear-time parsing.** Packrat parsing (memoized recursive descent over a PEG) runs in O(n) time by caching results at each input position for each grammar rule.
- **Expressive.** PEGs can express some patterns that pure CFGs cannot, such as the "dangling else" resolution and context-sensitive keywords.

**Disadvantages:**
- **Memory consumption.** Packrat parsing memoizes the result of every rule at every position, consuming O(n * |rules|) memory. For large source files and complex grammars, this can be prohibitive.
- **Left recursion.** Standard PEGs do not support left recursion. Extensions exist (e.g., in packrat parsers by Warth et al.) but add complexity.
- **Silent priority bugs.** Because the ordered choice operator is deterministic, a grammar rule that accidentally matches a broader pattern first will silently shadow later alternatives. These bugs are subtle and hard to detect.
- **No ambiguity warnings.** Unlike CFG-based generators that report conflicts, PEG tools cannot warn about shadowed alternatives because shadowing is the intended semantics of ordered choice.

**Assessment for Cobalt:** PEGs are a reasonable choice but carry risks from silent shadowing bugs. They are best suited for simpler languages or situations where the grammar is well-understood and stable. For a language that is still being designed (like Cobalt), the lack of ambiguity warnings is a real drawback during rapid iteration.

---

## 3. IL Emission

### System.Reflection.Emit

**What it provides.** `System.Reflection.Emit` allows runtime generation of .NET assemblies, types, and methods. The API mirrors the structure of an assembly: `AssemblyBuilder` -> `ModuleBuilder` -> `TypeBuilder` -> `MethodBuilder` -> `ILGenerator`. You emit IL instructions by calling methods on `ILGenerator` (e.g., `Emit(OpCodes.Ldarg_0)`, `Emit(OpCodes.Call, methodInfo)`).

**PersistedAssemblyBuilder (.NET 9+).** The `PersistedAssemblyBuilder` class, introduced in .NET 9, provides a fully managed Reflection.Emit implementation that supports saving assemblies to disk. Prior to this, .NET Core's `AssemblyBuilder` could only create transient in-memory assemblies (the `Save` method existed only in .NET Framework). `PersistedAssemblyBuilder` also supports PDB generation via `PortablePdbBuilder`, enabling source-level debugging of emitted assemblies with proper sequence points.

**Limitations:**
- **API-level abstraction.** Reflection.Emit works at the level of .NET types and methods, not at the raw metadata level. This means certain metadata constructs (custom calling conventions, advanced PE layout, module-level methods) are difficult or impossible to represent.
- **No reading/inspection.** Reflection.Emit is write-only. You cannot load an existing assembly and modify it.
- **NativeAOT/trimming restrictions.** `System.Reflection.Emit` is not available in trimmed or NativeAOT-compiled applications. If Cobalt's compiler itself needs to be AOT-compiled (for distribution as a single native binary), Reflection.Emit cannot be used.
- **Type dependency ordering.** Types must be defined before they can be referenced, which complicates mutual references and forward declarations.

### Mono.Cecil

**What it provides.** Cecil is a library for reading, modifying, and writing .NET assemblies at the IL and metadata level. Unlike Reflection.Emit, Cecil does not require loading the assembly into the runtime -- it operates on the binary representation directly. Cecil can:

- Create assemblies from scratch, writing types, methods, fields, IL instructions, and all metadata tables.
- Load existing assemblies and modify them (IL weaving), which is how tools like Fody, PostSharp, and ILSpy work.
- Work without having compatible referenced assemblies loaded (it operates on metadata references, not live types).

**Key architectural concept.** Cecil distinguishes between *references* and *definitions*. A `TypeReference` is a pointer to a type that may be defined elsewhere; a `TypeDefinition` is the full definition. This mirrors the ECMA-335 metadata model and gives Cecil full control over how types are referenced.

**IL emission.** Cecil's `ILProcessor` provides methods to create, insert, append, and replace IL instructions. Instructions reference `MethodReference`, `FieldReference`, etc., rather than live `MethodInfo` objects, which means you can emit code that references assemblies not loaded in the current process.

**Compared to Reflection.Emit:**
- More control over metadata (custom attributes on parameters, explicit layout, P/Invoke details).
- Supports reading and modifying existing assemblies.
- No dependency on the runtime's type loading machinery.
- Slightly more verbose API for common cases.
- Battle-tested in the .NET ecosystem by dozens of tools (Fody, ILSpy, ICSharpCode.Decompiler, xUnit instrumentation, Unity's IL2CPP pipeline).

### IKVM.Reflection

**What it provides.** IKVM.Reflection was written for the IKVM project (which runs Java bytecode on .NET) as a replacement for `System.Reflection.Emit`. Its API closely mirrors `System.Reflection.Emit` -- switching between the two can be done with conditional compilation -- but it writes assemblies to disk without loading them into the runtime.

**Current status.** IKVM.Reflection is largely a legacy component. The IKVM project itself has been exploring migration to `System.Reflection.Metadata.Ecma335` (see below) as the modern replacement. A successor library, `Managed.Reflection`, targets .NET Standard but has limited community adoption.

**Assessment for Cobalt:** IKVM.Reflection is not recommended. Its API mimics Reflection.Emit's design (including its limitations), and the project's direction is toward SRM. If the goal is an Emit-like API without Emit's constraints, `PersistedAssemblyBuilder` (.NET 9+) is the modern answer.

### System.Reflection.Metadata (SRM)

**What it provides.** SRM is the lowest-level .NET metadata API. It reads and writes ECMA-335 metadata tables, blobs, heaps, and IL directly. The `MetadataBuilder` class constructs assemblies by explicitly populating metadata tables row by row: type definitions, method definitions, field definitions, custom attributes, etc.

**Key characteristics:**
- **Maximum control.** Every metadata table and heap is directly accessible. You can produce assemblies with unusual metadata layouts, custom debug information, or non-standard features that no higher-level API supports.
- **Performance.** SRM is designed for high-performance scenarios (it is the foundation of the Roslyn compiler's metadata reading). Reading and writing are allocation-efficient and avoid unnecessary copying.
- **No abstraction layer.** There is no `TypeBuilder` or `ILGenerator`. You manually encode IL instructions using `InstructionEncoder`, manage metadata tokens with `MetadataTokens`, and assemble method bodies with `MethodBodyStreamEncoder`. This is significantly more code than Cecil or Reflection.Emit.
- **Portable PDB support.** SRM natively supports reading and writing Portable PDB files for debugging information.

**Compared to Cecil:**
- Lower-level: more control but more boilerplate.
- Part of the .NET BCL (no external NuGet dependency for the reader; the writer is in `System.Reflection.Metadata`).
- Better performance for metadata-heavy operations.
- No "modify existing assembly" workflow (unlike Cecil, which loads, modifies, and saves).

### Recommendation for Cobalt

| Criterion | Reflection.Emit (Persisted) | Mono.Cecil | SRM (MetadataBuilder) |
|---|---|---|---|
| Ease of use | High | Medium | Low |
| Metadata control | Medium | High | Maximum |
| Debugging (PDB) support | Yes (.NET 9+) | Yes (via Mono.Cecil.Pdb) | Yes (native) |
| Read/modify existing assemblies | No | Yes | Read only |
| NativeAOT compatible | No | Yes | Yes |
| External dependency | No (BCL) | Yes (NuGet) | No (BCL) |
| Ecosystem precedent for compilers | Rare | Extensive | Growing (Roslyn uses SRM for reading) |

**For a new compiler, Mono.Cecil is the pragmatic choice.** It provides the right level of abstraction -- higher than SRM's raw metadata tables, but lower and more flexible than Reflection.Emit. Cecil's `ILProcessor` maps naturally to compiler IR -> IL lowering. The ability to reference types from external assemblies without loading them is essential for a compiler. Cecil's extensive use in the .NET ecosystem means bugs are rare and documentation is abundant.

**SRM is the long-term strategic choice** if you want zero external dependencies and maximum control, and are willing to invest in building your own abstraction layer over `MetadataBuilder` and `InstructionEncoder`. This is the direction the IKVM project is moving, and it aligns with how Roslyn itself works.

**Reflection.Emit (PersistedAssemblyBuilder) is acceptable** for rapid prototyping but is not recommended for a production compiler due to its API limitations, inability to target NativeAOT, and lack of ecosystem precedent in compiler projects.

---

## 4. LSP and IDE Integration

### The Language Server Protocol

LSP standardizes communication between a development tool (editor/IDE) and a language server (a background process that understands a specific programming language). The protocol uses JSON-RPC over stdio or TCP. Key capabilities include:

| Feature Category | Examples |
|---|---|
| **Navigation** | Go to definition, find references, document symbols, workspace symbols |
| **Intelligence** | Completion, signature help, hover information |
| **Diagnostics** | Errors, warnings, and information diagnostics pushed from server to client |
| **Refactoring** | Code actions (quick fixes, refactorings), rename |
| **Formatting** | Document formatting, range formatting, on-type formatting |
| **Semantic tokens** | Token classification for semantic syntax highlighting |

LSP is implemented by approximately 300 language servers and 50 editors as of 2025. Its value proposition is the M*N to M+N reduction: instead of each of M editors implementing support for each of N languages (M*N work), each language implements one server and each editor implements one client.

### Building an LSP Server for a New Language

**Existing frameworks for .NET:**
- **OmniSharp's `csharp-language-server-protocol`** is a C# implementation of the LSP specification. It provides strongly-typed request/response models, handler registration via `MediatR`, and both dynamic and static capability registration. This library can serve as the foundation for Cobalt's language server.
- The library supports the full LSP specification and handles JSON-RPC framing, message dispatch, and capability negotiation.

**Architecture of a language server:**
1. The server maintains an in-memory representation of all open documents and their parse/analysis state.
2. On each edit notification (`textDocument/didChange`), the server incrementally re-parses and re-analyzes the affected file.
3. Diagnostics are pushed asynchronously to the client via `textDocument/publishDiagnostics`.
4. Request handlers for completion, hover, go-to-definition, etc. query the current analysis state.

**Critical requirement: error recovery.** An IDE user's code is in an invalid state most of the time (mid-edit). The parser must produce a useful partial tree even when the code contains errors. This is why both the parser and the semantic analysis must be designed for error tolerance from the beginning. Adding error recovery to a parser that was designed only for valid input is extremely difficult.

### Incremental Analysis for IDE Responsiveness

The gold standard is rust-analyzer, which achieves sub-second response times on large Rust projects through several techniques:

**The Salsa query framework.** Salsa models the compiler as a set of pure functions (queries) that transform inputs into derived values. Every query result is memoized. When an input changes (a file is edited), Salsa's "red-green" algorithm determines which cached query results are still valid:

1. All query results that transitively depend on the changed input are marked "red" (potentially stale).
2. Salsa re-executes red queries. If the result of a re-executed query is the same as the cached result ("early cutoff"), queries that depend on it are marked "green" (still valid) without re-execution.
3. This propagates: even if a file changes, if the parsed AST happens to be unchanged (e.g., only whitespace changed), all downstream semantic queries are cut off immediately.

**Red-green trees (from Roslyn).** Roslyn introduced the concept of red-green syntax trees. The "green" tree is an immutable, position-independent tree of syntax nodes (shared across incremental re-parses). The "red" tree is a position-aware facade over the green tree, constructed lazily. This separation allows sharing unchanged subtrees across edits. rust-analyzer adopted this concept for its syntax representation.

**Implications for Cobalt:** If Cobalt builds a language server, the compiler's analysis pipeline should be designed as a query-based system from the start. Retrofitting incrementality onto a batch compiler is far harder than designing for it initially. This argues for either using or emulating Salsa's architecture in the .NET implementation.

### Debug Adapter Protocol (DAP)

DAP is the debugging counterpart to LSP. It standardizes communication between a development tool and a debug adapter, which bridges the tool to a specific debugger or runtime. DAP features include:

- Launch and attach to processes
- Breakpoints (source, function, conditional, data)
- Step in, step out, step over, continue
- Variable inspection, watch expressions, stack traces
- Exception handling configuration

**For Cobalt:** Since Cobalt targets .NET IL, the generated assemblies can be debugged using the existing .NET debugger infrastructure (vsdbg, the Mono debugger, or LLDB with the SOS extension). The key requirement is emitting correct debugging information (Portable PDB with sequence points mapping IL offsets to source locations). If Cecil or SRM is used for IL emission, PDB generation is well-supported. A dedicated DAP server for Cobalt is not necessary initially -- the standard .NET debugging tools will work as long as PDB files are accurate.

### VS Code Extension Development

A VS Code extension for a new language requires:

1. **Language configuration.** A `language-configuration.json` file declaring comment styles, bracket pairs, auto-closing pairs, and indentation rules.
2. **Syntax highlighting.** A TextMate grammar (`.tmLanguage.json`) that maps token patterns to scope names. TextMate grammars use Oniguruma regular expressions and are structured as a list of pattern rules and repository entries.
3. **Semantic highlighting.** An LSP server can provide `textDocument/semanticTokens` to augment TextMate grammar highlighting with semantic information (e.g., distinguishing between a local variable and a parameter, or between a type name and a namespace).
4. **LSP client.** The `vscode-languageclient` npm package connects the VS Code extension to a language server process.

### Visual Studio Extensibility

Visual Studio supports language extensions through several mechanisms:

- **LSP support** (since VS 2017 15.8) allows reusing the same language server that powers VS Code.
- **MEF (Managed Extensibility Framework)** components extend the VS editor with custom classifiers, adornments, margins, and completion providers. Since VS 2010, the editor is built entirely on MEF.
- **VSIX packages** bundle and distribute extensions. Roslyn analyzers are automatically discovered when included as `Analyzer` assets in a VSIX manifest.

For Cobalt, the primary IDE integration path should be an LSP server, which can be consumed by VS Code, Visual Studio, Neovim, Emacs, and any other LSP-capable editor. Editor-specific features (custom debugger views, project system integration) can be added later through VSIX/VS Code extension APIs.

---

## 5. Implications for Cobalt

### Augmented C# Approach: Feasibility Assessment

A Roslyn analyzer-based borrow checker can achieve a **useful but limited** form of ownership checking:

**What is practical:**
- Tracking ownership of `IDisposable` resources and flagging leaks, double-disposes, and use-after-dispose.
- Enforcing move semantics for types annotated with `[Owned]` attributes -- detecting use-after-move within a single method.
- Flagging aliasing violations for `ref struct` and `Span<T>` values (complementing the compiler's existing scoped/ref-safety rules).
- Suggesting `in`, `ref readonly`, or `scoped` modifiers where they would improve safety.

**What is not practical:**
- Full lifetime tracking across method boundaries (requires inter-procedural analysis that the analyzer framework does not support well).
- Guaranteeing exclusivity of mutable references (C# has no language-level mechanism to enforce this beyond `ref struct`).
- Proving memory safety at compile time (the GC already handles memory safety; the analyzer addresses resource safety, which is a different problem).

**Maximum practical complexity:** The IDisposableAnalyzers and Infer# projects suggest the ceiling. IDisposable analysis tracks ownership flow within and across method calls but relies on conventions and heuristics. Infer# achieves true inter-procedural analysis but only by leaving the Roslyn framework entirely and operating on compiled IL via its own analysis engine. A Roslyn analyzer-based borrow checker would land somewhere between these two: more sophisticated than IDisposableAnalyzers (by performing dataflow-based tracking), but less powerful than Infer# (constrained by the analyzer driver's execution model).

### New Language Approach: Recommended Tooling Stack

| Component | Recommendation | Rationale |
|---|---|---|
| **Parser** | Hand-written recursive-descent | Best error messages, full control, incremental parsing support, proven in production compilers |
| **IL emission** | Mono.Cecil (with migration path to SRM) | Right level of abstraction for a compiler, no runtime loading dependency, PDB support, battle-tested |
| **LSP server** | OmniSharp's `csharp-language-server-protocol` | Mature C# LSP framework, handles protocol details, lets you focus on language logic |
| **IDE syntax highlighting** | TextMate grammar (VS Code) + optional Tree-sitter grammar (Neovim/Zed) | TextMate for VS Code compatibility, Tree-sitter for modern editors |
| **Debugging** | Standard .NET debugger (vsdbg) with Portable PDB | No custom DAP needed; emit correct PDB sequence points from Cecil/SRM |
| **Incremental analysis** | Query-based architecture (Salsa-inspired) | Essential for IDE responsiveness; design for it from the start |

### Could Both Approaches Share Tooling?

Yes, partially. The key shared component would be the **ownership analysis logic** itself:

- Define a common intermediate representation for ownership facts: what is owned, what is borrowed, what is moved, what is returned.
- For the augmented C# approach, populate this IR from Roslyn's `IOperation` trees and `ControlFlowGraph`.
- For the new language approach, populate it from Cobalt's own AST and semantic model.
- The analysis engine (checking borrow rules, detecting use-after-move, validating lifetime constraints) operates on the IR and is shared.

This is similar to how Infer# works: it translates .NET IL to Infer's SIL intermediate language, and then reuses Infer's analysis passes unchanged. Cobalt could define its own "ownership IL" and write one analysis engine that consumes it from either frontend.

**Shared components that could work:**
- Ownership rule definitions and violation categories.
- The core analysis engine operating on an abstract ownership IR.
- Diagnostic message templates and documentation.
- Test cases (ownership patterns that should pass or fail, expressed in both C# and Cobalt).

**Components that cannot be shared:**
- The parser (Roslyn's parser vs. Cobalt's parser).
- Semantic model construction (Roslyn's `SemanticModel` vs. Cobalt's type checker).
- IL emission (Roslyn's emitter vs. Cobalt's Cecil/SRM-based emitter).
- IDE integration plumbing (Roslyn analyzer driver vs. standalone LSP server).

### Build System Integration

**MSBuild custom tasks.** Cobalt's compiler can be invoked as an MSBuild custom task, enabling `dotnet build` integration. Custom tasks implement the `ITask` interface and can be distributed via NuGet packages. The NuGet package includes a `.targets` file that hooks into the build pipeline, typically overriding or extending the `CoreCompile` target.

**dotnet CLI tooling.** A `dotnet tool` (global or local) can wrap the Cobalt compiler for command-line use. For deeper integration, a custom MSBuild SDK (`Sdk="Cobalt.Sdk/1.0.0"`) can replace the default C# SDK, providing Cobalt-specific build logic, default imports, and project file schemas.

**Distribution via NuGet:**
- For the augmented C# approach: the analyzer ships as a standard NuGet package with `analyzers/` and `build/` directories.
- For the new language approach: the compiler ships as a `dotnet tool` package, and the SDK ships as an MSBuild SDK package.

---

## Sources

- [Roslyn Semantic Analysis - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis)
- [Roslyn Overview - GitHub](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Roslyn-Overview.md)
- [Roslyn Analyzer Actions Semantics - GitHub](https://github.com/dotnet/roslyn/blob/main/docs/analyzers/Analyzer%20Actions%20Semantics.md)
- [Roslyn Incremental Generators - GitHub](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md)
- [Roslyn Nullable Reference Types - GitHub](https://github.com/dotnet/roslyn/blob/main/docs/features/nullable-reference-types.md)
- [Writing Dataflow Analysis Based Analyzers - GitHub](https://github.com/dotnet/roslyn-analyzers/blob/main/docs/Writing%20dataflow%20analysis%20based%20analyzers.md)
- [Rust-like Ownership in C# Discussion - GitHub](https://github.com/dotnet/csharplang/discussions/7680)
- [A Comparison of Rust's Borrow Checker to the One in C#](https://em-tg.github.io/csborrow/)
- [Infer#: Interprocedural Memory Safety Analysis for C# - .NET Blog](https://devblogs.microsoft.com/dotnet/infer-interprocedural-memory-safety-analysis-for-c/)
- [IDisposableAnalyzers - GitHub](https://github.com/DotNetAnalyzers/IDisposableAnalyzers)
- [ANTLR4 Adaptive LL(*) Parsing Paper](https://www.antlr.org/papers/allstar-techreport.pdf)
- [Tree-sitter: An Incremental Parsing System - GitHub](https://github.com/tree-sitter/tree-sitter)
- [Tree-sitter as Compiler Parser Discussion - GitHub](https://github.com/tree-sitter/tree-sitter/discussions/831)
- [Pidgin Parser - GitHub](https://github.com/benjamin-hodgson/Pidgin)
- [Parlot Parser - GitHub](https://github.com/sebastienros/parlot)
- [Superpower Parser - GitHub](https://github.com/datalust/superpower)
- [Mono.Cecil Overview](https://www.mono-project.com/docs/tools+libraries/libraries/Mono.Cecil/)
- [PersistedAssemblyBuilder - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-reflection-emit-persistedassemblybuilder)
- [MetadataBuilder - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.ecma335.metadatabuilder)
- [IKVM.NET - GitHub](https://github.com/ikvmnet/ikvm)
- [IKVM Migration to SRM - GitHub Issue](https://github.com/ikvmnet/ikvm/issues/59)
- [Language Server Protocol - Official](https://microsoft.github.io/language-server-protocol/)
- [OmniSharp C# Language Server Protocol - GitHub](https://github.com/OmniSharp/csharp-language-server-protocol)
- [rust-analyzer Architecture](https://rust-analyzer.github.io/book/contributing/architecture.html)
- [Salsa Framework - GitHub](https://github.com/salsa-rs/salsa)
- [Salsa Red-Green Algorithm](https://salsa-rs.github.io/salsa/reference/algorithm.html)
- [Debug Adapter Protocol - Official](https://microsoft.github.io/debug-adapter-protocol/)
- [VS Code Language Server Extension Guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide)
- [VS Code Syntax Highlight Guide](https://code.visualstudio.com/api/language-extensions/syntax-highlight-guide)
- [MSBuild Custom Task Tutorial - Microsoft Learn](https://learn.microsoft.com/en-us/visualstudio/msbuild/tutorial-custom-task-code-generation)
- [Customize Roslyn Analyzer Rules - Microsoft Learn](https://learn.microsoft.com/en-us/visualstudio/code-quality/use-roslyn-analyzers)
- [Roslyn Performance Considerations - GitHub](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Performance-considerations-for-large-solutions.md)
