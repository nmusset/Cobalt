# Phase 3: Approach Decision

This document synthesizes the findings from Phase 1 (comparative research, documents 01-08) and Phase 2 (feasibility assessment, documents 01-03) into a recommendation for Cobalt's implementation approach. The two candidates are:

1. **Augmented C#**: Add borrow-checker-style static analysis on top of standard C# via Roslyn analyzers.
2. **New Language**: Design a new language with C#-like syntax and a built-in borrow checker, compiled to .NET IL.

---

## 1. Summary of Phase 1 Findings

Eight research topics were investigated. The findings below are distilled to the differences that matter most for the decision -- not a full recap, but the evidence that tips the scale.

### Common Ground Cobalt Can Build On

C# and Rust share more infrastructure than their surface differences suggest. These shared foundations are what make Cobalt plausible at all:

**Value types map naturally.** .NET structs are value types with copy-on-assignment semantics, stored inline on the stack or within containing objects. Rust's default model is also value-oriented with stack allocation. A Cobalt type that is stack-allocated and ownership-tracked maps cleanly to a .NET struct. The CLR's reified generics even provide JIT specialization for value-type instantiations, achieving a form of monomorphization comparable to Rust's for the types where it matters most.

**Ref structs provide a partial ownership primitive.** The `IsByRefLike` attribute, `scoped` keyword, and ref-safety rules give .NET a nascent borrow-checking system for stack-confined values. These are runtime-enforced restrictions -- not just compiler conventions. Cobalt can extend this infrastructure rather than building from scratch.

**Custom attributes provide a proven metadata encoding path.** The NullableAttribute precedent demonstrates that compile-time-only type information (not enforced by the runtime) can be encoded in .NET metadata, consumed across assembly boundaries by compilers that understand it, and gracefully ignored by compilers that do not. Ownership annotations, lifetime parameters, and move-semantic flags can follow the same pattern.

**The GC provides a safety floor.** Managed references never dangle. Use-after-free and double-free are impossible for GC-tracked objects. This means Cobalt's borrow checker does not need to solve memory safety (the GC already does) and can focus entirely on higher-value guarantees: data-race freedom, mutation discipline, use-after-dispose prevention, and resource lifecycle enforcement.

### Irreconcilable Differences That Must Be Designed Around

**CIL has no concept of ownership, moves, or lifetimes.** There is no IL instruction for "invalidate this variable after use," no verifier rule for "reject reads of moved locals," and no metadata representation for parametric lifetimes. Every ownership guarantee must be enforced by the Cobalt compiler and erased before IL emission. The runtime provides no safety net for ownership violations -- if the compiler has a bug, or if non-Cobalt code calls into a Cobalt library, the runtime will not catch the violation.

**C# assumes unrestricted aliasing.** Every reference type can be freely aliased and mutated through any reference. The aliasing-XOR-mutability invariant that makes Rust's borrow checker work is antithetical to how .NET APIs are designed. `List<T>`, `DbContext`, `Task<T>`, events, delegates, and the entire BCL assume multiple mutable references as the default. Any ownership system on .NET must either restrict itself to a subset of the type system or create a two-world model (checked and unchecked code).

**Async state machines create implicit aliasing.** The C# compiler transforms async methods into state machine structs that capture local variables across await points. This capture is invisible to analyzers operating on the pre-transformation syntax tree and fundamentally conflicts with lifetime tracking. Ref structs cannot be live across await points even in C# 13. Any borrow-checking system on .NET must either prohibit borrows across await boundaries or accept a significant gap in coverage for async code.

**.NET enums cannot carry data.** Rust's algebraic data types (tagged unions with per-variant data) have no clean CIL representation when variants contain managed references. The CLR's overlapping-reference-field restriction prevents true union layouts for reference-containing variants. Cobalt must choose between struct-based tagged unions (for value-only variants), sealed class hierarchies (for reference-containing variants), or a hybrid approach -- none of which perfectly preserves Rust's value semantics.

**Thread safety is not tracked in the type system.** .NET has no equivalent of `Send` and `Sync`. Any reference can be shared with any thread at any time. Bringing compile-time data-race prevention to .NET requires introducing marker traits that the runtime does not understand and cannot enforce, creating another domain where safety depends entirely on the Cobalt compiler.

---

## 2. Summary of Phase 2 Findings

### Augmented C# Approach: Where It Hits Its Ceiling

The feasibility assessment identifies a ceiling that is structural, not merely a matter of engineering effort:

**Roslyn analyzers cannot enforce guarantees -- they can only advise.** Analyzer diagnostics can be suppressed via `#pragma`, `.editorconfig`, or by disabling the analyzer entirely. Any safety property that depends on the analyzer being active is advisory, not guaranteed. This is fundamentally different from Rust's borrow checker, which is part of the compiler and cannot be bypassed.

**Inter-procedural analysis is infeasible at scale.** Roslyn's flow analysis is intra-procedural by design. The nullable reference type analysis -- the most sophisticated flow analysis in Roslyn -- performs no inter-procedural analysis. Cross-assembly calls are opaque. The augmented approach must therefore be contract-based: trusting annotations on method signatures without verifying them against method bodies in external assemblies. This produces weaker guarantees than a compiler-integrated borrow checker.

**C#'s type system cannot express the required relationships.** Named lifetime parameters (`'a`, `'b`), lifetime subtyping (`'a: 'b`), variance in lifetimes, and higher-ranked trait bounds are inexpressible in C#'s type system or attribute system. Attributes can encode ownership modes (`[Owned]`, `[Borrowed]`) but cannot create parametric relationships between positions in a method signature. This limits the analyzer to simple lifetime patterns (estimated 60-70% coverage by inference), leaving complex reference relationships unchecked.

**Pervasive C# patterns are incompatible with strict borrow-checking.** The feasibility assessment finds that async/await, ORM patterns (Entity Framework), events/delegates, and dependency injection are fundamentally incompatible with aliasing-XOR-mutability enforcement. These patterns collectively dominate modern .NET application code. A strict analyzer would reject idiomatic C# code; a lenient one provides weaker guarantees. The practical scope narrows to: `IDisposable` tracking, single-method aliasing checks, and annotated ownership transfer -- valuable, but well short of full borrow-checking.

**The augmented approach's ceiling is clear.** It can deliver strengthened resource disposal tracking, move-semantic discipline for annotated types, scoped borrowing for ref structs, and advisory thread-safety annotations. It cannot deliver compile-time proof of data-race freedom, full lifetime safety, or guarantees that survive suppression.

### New Language Approach: Where It Faces Its Biggest Risks

The new language approach has no hard blockers, but it concentrates risk in effort and adoption:

**The borrow checker is the highest-complexity novel component.** Rust's borrow checker is one of the most complex components of `rustc`. Cobalt's borrow checker can be simpler (since the GC handles memory safety, lifetimes target logical safety rather than memory safety), but it is still estimated at 10,000-20,000 lines of specialized code requiring deep expertise in dataflow analysis, lifetime inference, and control-flow-sensitive type checking. This is a multi-year engineering effort.

**Ownership guarantees degrade at the C# interop boundary.** C# code consuming Cobalt types operates without ownership enforcement. Value types are freely copyable. Reference types are freely aliasable. Move semantics, lifetime constraints, and exclusive-reference guarantees are invisible to C#. This is analogous to Rust's FFI boundary, but .NET's boundary is broader -- C# consumers expect to use Cobalt types as regular .NET types without special precautions.

**Adoption depends on ecosystem gravity.** A new language must earn its place in the .NET ecosystem. Developers must learn new syntax, tooling must be built (LSP server, debugger extensions, build system integration), and the language must demonstrate clear value over C# to justify the investment. Midori's failure was organizational, not technical -- but it demonstrates that technical superiority alone does not guarantee adoption.

**IDE support requires upfront architectural investment.** The query-based architecture needed for responsive IDE features (completion, diagnostics, hover) must be designed into the compiler from the start. Retrofitting incrementality onto a batch compiler is prohibitively difficult. This front-loads engineering effort before the language is usable for real projects.

### Cross-Cutting: What Is True Regardless of Approach

**Rust interop is FFI-based for the foreseeable future.** Both approaches use the same P/Invoke infrastructure. Tools like csbindgen and Interoptopus automate binding generation. Deeper integration paths (rustc_codegen_clr, WASM Component Model) are experimental and cannot be relied upon. Cobalt's advantage over plain C# is ownership tracking across the FFI boundary -- achievable by both approaches, with the new language having a moderate advantage for dual code generation.

**The GC interaction model is the same.** Value lifetime (deterministic `Drop`/`Dispose`) and memory lifetime (GC reclamation) are decoupled on .NET. This is weaker than Rust but is the strongest achievable without reimplementing the runtime. Both approaches must implement the same explicit ownership boundary (owned values vs. GC-managed values) using the same .NET mechanisms (value types, ref structs, `IDisposable`).

