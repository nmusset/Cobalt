# Feasibility Assessment: New Language Approach

This document assesses the feasibility of designing a new programming language with C#-like syntax and a built-in borrow checker, compiled to .NET IL. Under this approach, ownership and lifetimes are first-class language concepts, not bolted on after the fact. The analysis is grounded in the Phase 1 research findings (documents 01 through 08).

---

## 1. Can Ownership and Lifetime Information Be Encoded in .NET IL/Metadata?

**Verdict: Viable with constraints**

### Available Metadata Mechanisms

The .NET metadata system provides three mechanisms for encoding information beyond what CIL natively represents: custom attributes, required modifiers (modreqs), and optional modifiers (modopts).

**Custom attributes** are the strongest candidate. They can be attached to assemblies, modules, types, methods, parameters, return values, fields, properties, events, and generic parameters. Their constructor arguments support primitive types, strings, `System.Type`, enums, and one-dimensional arrays of these types. This is sufficient to encode ownership modes (owned, shared-borrow, mutable-borrow), lifetime parameter indices, and lifetime relationships between parameters and return values.

The IL constraints research (Phase 1.5) identifies the following encoding strategy:

- `CobaltOwnershipAttribute` with a byte-array encoding for each type position in a signature (e.g., `0` = unowned/default, `1` = owned, `2` = shared-borrow, `3` = mutable-borrow).
- `CobaltOwnershipContextAttribute` at the type or method level to establish defaults, reducing per-member overhead.
- `CobaltLifetimeAttribute` mapping lifetime parameters to indices and recording relationships between them.
- `CobaltModuleAttribute` at the module level, encoding compiler version and semantic rule version.

**Modreqs and modopts** are not recommended. Modreqs change type identity, meaning a method with a modreq-annotated parameter would have a different signature than the same method without the annotation. This breaks interop -- C# could not call Cobalt methods without understanding the modreq. Modopts do not support parameterization (you cannot encode "lifetime `'a`" as a modopt, only a fixed marker type), and they interact poorly with generics. The Phase 1.5 research concludes that custom attributes have become the preferred encoding for modern C# compile-time concepts, and modreqs/modopts are better suited for binary-level interop markers.

### The NullableAttribute Precedent

C# 8's nullable reference types established the precedent that is directly applicable to Cobalt. The compiler emits `NullableAttribute` and `NullableContextAttribute` to record nullability information that is purely a compile-time concept:

- `NullableAttribute` uses a `byte[]` or single `byte` to encode nullability state for each type position in a signature.
- `NullableContextAttribute` establishes a default at the type or method scope, allowing individual attributes to be omitted when they match the default.
- The runtime does not enforce the annotations. C# consumers with nullable analysis enabled see warnings; pre-nullable consumers see nothing.

This pattern maps directly to ownership and lifetime encoding. Cobalt would emit custom attributes that are:

- Invisible to the runtime (zero enforcement, zero cost).
- Invisible to C# consumers (they see normal .NET types).
- Visible to the Cobalt compiler when consuming Cobalt assemblies (enabling cross-assembly ownership checking).

Additional precedents reinforce this model: `ScopedRefAttribute` marks parameters as `scoped`, `RefSafetyRulesAttribute` records which ref-safety rule version was used during compilation, and `IsReadOnlyAttribute` marks readonly references. All are compile-time concepts encoded in attributes.

### Can Lifetime Parameters Survive for Cross-Assembly Consumption?

Yes, via custom attributes. A Cobalt assembly consumed by another Cobalt project would have its lifetime annotations read from metadata, enabling full lifetime checking across assembly boundaries. The encoding must be compact -- lifetime annotations can be numerous in generic code -- but the NullableAttribute precedent demonstrates that hierarchical defaulting (context attributes reducing per-member overhead) makes this practical.

The critical limitation is that lifetime information is opaque to non-Cobalt consumers. A C# project referencing a Cobalt assembly would see the types without any ownership or lifetime constraints. This is an inherent property of the approach, not a defect -- it mirrors how Rust erases lifetimes before code generation, and how C#'s nullable annotations are advisory rather than enforced.

### Runtime Costs

