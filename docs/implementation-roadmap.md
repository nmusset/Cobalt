# Cobalt Implementation Roadmap

This document outlines the implementation plan for the Cobalt MVP. It follows the recommendation from the [approach decision](decision/01-approach-decision.md): build a new language targeting .NET IL, using a Roslyn analyzer as a stepping stone to validate the ownership model.

The MVP scope covers Phase A (analyzer) and Phase B Milestone 1 (core language compiler).

---

## Phase A: Roslyn Analyzer — Ownership Stepping Stone

A Roslyn analyzer package that brings ownership-aware static analysis to standard C# code. This phase validates the ownership model against real .NET patterns, builds community, and generates design feedback before committing to a full compiler.

### A.1 Ownership Annotation Attributes ✅

Define a set of custom attributes that express ownership semantics in C#:

- `[Owned]` — marks a parameter, return, or field as taking ownership (caller gives up access)
- `[Borrowed]` — marks a parameter as borrowing (caller retains ownership, callee cannot store)
- `[MutBorrowed]` — marks a parameter as exclusively borrowed for mutation
- `[MustDispose]` — marks a type or return value as requiring disposal by the receiver
- `[Scoped]` — marks a reference as confined to the current scope (cannot escape)
- `[NoAlias]` — marks a parameter or field as having no other aliases

**Deliverable:** `Cobalt.Annotations` NuGet package containing the attribute types.

### A.2 IDisposable Ownership Tracking ✅

Extend existing CA2000/CA2213 analysis with ownership-aware rules:

- Enforce that `[Owned]` disposable values are disposed on all control flow paths
- Detect ownership transfer of disposables (passing an `[Owned]` parameter transfers disposal responsibility)
- Prevent use-after-dispose for `[Owned]` types
- Track ownership through common patterns: factory methods, builder patterns, using declarations

### A.3 Move-Semantic Discipline ✅

- Prevent use-after-move: when an `[Owned]` value is passed to a method taking `[Owned]`, the source variable is considered moved
- Detect accidental aliasing of single-owner values
- Warn on implicit copies of `[Owned]` types (suggest explicit `.Clone()`)

### A.4 Thread-Safety Annotations ✅

- `[Sync]` / `[NotSync]` marker attributes for types
- Warn when a `[NotSync]` type is captured by a lambda passed to `Task.Run` or `Parallel.ForEach`
- Advisory only — cannot enforce at compile time, but catches a class of bugs .NET has no defense against

### A.5 Deliverable

- `Cobalt.Analyzers` NuGet package (analyzer + code fixes)
- Opt-in per-project via NuGet reference
- Documentation with examples of each diagnostic
- Test suite covering all rules

### Key Technical Decisions (Phase A)

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Analysis scope | Intra-procedural + contract-based | Inter-procedural analysis is infeasible in Roslyn (see feasibility study) |
| Severity | Warnings, not errors | Analyzers cannot enforce; align with NRT precedent |
| Attribute design | Separate `Cobalt.Annotations` package | Allows adoption without the analyzer; attributes inform Phase B syntax |

---

## Phase B, Milestone 1: Core Language Compiler

The Cobalt compiler — a new language with C#-like syntax and a built-in borrow checker, compiling to .NET IL.

### B.1 Parser

- Hand-written recursive-descent parser (consistent with Rust, Go, TypeScript approach)
- C#-like syntax with Rust-inspired ownership keywords (`own`, `ref`, `mut`)
- Error recovery for IDE-quality diagnostics from the start
- Concrete syntax tree (CST) preserving whitespace and trivia for formatting tools

### B.2 Type System

- Value types and reference types mapping to .NET structs and classes
- Generics with trait bounds (compiled to .NET reified generics with constraints)
- Traits compiled to .NET interfaces; `impl` blocks compiled to interface implementations
- Algebraic data types (discriminated unions) compiled to sealed class hierarchies
- `Option<T>` and `Result<T, E>` as first-class types (no null)

### B.3 Ownership and Borrow Checker

The core differentiator. Enforces at compile time:

- **Move semantics:** values are moved by default on assignment and function call. Source is invalidated after move.
- **Borrowing:** shared references (`&T`) and exclusive references (`&mut T`). Aliasing XOR mutability enforced.
- **Lifetime inference:** NLL-style liveness analysis. Named lifetimes available for complex cases but elided where possible.
- **Drop:** deterministic destruction at scope exit, compiled to `IDisposable.Dispose()` calls in IL.

Scope of the borrow checker for Milestone 1:
- Intra-function analysis (variables, fields, borrows, moves)
- Cross-function analysis via signature-encoded lifetimes
- No async/await support yet (deferred to Milestone 2)

### B.4 IL Emission

- **Mono.Cecil** for assembly generation (full metadata control, PDB support, no runtime dependency)
- Ownership metadata encoded as custom attributes (following NullableAttribute precedent)
- Move semantics enforced at compile time; optionally cleared at runtime in debug mode
- Portable PDB generation for debugger support (breakpoints, stepping, variable inspection)

### B.5 Core Standard Library

Minimal stdlib for Milestone 1:

| Type | Description | .NET Mapping |
|------|-------------|-------------|
| `Option<T>` | Absence without null | Sealed class hierarchy or struct |
| `Result<T, E>` | Error handling without exceptions | Sealed class hierarchy |
| `Vec<T>` | Growable array | Wrapper over `List<T>` or custom |
| `String` | Owned UTF-8 string | Wrapper or interop with `System.String` |
| `Box<T>` | Heap-allocated owned value | Reference type wrapper |
| `Span<T>` | Borrowed view over contiguous memory | Direct use of `System.Span<T>` |

### B.6 .NET Interop (Dual-World Model)

- **Consuming .NET:** BCL types usable directly in Cobalt code as "unchecked" types. No unsafe block required for standard API calls. The compiler treats .NET types as having no ownership annotations — aliasing is permitted, borrow checking is not applied.
- **Exposing Cobalt:** Cobalt types compile to standard .NET assemblies. C# consumers see normal .NET types. Ownership metadata is encoded as custom attributes (readable by the Phase A analyzer for advisory checking).
- **NuGet:** Cobalt projects reference NuGet packages through standard MSBuild infrastructure.

### B.7 Build and Tooling

- `cobaltc` CLI compiler
- MSBuild SDK for `.cobaltproj` files (enabling `dotnet build`)
- Basic error messages with source locations and fix suggestions

### Key Technical Decisions (Phase B, Milestone 1)

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Parser | Hand-written recursive-descent | Best error recovery, performance, and control (see tooling research) |
| IL emission | Mono.Cecil | Full metadata control, PDB support, no runtime dependency (see tooling research) |
| Lifetime model | NLL-style parametric lifetimes | Start with proven model; simplify later if possible (see open questions in decision doc) |
| .NET interop | Dual-world (checked/unchecked) | Avoids requiring unsafe blocks for BCL calls (see feasibility study) |
| Enum encoding | Sealed class hierarchies | Only viable option for reference-containing variants on .NET (see IL constraints research) |

---

## What Comes After the MVP

These are scoped out of the MVP but documented here for context. See the [decision document](decision/01-approach-decision.md) Section 4 for details.

- **Milestone 2 — Ecosystem Integration:** LSP server, query-based incremental analysis, async/await with Send/Sync bounds, pattern matching, external BCL annotation overlays
- **Milestone 3 — Production Readiness:** Rust FFI with ownership-tracked wrappers, performance optimizations (stack allocation, copy elision), bootstrapping, comprehensive diagnostics

---

## Open Questions to Resolve During Implementation

Carried from the [decision document](decision/01-approach-decision.md) Section 5:

1. **Lifetime model simplification** — can a simpler model (region-based, mutable-value-semantics) replace full parametric lifetimes?
2. **Async interaction** — precise restrictions needed for borrows across await points
3. **BCL consumption ergonomics** — what fraction of typical .NET app code is checked vs unchecked?
4. **Cross-assembly metadata** — do custom attributes survive and work correctly across assembly boundaries?
5. **Move enforcement strategy** — compile-time-only vs runtime clearing vs hybrid (benchmark during prototyping)
