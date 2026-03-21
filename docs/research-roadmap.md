# Cobalt Research Roadmap

This document outlines the research plan for the Cobalt project — a programming language combining C#/Rust semantics on the .NET runtime. The research is structured in three phases, each building on the previous one.

Prototyping is explicitly out of scope; this roadmap covers only the research and decision-making that must happen first.

---

## Phase 1: Knowledge Base

Comparative research across eight areas. Each topic documents how C# and Rust handle it, identifies common ground, and highlights key differences relevant to Cobalt's design.

### 1.1 Type Systems

- Value types vs reference types; struct semantics in both languages
- Generics: .NET reified generics vs Rust monomorphization; trade-offs and IL implications
- Traits vs interfaces: default implementations, coherence rules, orphan rules
- Enums and algebraic data types: Rust enums with data vs C# enums; discriminated unions
- Nullability: Rust's `Option<T>` vs C# nullable reference types and annotations

### 1.2 Ownership & Borrowing

- Move semantics: Rust's move-by-default vs C# value/reference assignment
- Copy vs Clone: implicit copy for primitives, explicit clone, and C# equivalents
- Borrow rules: shared (`&T`) vs mutable (`&mut T`) references; exclusivity guarantees
- Lifetime annotations: explicit lifetimes, elision rules, and what they enable
- Reborrowing and nested borrows

### 1.3 Memory Management

- GC (generational, concurrent) vs RAII / `Drop` trait
- Deterministic destruction: C# `IDisposable`/`using` vs Rust `Drop`
- Pinning: `Pin<T>` in Rust; relevance for async and self-referential structures
- Interior mutability: `Cell`, `RefCell`, `Mutex` patterns in Rust; C# `ref` and `Span<T>`

### 1.4 Concurrency

- Async/await models: Rust's poll-based futures vs C#'s Task-based async
- Thread safety markers: Rust `Send`/`Sync` traits vs .NET threading model
- Data-race prevention: what Rust guarantees at compile time vs what .NET leaves to the developer
- Channels, actors, and task-based concurrency patterns in both ecosystems

### 1.5 .NET IL Constraints

- What the .NET runtime type system can express (ref structs, `Span<T>`, byref returns)
- Metadata encoding limits: custom attributes, modreqs/modopts
- Value type restrictions and layout control
- Ref structs and stack-only semantics as a partial ownership model

### 1.6 Interop

- C#/.NET interop: P/Invoke, COM, source generators, `SafeHandle`
- Rust FFI: `extern "C"`, `#[repr(C)]`, cbindgen/cxx-style bridges
- Marshalling costs and data layout compatibility
- Feasibility of deeper integration beyond raw FFI

### 1.7 Existing Work & Prior Art

- Midori's ownership and type system experiments
- Vale's region-based borrowing and generational references
- Austral's linear type system
- Mojo's ownership model on MLIR
- Any Rust-on-CLR experiments or proposals
- Roslyn analyzer capabilities and limitations as a static analysis platform

### 1.8 Tooling Landscape

- Roslyn analyzer pipeline: diagnostics, code fixes, incremental analysis (relevant to augmented C#)
- Compiler frontend options: ANTLR, custom recursive-descent parsers, tree-sitter (relevant to new language)
- IL emission: `System.Reflection.Emit`, Mono.Cecil, IKVM.Reflection
- LSP and IDE integration considerations

**Deliverable:** A consolidated reference document (or one document per topic) capturing findings.

---

## Phase 2: Feasibility Study

Takes Phase 1 findings and stress-tests the two candidate approaches against the hardest technical questions. Each item is assessed as **viable**, **viable with constraints**, or **blocker**.

Feasibility flags discovered during Phase 1 research should be captured as they arise, then consolidated here.

### 2.1 Augmented C# Approach

- Can Roslyn analyzers express ownership and borrowing rules with sufficient precision?
- Can lifetime analysis be performed without language-level annotations (inference only)?
- How do existing C# patterns (async, LINQ, lambdas/closures) interact with borrow-checking constraints?
- What escape hatches are needed for interop with unchecked C# libraries?

### 2.2 New Language Approach

- Can ownership and lifetime information be encoded in .NET IL/metadata, or must it be erased at compile time?
- What runtime semantics are lost if lifetimes are compile-time only?
- Can the language consume and expose standard .NET APIs without excessive wrapper boilerplate?
- What is the minimum viable compiler pipeline (parser → analysis → IL emit)?

### 2.3 Cross-Cutting Concerns

- **Rust interop**: Is FFI the only viable path, or can deeper integration be achieved?
- **GC interaction**: Can owned/borrowed values coexist with GC-managed objects cleanly?
- **Performance**: Does borrow-checking on .NET yield tangible benefits (fewer allocations, less GC pressure), or is it primarily a correctness tool in this context?

**Deliverable:** A feasibility assessment document cataloguing findings, blockers, and open risks for each approach.

---

## Phase 3: Decision

Synthesizes Phase 1 and Phase 2 into a clear recommendation.

### 3.1 Evaluation Criteria

- **Technical viability** — Which approach has fewer blockers and open risks?
- **Expressiveness** — Which approach can enforce stronger guarantees?
- **Ecosystem friction** — Which approach integrates more naturally with existing .NET and Rust code?
- **Effort & complexity** — Roslyn analyzers vs building a compiler from scratch
- **Incremental adoption** — Can developers adopt the solution gradually, or is it all-or-nothing?

### 3.2 Document Structure

- Summary of Phase 1 findings: key differences that matter most for Cobalt
- Summary of Phase 2 findings: blockers and constraints per approach
- Side-by-side comparison against the evaluation criteria
- Recommendation with rationale
- Open questions and risks to carry into prototyping

**Deliverable:** A decision document with a clear recommendation and the evidence behind it.