Custom attributes have zero runtime cost unless explicitly read via reflection. The Cobalt compiler reads them at compile time using `System.Reflection.Metadata`, which provides zero-allocation access to raw attribute blobs. There is a small metadata size increase in the assembly (attribute blobs stored in the `#Blob` heap), but this is negligible -- the NullableAttribute system has been deployed across the entire .NET ecosystem without measurable size concerns.

Generic parameter constraints defined by Cobalt (e.g., `where T : Owned`, `where T : Movable`) would exist only as custom attributes, checked only by the Cobalt compiler. The runtime would not enforce them. Code compiled by a non-Cobalt compiler could violate these constraints, but this is acceptable if Cobalt treats non-Cobalt callers as "unchecked" boundary code.

### Assessment

The metadata encoding approach is proven by NullableAttribute and is sufficient for Cobalt's needs. The constraint is that enforcement is purely compile-time and purely within the Cobalt ecosystem -- other .NET languages will not see or respect the annotations. This is an acceptable trade-off, not a blocker.

---

## 2. What Runtime Semantics Are Lost If Lifetimes Are Compile-Time Only?

**Verdict: Viable with constraints**

### The Erasure Model

Cobalt lifetimes would be erased at IL emission, following the same model as Rust. In Rust, lifetimes are checked by the borrow checker during compilation and do not exist in the generated machine code. In Cobalt, lifetimes would be checked by the Cobalt compiler and would not exist in the generated IL (except as custom attribute metadata for cross-assembly consumption by other Cobalt code).

This is different from the situation in Rust in one important respect: Rust's compiled output is consumed only by the linker and loader, which do not need lifetime information. Cobalt's compiled output (a .NET assembly) is consumed by other compilers and the runtime, which could theoretically use lifetime information but will not, since it is encoded in opaque attributes.

### What Happens When C# Consumes a Cobalt Assembly

When a Cobalt assembly is consumed from C#, the C# compiler sees normal .NET types with custom attributes it does not understand (and ignores). The IL constraints research (Phase 1.5) spells out the consequences:

- **No ownership enforcement.** All Cobalt types are usable as regular .NET types. C# code can freely copy value types, alias reference types, and pass arguments without regard to Cobalt's ownership rules.
- **No lifetime enforcement.** C# code can store references beyond their intended lifetime, pass borrowed values to long-lived contexts, and otherwise violate Cobalt's lifetime constraints.
- **Ref struct restrictions are still enforced.** The runtime enforces `IsByRefLike` restrictions (no boxing, no heap storage, no use as generic type arguments without `allows ref struct`). Any Cobalt types that map to ref structs retain these guarantees even when consumed from C#.
- **Generic constraints (runtime-enforced ones) are still enforced.** Interface constraints, `struct`/`class` constraints, and `unmanaged` constraints are checked by the runtime.

### Can C# Code Violate Cobalt's Ownership Invariants?

Yes, and this is unavoidable. The ownership and borrowing research (Phase 1.2) identifies the specific violations:

- **Value type copying.** A Cobalt value type exposed to C# will be freely copyable. C# has no mechanism to prevent copying a struct. Every assignment is a bitwise copy with both source and destination remaining valid. Move semantics cannot be enforced across the interop boundary.
- **Reference aliasing.** A Cobalt reference type exposed to C# can be freely aliased. The aliasing-XOR-mutability invariant cannot be enforced outside Cobalt code.
- **Lifetime escape.** A reference obtained from a Cobalt API can be stored indefinitely by C# code, even if the Cobalt API intended it to be short-lived.

### Comparison to Rust's Erasure Model

Rust erases lifetimes in compiled code, but the consequences differ because Rust's compiled output is native code called through the C ABI:

- Rust's FFI boundary is explicitly `unsafe`. Any foreign code calling Rust functions accepts responsibility for upholding invariants. Cobalt's .NET boundary is implicitly safe -- C# consumers expect to call Cobalt types without special precautions.
- Rust values that cross FFI boundaries are typically transferred via raw pointers with explicit ownership documentation. Cobalt values that cross the .NET boundary are regular .NET types with no visible ownership semantics.
- Rust's lack of a GC means dangling references are memory-safety bugs. Cobalt runs on the GC, so dangling managed references are not memory-safety bugs -- they are logical-safety bugs (use-after-dispose, data races, stale data).