**Performance benefits are real but workload-dependent.** Ownership information enables stack allocation of non-escaping values, copy elision, smaller live-sets, and finalization elimination. Evidence from Midori, .NET 10's JIT escape analysis improvements, and Mojo supports this. The new language approach can act on these optimizations directly (it controls codegen); the augmented approach can only advise.

---

## 3. Side-by-Side Comparison

The five evaluation criteria from the roadmap, assessed against Phase 1 and Phase 2 findings:

| Criterion | Augmented C# | New Language | Verdict |
|---|---|---|---|
| **Technical viability** | Viable with constraints. No hard blockers, but the ceiling is well-defined: advisory diagnostics, no inter-procedural analysis across assemblies, inexpressible lifetime relationships. The approach works within its limits but cannot exceed them. | Viable with constraints. No hard blockers. The borrow checker is the highest-risk component, but Rust's implementation provides a detailed roadmap. CIL encoding via custom attributes is proven. Ownership degrades at the C# boundary (same as Rust at FFI). | **New language.** Both are technically viable, but the augmented approach has a hard ceiling that cannot be raised without becoming a compiler extension (at which point it is effectively the new-language approach). The new language has open design space above it. |
| **Expressiveness** | Limited. Cannot express named lifetimes, lifetime subtyping, lifetime variance, or higher-ranked trait bounds. Ownership mode annotations (`[Owned]`, `[Borrowed]`) work, but parametric lifetime relationships between multiple references are inexpressible. Estimated 60-70% of function signatures covered by inference; the rest are unchecked or require workarounds. | Full. Controls the type system, so it can express named lifetimes, lifetime bounds, conditional trait implementations, `Send`/`Sync` marker traits, and algebraic data types. Can implement NLL-style liveness analysis and Polonius-style origin tracking. The borrow checker is a first-class compiler pass, not a bolted-on analysis. | **New language, decisively.** This is the largest gap between the two approaches. The augmented approach's expressiveness ceiling is set by C#'s type system, which was not designed for ownership semantics. The new language has no such ceiling. |
| **Ecosystem friction** | Minimal for .NET. Code is standard C#; every existing tool, library, NuGet package, and IDE works. No new build system, no new debugging infrastructure. Friction for Rust interop is identical to plain C# (FFI-based). | Moderate for .NET. Cobalt types compile to standard .NET assemblies consumable by C#, but developers must learn new syntax. Requires new tooling (LSP server, build integration). NuGet packages work through standard MSBuild. Friction for Rust interop is slightly lower (dual code generation). | **Augmented C#.** The augmented approach has near-zero ecosystem friction because it is C#. The new language inherits .NET compatibility at the binary level but requires its own tooling ecosystem. |
| **Effort & complexity** | Low to medium. A Roslyn analyzer package is a well-understood deliverable. The implementation is bounded: intra-procedural dataflow analysis, attribute-based contracts, diagnostic reporting. Can be built and shipped incrementally by a small team. | High. A full compiler (parser, type checker, borrow checker, IL emission) with IDE support is a multi-year project. The borrow checker alone is estimated at 10,000-20,000 lines. Requires expertise in compiler construction, type theory, and .NET internals. | **Augmented C#, significantly.** The augmented approach is 5-10x less effort for its initial deliverable. The new language is a larger commitment with a longer time to first usable output. |
| **Incremental adoption** | Excellent. Developers add a NuGet package and opt in per-file or per-project, exactly like NRT. Annotations can be added gradually. Unannotated code is "oblivious" and continues to compile without warnings. | Good, but requires a language switch. Developers write new code in Cobalt and call into existing C# libraries. Cobalt code can be consumed from C# as normal .NET assemblies. But there is a discrete transition: at some point, a developer writes their first `.cobalt` file instead of a `.cs` file. | **Augmented C#.** The augmented approach is the most incrementally adoptable solution possible -- it does not require changing languages. The new language can be adopted gradually at the file level, but the initial adoption step is larger. |

### Analysis

The comparison reveals a clear pattern: the augmented C# approach wins on ecosystem friction, effort, and incremental adoption. The new language approach wins on technical viability (in terms of ceiling) and expressiveness. The question is which set of criteria matters more for Cobalt's goals.