The last point is significant. Because the GC prevents memory corruption, the consequences of lifetime violations in Cobalt are less severe than in Rust. A violated lifetime in Cobalt produces a logic error (accessing a disposed resource, racing on shared state), not undefined behavior. This means the safety margin for compile-time-only enforcement is larger.

### What Runtime Checks Should Be Inserted as a Safety Net?

The ownership and borrowing research (Phase 1.2) recommends a hybrid approach:

1. **Compile-time enforcement as the primary mechanism.** The Cobalt compiler rejects use-after-move, aliasing violations, and lifetime escapes at compile time.
2. **Debug-mode runtime clearing.** In debug builds, the compiler emits IL that zeroes moved-from variables after a move (value types via `initobj`, reference types via storing `null`). This provides defense-in-depth: moved references produce `NullReferenceException` rather than stale data.
3. **Interop-boundary clearing.** Types crossing the C#/Cobalt boundary always emit clearing, regardless of build mode. This limits the blast radius of C# code violating ownership invariants.
4. **Roslyn-style analyzers for C# consumers.** Cobalt could ship a .NET analyzer that warns C# consumers when they access Cobalt metadata-marked "moved" variables or violate ownership annotations. This is advisory, not enforced, but it catches common mistakes.

The runtime cost of clearing is one write per move. This is acceptable in debug builds and at interop boundaries. For hot paths in release builds, the compile-time-only approach has zero overhead.

### Assessment

Lifetime erasure is a viable model with well-understood trade-offs. The key constraint is that Cobalt's safety guarantees degrade at the boundary with non-Cobalt code. This is analogous to how Rust's safety guarantees degrade at FFI boundaries, and how C#'s nullable analysis degrades when consuming unannotated assemblies. The mitigations (debug clearing, interop clearing, analyzer for C# consumers) reduce the practical risk. The fact that the GC prevents memory corruption makes the consequences of violations less severe than they would be in a native-code language.

---

## 3. Can the Language Consume and Expose Standard .NET APIs Without Excessive Wrapper Boilerplate?

**Verdict: Viable with constraints**

### BCL Types in Cobalt Code

The core challenge is that the entire .NET BCL assumes unrestricted aliasing. `List<T>`, `Dictionary<TKey, TValue>`, `Task<T>`, `string`, `Stream`, and every other BCL type can be freely copied (reference-copied) and mutated through any reference. Cobalt's borrow checker must accommodate this.

The ownership and borrowing research (Phase 1.2) and interop research (Phase 1.6) propose a dual-world model, directly inspired by Mojo's approach to Python objects (Phase 1.7, Prior Art):

- **Cobalt-native types** are ownership-tracked. They have lifetimes, move semantics, and borrow-checking enforcement.
- **.NET interop types** are treated as GC-managed references with no ownership tracking. They behave like C# reference types: freely aliased, freely copied, garbage-collected.

In practice, this means:

```
// Hypothetical Cobalt syntax
let list = List<int>.new();       // .NET type, GC-managed, no borrow checking
list.Add(42);                     // Direct BCL method call, no wrapper
let item = list[0];               // Standard indexer access

let buffer = Buffer.new(1024);    // Cobalt-native type, ownership-tracked
let slice = &buffer[0..512];      // Borrow with lifetime
process(slice);                   // Borrow checker validates this
```

This dual-world model avoids the problem of requiring every .NET API call to be wrapped in an unsafe block. .NET types are implicitly "unchecked" from the borrow checker's perspective, similar to how Rust's `unsafe` code is trusted for memory safety. The Cobalt compiler can still apply standard .NET type safety (null checking, type constraints) to BCL types, but it does not attempt to borrow-check them.

### Would Every .NET API Call Require an Unsafe/Unchecked Block?

No. The recommended approach is **gradual adoption**, similar to NRT's rollout:

1. **.NET types are "unchecked" by default.** Calling a BCL method, constructing a BCL type, or passing BCL types as arguments does not require any special annotation or block. The borrow checker ignores these operations.
2. **Cobalt-native types are "checked" by default.** The borrow checker fully applies to types defined in Cobalt with ownership annotations.
3. **An `unsafe` or `unchecked` block is required only when performing operations that could violate Cobalt's invariants on Cobalt-native types** -- such as converting a Cobalt-owned value to a raw .NET reference, or bypassing the borrow checker for a specific operation.
4. **An `interop` annotation can optionally promote a .NET type to checked status**, allowing the borrow checker to enforce ownership on specific BCL types where the developer wants stronger guarantees.

### Could the Compiler Automatically Infer Ownership Semantics for Well-Known BCL Types?

Partially. The compiler could maintain a built-in knowledge base of common BCL types:

- **Immutable types** (`string`, `DateTime`, `TimeSpan`, `Guid`, `ImmutableList<T>`, `ImmutableDictionary<TKey, TValue>`): could be automatically treated as `Copy` or freely shared, since mutation is impossible.
- **`IDisposable` types** (`Stream`, `DbConnection`, `HttpClient`): could be automatically treated as affine (must be used at most once, must be disposed). The compiler could enforce `using`-like patterns without requiring manual annotation.
- **Thread-safe types** (`ConcurrentDictionary<TKey, TValue>`, `Channel<T>`, `ImmutableArray<T>`): could be automatically marked as `Sync` (safe to share across threads).
- **`Span<T>` and `ReadOnlySpan<T>`**: already have ref-struct restrictions enforced by the runtime. The Cobalt compiler could layer additional lifetime tracking on top of the existing scoped-ref system.

This is feasible for a curated set of well-known types. For the long tail of NuGet packages, the compiler would treat types as unchecked unless annotated.

### NuGet Package Consumption

A Cobalt project should be able to reference and use standard NuGet packages with no additional ceremony. Since Cobalt compiles to standard .NET assemblies and consumes standard .NET assemblies, NuGet package resolution works through MSBuild's existing infrastructure. The Cobalt compiler reads referenced assemblies using `System.Reflection.Metadata`, resolves types and members, and emits calls to them in IL.

Types from NuGet packages would be treated as unchecked .NET types unless the package was compiled by Cobalt (in which case its ownership attributes would be read and enforced) or unless the Cobalt ecosystem provides annotation overlays for popular packages (similar to DefinitelyTyped for TypeScript).

### Exposing Cobalt Types to C#

A Cobalt type compiled to IL would look like a normal .NET type to C# consumers:

- A Cobalt `struct` compiles to a .NET struct.
- A Cobalt `class` compiles to a .NET class.
- A Cobalt `trait` compiles to a .NET interface.
- A Cobalt `enum` (with data) compiles to a struct with a tag field and variant data, or a sealed class hierarchy, depending on the variant content (per the Phase 1.1 research on enum representation strategies).

The custom attributes encoding ownership and lifetime information would be present in the assembly but invisible to C# -- C# would use the types as standard .NET types without any ownership awareness. If the Cobalt project ships a companion Roslyn analyzer, C# consumers could optionally get advisory warnings about ownership violations.

The constraint is that Cobalt types with move-only semantics would still be copyable from C#. A Cobalt value type cannot prevent C# from copying it. Possible mitigations:

- Expose move-only types as reference types (classes) rather than value types, so assignment copies the reference rather than the value. The underlying data is not duplicated, and the Cobalt borrow checker prevents aliased mutation within Cobalt code.
- Accept the limitation and document that move-only types are safe only within Cobalt code, similar to how `unsafe` Rust types are safe only when used according to their documented invariants.

### Assessment

.NET interop is viable with a dual-world model. The constraint is that the borrow checker's guarantees do not extend to .NET types unless the developer explicitly opts in. This is a pragmatic trade-off: attempting to borrow-check the entire BCL would make the language unusable. The gradual adoption model (unchecked by default, checked by annotation) is proven by NRT and is the right approach for ecosystem adoption.

---

## 4. What Is the Minimum Viable Compiler Pipeline?

**Verdict: Viable with constraints**

### Parser

**Recommendation: Hand-written recursive-descent parser.**

The tooling research (Phase 1.8) evaluates ANTLR4, hand-written recursive-descent, Tree-sitter, parser combinators (Sprache, Superpower, Pidgin, Parlot), and PEG parsers. The recommendation for a production compiler is clear:

- **Error messages.** A hand-written parser can produce error messages tailored to each production ("expected `;` after loop initializer" rather than "unexpected token"). This is extremely difficult with generated parsers.
- **Performance.** No abstraction layers between the parser and the language. Recursive-descent parsers are typically the fastest approach for LL grammars.
- **Incremental parsing.** Hand-written parsers can be engineered for incremental re-parsing (tracking which syntax tree nodes are affected by an edit). This is essential for IDE integration and is how rust-analyzer achieves sub-millisecond parse updates.
- **Flexibility.** Context-sensitive parsing decisions (e.g., distinguishing between a type and an expression in generics) can be resolved with arbitrary logic.
- **No external dependencies.** The parser is just code in the implementation language.