Cobalt's stated goal is to bring Rust-style ownership and borrow-checking guarantees to .NET. The key word is "guarantees." The augmented approach can provide useful warnings and catch real bugs, but it cannot provide guarantees -- its diagnostics are suppressible, its analysis is bounded, and its expressiveness is constrained by C#'s type system. The new language approach can provide guarantees within its own code (degrading to C#-level safety at interop boundaries, exactly as Rust degrades at FFI boundaries).

If the goal were "improve C# code quality with ownership-aware linting," the augmented approach would be sufficient. If the goal is "bring Rust-style guarantees to .NET," the new language is necessary.

---

## 4. Recommendation

**Build the new language. Use the augmented C# approach as a stepping stone, not an alternative.**

The two approaches are not mutually exclusive -- they are sequential. The recommended strategy is:

### Phase A: Augmented C# Analyzer (6-12 months)

Build and ship a Roslyn analyzer package that delivers the augmented approach's high-value use cases:

1. **Strengthened `IDisposable` tracking.** Enforce that owned resources are disposed on all control flow paths. Prevent use-after-dispose. Detect ownership transfer of disposables. This extends existing CA2000/CA2213 with ownership semantics and is the highest-value, lowest-friction application.
2. **Move-semantic discipline.** Prevent use-after-move for types annotated with `[Owned]`. Detect accidental aliasing of single-owner values.
3. **Thread-safety annotations.** Flag when a non-`[Sync]` type is shared across threads. Advisory, not enforced, but catches a class of bugs that .NET provides no compile-time defense against.
4. **Ownership annotation attributes.** Define `[Owned]`, `[Borrowed]`, `[MutBorrowed]`, `[MustDispose]`, `[Scoped]`, and `[NoAlias]`. These attributes become the de facto specification for Cobalt's ownership model -- they force design decisions on ownership semantics before committing to language syntax.

**What this phase delivers:**
- A usable tool that catches real bugs in real C# codebases.
- Validation of Cobalt's ownership model against real-world .NET patterns. The analyzer's limitations will reveal which ownership rules work on .NET and which need adaptation.
- Community feedback and adoption data. If the analyzer is useful, it builds an audience for the full language. If it is not, the project learns early and cheap.
- A set of ownership annotations that directly inform the new language's syntax and semantics.

**What this phase does not deliver:**
- Compile-time guarantees. The analyzer's diagnostics are advisory.
- Full lifetime safety. Parametric lifetimes are inexpressible.
- Data-race freedom. The aliasing-XOR-mutability invariant cannot be enforced.

### Phase B: New Language Compiler (multi-year)

Build the Cobalt compiler, targeting .NET IL, with the following milestones:

**Milestone 1: Core Language (12-18 months from start of Phase B)**
- Hand-written recursive-descent parser for Cobalt syntax.
- Type checker with generics, trait resolution, and type inference.
- Borrow checker with NLL-style liveness analysis, move tracking, and lifetime inference.
- IL emission via Mono.Cecil with ownership metadata encoded as custom attributes.
- Core standard library: primitive types, `Option<T>`, `Result<T, E>`, `Vec<T>`, `String`, `Box<T>`.
- BCL interop: dual-world model (checked Cobalt types, unchecked .NET types). BCL types usable without wrappers.
- Basic error reporting with source locations.

At this milestone, Cobalt is usable for small programs and libraries. The borrow checker enforces ownership within Cobalt code. .NET libraries can be consumed. Cobalt assemblies can be referenced from C#.

**Milestone 2: Ecosystem Integration (6-12 months after Milestone 1)**
- LSP server for IDE support (completion, diagnostics, hover, go-to-definition).
- Query-based incremental analysis architecture.
- Portable PDB generation for debugging.
- MSBuild/SDK integration for project building.
- `Send`/`Sync` marker traits with auto-derivation.
- Async/await support built on .NET's `Task` infrastructure with `Send` bounds on spawned tasks.
- Algebraic data types (discriminated unions) with pattern matching.
- External annotation overlays for the most common BCL types.

At this milestone, Cobalt is usable for real projects. IDE support makes it practical for daily development. Async code is supported with thread-safety guarantees. The language can be evaluated against production workloads.

**Milestone 3: Production Readiness (12+ months after Milestone 2)**
- Rust interop with ownership-tracked FFI wrappers and optional dual code generation.
- Performance optimizations: stack allocation of non-escaping owned values, copy elision, null-on-drop.
- Bootstrapping: Cobalt compiler written in Cobalt.
- Migration path from Cecil to SRM for IL emission.
- Comprehensive error messages with suggestions (modeled on Rust's diagnostics).
- Documentation and tutorial materials.

### Why This Sequencing Works

The analyzer-first strategy avoids Midori's mistake of requiring a "replace the world" commitment. The analyzer validates the ownership model cheaply, builds community, and generates design feedback. The language delivers the guarantees the analyzer cannot. Developers who adopted the analyzer's annotations will find that Cobalt's syntax encodes the same concepts they are already using -- the transition from annotated C# to Cobalt is a refinement, not a revolution.

The analyzer also serves a permanent role even after the language ships: it provides advisory ownership checking for C# consumers of Cobalt libraries. When C# code calls into a Cobalt assembly, the companion analyzer can warn about ownership violations using the same custom attribute metadata that the Cobalt compiler emits. This closes the gap at the interop boundary, making the two-world model more robust.

---

## 5. Open Questions and Risks

### Questions That Could Change the Recommendation

**Can the borrow checker be simplified enough for .NET's context?** Since the GC handles memory safety, Cobalt's lifetimes target logical safety (use-after-dispose, data races, stale references). This is a narrower problem than Rust's. It may be possible to use a simpler lifetime model -- perhaps Vale's region-based approach or Hylo's mutable-value-semantics model -- rather than Rust's full parametric lifetime system. If a significantly simpler model proves sufficient, the engineering effort drops substantially. This should be validated during prototyping.

**How much of the .NET ecosystem can participate in borrow checking?** The dual-world model (checked Cobalt types, unchecked .NET types) means large portions of typical .NET code are unchecked. If the unchecked portion is too large -- if most real programs spend most of their time in unchecked .NET library code -- the borrow checker's guarantees cover too little of the actual execution to be compelling. Prototyping should measure what fraction of a representative .NET application's code can meaningfully be checked.

**Will developers accept a new language?** .NET developers have C#, F#, and VB.NET. Adding a fourth language requires a clear value proposition that justifies learning new syntax, adopting new tooling, and maintaining mixed-language solutions. The analyzer phase will generate data on whether developers value ownership checking enough to adopt it when it is convenient (a NuGet package). Whether they value it enough to switch languages is a separate, harder question.

### Biggest Risks to the Recommended Approach

**Sustained engineering investment.** A multi-year compiler project requires sustained funding, dedicated contributors with deep expertise, and organizational commitment through the long period before the language is production-ready. If investment falters, the project stalls at a partially-complete state that delivers less value than the augmented approach alone.

**Borrow checker complexity.** The borrow checker is novel for .NET. Rust's implementation provides a roadmap, but adapting it to .NET's GC-managed, unrestricted-aliasing environment introduces new interactions that are not fully understood. The borrow checker may prove more complex than estimated, particularly in its interaction with async/await, LINQ, and closure capture patterns.

**Interop boundary friction.** If the ownership boundary between Cobalt and C# is too porous (too many patterns require `unsafe` or unchecked blocks), or if the ergonomics of crossing the boundary are poor (excessive wrapping, verbose type conversions), developers will resist adoption. The boundary design must be smooth enough that calling .NET libraries from Cobalt feels natural, not burdensome.

**Debugging experience.** Standard .NET debuggers will show Cobalt's IL-level representation, which may not map cleanly to Cobalt source concepts. Moved-from variables appearing as still-valid in the watch window, discriminated unions displaying as struct fields with a tag integer, and borrow states being invisible to the debugger will degrade the development experience. Custom debugger tooling may be needed sooner than planned.

### What Should Be Validated During Prototyping

1. **Lifetime model choice.** Build a minimal borrow checker prototype targeting a small subset of Cobalt (functions, structs, borrows, moves -- no generics, no async, no traits). Compare NLL-style parametric lifetimes against a simpler region-based model. Determine which provides sufficient expressiveness for the most common .NET patterns with the least annotation burden.

2. **Async interaction.** Prototype the interaction between borrow checking and .NET's async state machine transformation. Determine the precise restrictions needed for borrows across await points. Test whether `Send`/`Sync` bounds on `Task.Run` are practical and ergonomic.

3. **BCL consumption ergonomics.** Write representative Cobalt programs that use `List<T>`, `Dictionary<TKey, TValue>`, `HttpClient`, `Stream`, and `Task<T>`. Measure how often the dual-world boundary is crossed, how natural the transitions feel, and whether the unchecked code dominates the checked code.

4. **IL emission correctness.** Emit a Cobalt assembly with ownership metadata, consume it from C# and from another Cobalt project. Verify that cross-assembly ownership checking works via custom attributes. Verify that C# consumers see normal .NET types and can use them without friction.

5. **Move semantics on .NET.** Prototype the three enforcement strategies (compile-time-only, runtime clearing, hybrid) for move semantics. Benchmark the runtime clearing cost. Test whether debug-mode clearing catches real bugs in practice.