The upfront cost is higher -- a parser for a non-trivial language is thousands of lines of code. But every production compiler for a language of Cobalt's complexity (Rust, Go, TypeScript, Swift) uses a hand-written parser, and the investment pays off in error quality, performance, and incremental parsing support.

ANTLR4 is a reasonable choice for prototyping during the initial language design phase, since grammar changes can be made rapidly. But the production compiler should use a hand-written parser.

### Type Checking and Borrow Checking: Shared IR

Yes, type checking and borrow checking can and should share an IR. The recommended architecture, informed by how `rustc` structures its analysis:

1. **Parsing** produces an AST (abstract syntax tree).
2. **Name resolution and type checking** lower the AST to a typed IR (analogous to Rust's HIR/MIR). This IR records resolved types, trait implementations, and method dispatch decisions.
3. **Borrow checking** operates on the typed IR's control-flow graph. It requires type information (to know which types are `Copy`, which are `Send`/`Sync`, which implement `Drop`) and control-flow information (to track variable liveness, reborrowing, and conditional moves).
4. **IL emission** lowers the typed IR to CIL instructions.

The typed IR is the shared representation between type checking and borrow checking. Borrow checking cannot run before type checking (it needs type information), and it must complete before IL emission (it may reject the program). This is the same ordering as in `rustc`: types are checked on HIR, borrow checking runs on MIR, and codegen lowers MIR to LLVM IR.

A critical design decision is whether the borrow checker should operate on a CFG-based IR (like MIR) or on the AST-level typed IR. Rust's experience strongly favors the CFG-based approach:

- NLL (Non-Lexical Lifetimes) requires liveness analysis on the control-flow graph, which is natural on a CFG-based IR but awkward on an AST.
- Two-phase borrows, reborrowing, and conditional moves are all easier to reason about on a CFG.
- Polonius (the next-generation borrow checker) also operates on a CFG-based IR with datalog-style origin tracking.

The recommendation is to lower the AST to a CFG-based typed IR (similar to MIR) and run borrow checking on that representation.

### IL Emission Library

**Recommendation: Mono.Cecil, with a migration path to System.Reflection.Metadata (SRM).**

The tooling research (Phase 1.8) provides a detailed comparison:

| Criterion | Reflection.Emit (Persisted) | Mono.Cecil | SRM (MetadataBuilder) |
|---|---|---|---|
| Ease of use | High | Medium | Low |
| Metadata control | Medium | High | Maximum |
| Debugging (PDB) support | Yes (.NET 9+) | Yes (Mono.Cecil.Pdb) | Yes (native) |
| NativeAOT compatible | No | Yes | Yes |
| External dependency | No (BCL) | Yes (NuGet) | No (BCL) |
| Compiler ecosystem precedent | Rare | Extensive | Growing |

**Mono.Cecil** is the pragmatic choice for the initial compiler:

- Its `ILProcessor` maps naturally to compiler IR-to-IL lowering.
- It can reference types from external assemblies without loading them into the runtime, which is essential for a compiler.
- It has extensive ecosystem use (Fody, ILSpy, ICSharpCode.Decompiler, xUnit instrumentation, Unity's IL2CPP pipeline), meaning bugs are rare and documentation is abundant.
- It supports PDB generation for source-level debugging.
- It is NativeAOT-compatible, which matters if the Cobalt compiler itself is distributed as a native binary.

**SRM** is the long-term strategic choice for zero external dependencies and maximum metadata control, but requires building a custom abstraction layer over `MetadataBuilder` and `InstructionEncoder`. This investment is justified once the compiler is stable and the IL emission layer is well-understood.

**Reflection.Emit** (including `PersistedAssemblyBuilder` in .NET 9+) is acceptable for rapid prototyping but is not recommended for production: it cannot target NativeAOT, has limited metadata control, and lacks ecosystem precedent in compiler projects.

### Estimated Complexity

Building a compiler for a language with C#-like syntax and a built-in borrow checker is a substantial engineering effort. The major components and their estimated complexity:

| Component | Estimated Effort | Notes |
|---|---|---|
| Lexer | Low | Straightforward for a C#-like syntax |
| Parser | Medium-High | C#-like syntax has significant complexity (generics, async, pattern matching). Thousands of lines of code. |
| Name resolution | Medium | Module system, imports, visibility rules, namespace resolution |
| Type checking | High | Generics with constraints, trait resolution, type inference, variance |
| Borrow checking | Very High | This is the novel component. NLL-style liveness analysis, move tracking, reborrowing, two-phase borrows, lifetime inference, and lifetime checking. Rust's borrow checker is one of the most complex components of `rustc`. |
| IL emission | Medium | Straightforward with Cecil/SRM once the IR is well-designed. Custom attribute encoding for ownership metadata. |
| Standard library | Medium-High | Core types, trait definitions, interop facades |
| Error reporting | Medium | Quality error messages are critical for adoption and require sustained investment |

**Comparison to other .NET language compilers:**

- **F#** is implemented in approximately 200,000 lines of F# and compiles to .NET IL. It has type inference, pattern matching, and discriminated unions, but no borrow checker.
- **C# (Roslyn)** is approximately 3 million lines of C#. This is a mature, production-grade compiler with decades of features.
- **Iron (IronPython, IronRuby)** implementations are simpler -- approximately 50,000-100,000 lines -- but target dynamic languages without static type systems.

A minimum viable Cobalt compiler would likely be in the range of 50,000-100,000 lines of C# (or Cobalt, once bootstrapped), comparable to an early-stage F# compiler. The borrow checker alone could account for 10,000-20,000 lines, based on the complexity of `rustc`'s borrow checker scaled for a simpler lifetime model (since the GC handles memory safety and lifetimes are targeting logical safety rather than memory safety).

### Debugging: PDB Generation, Breakpoints, Stepping

Since Cobalt targets .NET IL, the standard .NET debugger infrastructure (vsdbg, the Mono debugger, or LLDB with the SOS extension) works out of the box, provided the compiler emits correct Portable PDB files. The tooling research (Phase 1.8) confirms:

- Both Cecil and SRM support Portable PDB generation with sequence points mapping IL offsets to source locations.
- A dedicated Debug Adapter Protocol (DAP) server for Cobalt is not necessary initially. Standard .NET debugging tools work as long as PDB files are accurate.
- The key requirement is correct sequence point emission: each IL instruction range must map to the corresponding source location (file, line, column).

**Potential debugging friction:**

- **Moved-from variables.** After a value is moved in Cobalt source, the emitted IL still has the original variable in scope with its old value (or a cleared value in debug mode). Debugging tools will show the variable, which may confuse developers. A custom debugger visualizer or VS Code extension could mark moved-from variables as "invalid" in the watch window.
- **Borrow checker concepts.** There is no way to visualize borrow states, lifetime scopes, or ownership graphs in standard .NET debuggers. This would require custom tooling.
- **Discriminated unions.** The IL representation (struct with tag field, or sealed class hierarchy) may not display cleanly as the Cobalt-level enum in the debugger. Custom debugger display attributes (`DebuggerDisplay`, `DebuggerTypeProxy`) can improve this.

### Incremental Compilation and IDE Support

The tooling research (Phase 1.8) is emphatic: **incremental analysis must be designed in from the start.** Retrofitting incrementality onto a batch compiler is far harder than designing for it initially.

The recommended architecture is a **query-based system**, inspired by Salsa (used by rust-analyzer) and Roslyn's red-green trees:

- The compiler is modeled as a set of pure functions (queries) that transform inputs (source files) into derived values (tokens, AST, typed IR, diagnostics, IL).
- Every query result is memoized. When a file changes, the system determines which cached results are still valid and re-executes only the invalidated queries.
- If the result of a re-executed query is unchanged (e.g., only whitespace changed in a file, so the AST is identical), downstream queries are not re-executed ("early cutoff").

This architecture enables:

- **Sub-second response times** for IDE features (completion, hover, go-to-definition, diagnostics) even on large projects.
- **Incremental compilation** where only changed files and their dependents are recompiled.
- **Efficient borrow checking** where a change to one function does not require re-checking the entire project.

The **LSP server** should be built using OmniSharp's `csharp-language-server-protocol` library (a mature C# LSP framework), with the compiler's query-based analysis engine as the backend. This provides IDE support in VS Code, Visual Studio, Neovim, Emacs, and any other LSP-capable editor.

**When this needs to be planned:**

- The query-based architecture should be established before the compiler reaches the "usable for non-trivial programs" stage. It does not need to be the first thing built (a batch compiler is fine for bootstrapping), but the internal API boundaries should be designed to accommodate incrementality from the start.
- Syntax highlighting can be delivered early via a TextMate grammar (for VS Code) and/or a Tree-sitter grammar (for Neovim/Zed), independent of the compiler.
- Semantic IDE features (completion, diagnostics, hover) require the query-based architecture and should be planned for the second major development phase.

### Assessment

The compiler pipeline is feasible with standard, well-understood techniques. The parser, type checker, and IL emission layer use proven approaches with strong tooling support. The borrow checker is the novel and highest-risk component, but Rust's experience provides a detailed roadmap for its architecture. The constraint is effort: this is a multi-year engineering project requiring deep expertise in compiler construction, type theory, and .NET internals. The risk is not technical infeasibility but rather the sustained investment required to reach production quality.

---

## 5. Summary

### Verdict Table

| Question | Verdict | Key Constraint |
|---|---|---|
| **1. Ownership/lifetime encoding in IL metadata** | **Viable with constraints** | Enforcement is compile-time only, within the Cobalt ecosystem. Non-Cobalt consumers see normal .NET types without ownership semantics. Custom attributes following the NullableAttribute precedent provide the encoding mechanism. |
| **2. Runtime semantics lost with compile-time lifetimes** | **Viable with constraints** | C# code can violate Cobalt's ownership invariants when calling into a Cobalt library. Mitigated by debug-mode clearing, interop-boundary clearing, and optional Roslyn analyzers for C# consumers. GC prevents memory corruption, limiting violations to logic errors. |
| **3. .NET API consumption without excessive boilerplate** | **Viable with constraints** | Requires a dual-world model (checked Cobalt types, unchecked .NET types). BCL types usable without wrappers. Move-only Cobalt types exposed to C# lose their move-only semantics. NuGet consumption works through standard MSBuild infrastructure. |
| **4. Minimum viable compiler pipeline** | **Viable with constraints** | Technically feasible with well-understood components (hand-written parser, CFG-based IR, Mono.Cecil for IL emission). The borrow checker is the highest-complexity novel component. Multi-year engineering effort. Incremental compilation must be planned early. |

### Overall Assessment

The New Language approach is **viable with constraints**. No individual question reveals a hard blocker. The constraints fall into two categories:

**Inherent constraints** (fundamental to the approach, cannot be eliminated):

- Ownership and lifetime guarantees do not extend across the Cobalt/C# boundary. C# code consuming Cobalt types operates without ownership enforcement. This is analogous to Rust's FFI boundary and C#'s nullable analysis boundary.
- The borrow checker's value proposition on .NET is different from Rust's. Since the GC handles memory safety, the borrow checker primarily targets data-race freedom, mutation discipline, and resource lifetime tracking (use-after-dispose prevention). This is still highly valuable -- arguably more so for .NET's dominant use case of concurrent server applications -- but it is a different pitch than Rust's "memory safety without GC."

**Engineering constraints** (addressable with sufficient effort):

- The compiler is a multi-year engineering project with a borrow checker as the highest-risk component.
- IDE support requires a query-based architecture designed in from the start.
- Interop ergonomics depend on a well-designed dual-world model and curated knowledge of common BCL types.
- Debugging and tooling will require custom extensions to present Cobalt-specific concepts (moved variables, borrow states, discriminated unions) cleanly.

The prior art strongly supports feasibility. Midori demonstrated that a C#-derived language with ownership and permission types can achieve systems-programming performance on a managed runtime. Mojo demonstrates that ownership semantics can coexist with a garbage-collected object model in a single language. The `rustc_codegen_clr` project proves that Rust's ownership model can be compiled to CIL. None of these projects is identical to Cobalt, but together they establish that each major technical challenge has been solved in at least one context.

The critical success factor is not technical feasibility but sustained engineering investment and a clear adoption strategy that makes the language incrementally useful within existing .NET ecosystems.
