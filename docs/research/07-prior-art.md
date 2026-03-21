# Prior Art and Related Work

This document surveys existing projects, languages, and tools relevant to Cobalt's goal of combining C#/Rust semantics on the .NET runtime.

---

## 1. Midori

### Background

Midori was a large-scale Microsoft Research incubation project (roughly 2008--2015) that set out to build an entire operating system from scratch in a safe, managed language derived from C#. The language was internally codenamed "M#" (or "System C#") and consisted of C# extended with systems-programming features: ownership annotations, permission types, non-nullable reference types, slices, and explicit error-handling contracts. Aside from a small microkernel, everything in Midori -- drivers, the domain kernel, the networking stack, and all user-mode applications -- was written in this type-safe language and compiled ahead-of-time to native code.

### Ownership, Borrowing, and Permission Types

Midori explored several mechanisms that prefigure modern ownership thinking:

- **Permission types.** Types could be annotated with permissions (`readonly`, `immutable`, `isolated`) that governed aliasing and mutation. An `isolated` reference guaranteed unique ownership of a subgraph of objects, enabling safe transfer between lightweight processes without copying. An `immutable` reference guaranteed deep immutability, allowing safe sharing across concurrent contexts with zero synchronization.
- **Slices.** Midori had a first-class slice type for forming safe, bounds-checked windows over buffers. Joe Duffy described slices as "safe, efficient, and everywhere" in the Midori system. This concept directly influenced the design of `Span<T>` and `ReadOnlySpan<T>` in modern .NET.
- **Non-nullable references.** The default reference type in the Midori language was non-nullable, with explicit opt-in for nullability -- the inverse of standard C#. This concept eventually surfaced in C# 8 as nullable reference types (NRT), though as a weaker, advisory feature rather than the hard guarantee Midori enforced.

### Error Model

Midori introduced a two-pronged error model:

1. **Abandonment** for unrecoverable programming bugs: the entire lightweight process was torn down instantly, with no user-code finalization. Because Midori processes were extremely cheap (lighter than OS threads), this was practical.
2. **Statically-checked exceptions** for recoverable errors: exception types were declared in method signatures and checked by the compiler, similar in spirit to Java's checked exceptions but with deeper type-system integration and better ergonomics. This avoided the problems of both unchecked exceptions (invisible control flow) and return-code boilerplate.

Joe Duffy's blog post "The Error Model" (February 2016) remains one of the most thorough treatments of error-handling design trade-offs in the literature.

### Concurrency Model

Midori was built on **software-isolated processes (SIPs)** -- ultra-lightweight, fine-grained processes communicating through strongly-typed asynchronous message-passing interfaces. There was no synchronous blocking anywhere in the system; all I/O, synchronization, and inter-process communication was asynchronous. Within a single process, safe shared-memory parallelism was enforced through the type system's permission model, making data races impossible by construction.

### Object Capabilities

Rather than ambient-authority access control (as in UNIX or Windows), Midori used a **capability-based security model**. Access to system resources (files, network, hardware) required holding an unforgeable capability object. The type system enforced that capabilities could not be fabricated or escalated, providing defense-in-depth against supply-chain attacks and sandboxing third-party code without runtime overhead.

### Influence on Modern .NET

Although Midori was discontinued around 2015 (primarily due to organizational challenges -- it required replacing the entire Windows ecosystem rather than incrementally improving it), many of its ideas migrated into .NET:

| Midori Feature | .NET Descendant |
|---|---|
| Slice type | `Span<T>`, `ReadOnlySpan<T>` (C# 7.2+) |
| Stack-only types | `ref struct` (C# 7.2+) |
| Lifetime scoping | `scoped` keyword (C# 11) |
| Non-nullable references | Nullable reference types / NRT (C# 8, advisory) |
| AOT compilation | NativeAOT (.NET 7+) |
| Async everywhere | ValueTask, async streams, IAsyncEnumerable |
| Error contracts | Ongoing discussion in csharplang proposals |

### Key Blog Posts by Joe Duffy

- "Blogging about Midori" (Nov 2015) -- series introduction
- "A Tale of Three Safeties" -- type, memory, and concurrency safety
- "Objects as Secure Capabilities" -- capability-based security model
- "Asynchronous Everything" -- async-first OS design
- "Safe Native Code" -- AOT compilation and performance of safe code
- "The Error Model" (Feb 2016) -- comprehensive error-handling analysis
- "15 Years of Concurrency" (Nov 2016) -- evolution of concurrency thinking

### Lessons for Cobalt

Midori is the single most relevant prior-art project for Cobalt. It demonstrated that a C#-derived language with ownership, permissions, and AOT compilation can achieve systems-programming performance while maintaining safety. Its failure was organizational and strategic (competing with Windows), not technical. Cobalt should study Midori's permission-type design closely, particularly the `isolated`/`immutable`/`readonly` permission hierarchy, which maps well to .NET's existing ref-struct and span infrastructure.

---

## 2. Vale

### Overview

Vale is a research programming language designed by Evan Ovadia (verdagon) that explores memory safety without garbage collection and without Rust's annotation-heavy lifetime system. Vale aims to be the "best language for high-performance low-overhead use cases, such as servers, games, simulations, and systems programming."

### Generational References

Vale's core innovation is **generational references** -- a technique for detecting use-after-free errors at runtime with minimal overhead:

- Every heap-allocated object contains a **generation counter** (an integer that increments each time the memory is freed and reused).
- Every pointer stores a **remembered generation** (the generation at the time the pointer was created).
- On dereference, the runtime asserts that the pointer's remembered generation matches the object's current generation. A mismatch indicates a use-after-free, and the program halts deterministically.

This provides temporal memory safety (no use-after-free, no dangling pointers) at the cost of a small runtime check on each dereference -- typically a single integer comparison plus a branch. Vale's benchmarks suggest overhead in the range of 1--5% for typical programs, which is dramatically less than full garbage collection.

### Region-Based Borrowing

Vale's second innovation is a **region borrow checker** that operates at a coarser granularity than Rust's per-reference lifetimes:

- A **region** is a group of objects whose mutability can be controlled collectively. When a function borrows a region immutably, the compiler statically guarantees that no object in that region will be mutated for the duration of the borrow.
- Within an immutable region, generational-reference checks can be **elided entirely** (if nothing can be freed, no generation can change, so all existing pointers are valid). This achieves zero-cost borrowing in practice for read-heavy code paths.
- Regions can be nested, and the programmer can open a new mutable region within an immutable borrow.

This approach avoids the annotation burden of Rust lifetimes (no `'a` parameters, no explicit lifetime bounds) while still enabling the compiler to eliminate runtime checks in hot paths.

### Current Status

Vale is in alpha (version 0.2 as of mid-2024). The region borrow checker has a working proof-of-concept prototype. Development appears to have slowed since 2023, with the most recent blog posts dating from that year. The project remains a solo research effort.

### Lessons for Cobalt

Vale's generational references are directly relevant to Cobalt's "augmented C#" approach. If full static borrow checking proves too restrictive or annotation-heavy for .NET developers, a generational-reference fallback could provide memory safety with minimal code changes. The region concept also maps naturally to .NET's `ref struct` / `Span<T>` scoping -- a region boundary could correspond to a `scoped` lifetime.

---

## 3. Austral

### Overview

Austral is a systems programming language created by Fernando Borretti. Its stated goal is radical simplicity: the entire language specification is small enough to be understood by a single person, and the borrow checker equivalent is under 600 lines of code.

### Linear Type System

Austral uses **linear types** rather than Rust's affine types. The distinction is critical:

| Property | Linear Types (Austral) | Affine Types (Rust) |
|---|---|---|
| Use count | Exactly once | At most once |
| Implicit drop | Forbidden | Allowed (via Drop trait) |
| Forgetting values | Compile error | Allowed (std::mem::forget) |

In Austral, a value of linear type **must** be consumed exactly once -- you cannot silently discard it. This has a profound consequence for resource management: the compiler forces you to explicitly close a file handle, release a lock, or deallocate a buffer. You cannot "forget" to clean up. If Rust's `Drop` trait is "RAII with an escape hatch" (via `mem::forget`), Austral's linearity is "RAII without the escape hatch."

Borretti has written about why this choice was made: affine types are compatible with traditional exception handling (a destructor runs implicitly on unwind), but linear types force the programmer to handle every resource disposition explicitly, which Austral considers a feature. The trade-off is that linear types are incompatible with C++/Java-style exceptions, so Austral uses an explicit error-handling mechanism instead.

### Capability-Based Security

Austral incorporates a capability system inspired by Cap'n Proto and object-capability security:

- Low-level operations (memory allocation, I/O, FFI) require holding a **capability** value of the appropriate type.
- Capabilities are linear, so they cannot be duplicated or forged.
- Third-party library code can be constrained by simply not passing it the capabilities it would need to perform dangerous operations.

This provides supply-chain attack resistance at the language level, without sandboxing or runtime access-control checks.

### Current Status

Austral remains an active project. Borretti discussed the language in a podcast appearance in mid-2025, and the GitHub repository continues to receive updates. The language is bootstrapped (the compiler is written in OCaml) and has a working standard library, but it is not yet used in production.

### Lessons for Cobalt

Austral demonstrates that a linear type system can be implemented with extreme simplicity. For Cobalt, the key question is whether .NET's existing semantics (particularly `IDisposable` and `using` blocks) could be strengthened to provide linear-type guarantees via static analysis. Austral's approach to capabilities also maps well to Cobalt's security goals, though it would require restricting .NET's ambient-authority APIs (which is a significant ecosystem challenge).

---

## 4. Mojo

### Overview

Mojo is a programming language developed by Modular (led by Chris Lattner, creator of LLVM and Swift) that aims to be a "superset of Python" with systems-programming performance. It is built on MLIR (Multi-Level Intermediate Representation) and brings ownership semantics to a Python-like syntax.

### Ownership Model

Mojo's ownership model borrows directly from Rust but adapts it for a language with Python-compatible semantics:

- Every value has **exactly one owner** at a time. When the owner's lifetime ends, the value is destroyed.
- Functions declare how they receive arguments using **argument conventions**:
  - `borrowed` (default): immutable reference, no ownership transfer
  - `inout`: mutable reference
  - `owned`: ownership is transferred to the callee
- The `^` **transfer operator** explicitly moves ownership of a value. After `x^`, the variable `x` is invalidated and any subsequent use is a compile error.

### Register-Passable Types and Value Semantics

Mojo distinguishes between types based on their runtime representation:

- **`@register_passable` types** are passed in machine registers rather than by pointer. The `@register_passable("trivial")` decorator further indicates that the type is memcpy-safe and has no destructor (analogous to C#'s `unmanaged` constraint).
- Mojo defaults to **value semantics**: assignment copies, and each copy is independent. This is the same default as C# structs but extended to all types.

### Interaction with Python's GC

Mojo must bridge between its owned/moved values and Python's reference-counted objects:

- `PythonObject` is register-passable and wraps a Python object reference. It participates in Python's reference counting, meaning Mojo code can hold Python objects and interoperate with CPython's GC.
- Within pure Mojo code, there is no GC -- the compiler inserts deterministic destruction at compile time based on ownership analysis.

This dual-world model (owned Mojo values + GC'd Python objects) is directly analogous to what Cobalt would face: owned Cobalt values + GC'd .NET objects.

### MLIR Foundation

Every Mojo construct is lowered to MLIR, which allows the compiler to track types, shapes, sizes, lifetimes, and ownership at multiple levels of abstraction. This enables aggressive optimization while maintaining safety. MLIR's progressive lowering model means Mojo can represent high-level ownership semantics alongside low-level register allocation in the same IR.

### Lessons for Cobalt

Mojo's most relevant contribution to Cobalt is its demonstration that ownership semantics can coexist with a garbage-collected object model in a single language. The `borrowed`/`inout`/`owned` argument convention system is clean and learnable. The `^` transfer operator provides explicit, visible ownership transfer without Rust's implicit move semantics. However, Mojo targets MLIR (a compiler IR), whereas Cobalt targets .NET IL (a runtime bytecode) -- this constrains what ownership information can be preserved past compilation.

---

## 5. Other Relevant Projects

### 5.1 Rust on CLR: rustc_codegen_clr

The most significant experiment in running Rust on .NET is **rustc_codegen_clr** by FractalFir (Micha). This is a Rust compiler backend that replaces LLVM's native code generation with CIL (Common Intermediate Language) emission:

- Rust MIR (Mid-level IR) is translated into .NET CIL and packaged into .NET assemblies.
- As of the Rust GSoC 2024 work, the backend compiles and passes **95% of the Rust `core` and `std` test suites**.
- .NET classes can be defined in pure Rust, and the long-term goal is near-seamless Rust/C# interop.
- Currently tested only on Linux x86_64 with CoreCLR (.NET 8). Mono support is partial.

This project demonstrates that Rust's ownership model can be **compiled to** CIL, but it does not prove that CIL can **enforce** ownership at the IL level -- the safety guarantees come from the Rust compiler frontend, not from the .NET runtime. This distinction is critical for Cobalt: if Cobalt compiles to standard CIL, the ownership guarantees exist only in the Cobalt compiler, not in the runtime.

### 5.2 Verona (Microsoft Research)

Project Verona is a research language exploring **concurrent ownership** through regions:

- All objects are organized into a **forest of isolated regions**. Each region is a group of objects with a single owning reference. When the owning reference is dropped, the entire region is collected.
- For concurrent access, Verona introduces **cowns** (concurrent owners) -- lightweight, isolated units of state that encapsulate mutable data. Multiple behaviours (Verona's unit of concurrent execution) can atomically acquire a set of cowns, guaranteeing exclusive access without locks.
- The concurrency model is called **Behaviour-Oriented Concurrency (BoC)**, inspired by the actor model and join calculus. It guarantees data-race freedom and deadlock freedom by construction.
- Recent work (PLDI 2025) extends the model with **dynamic region ownership**, including applications to Python concurrency safety (the "Pyrona" project).

Verona's region model is the most sophisticated treatment of ownership-based concurrency in current research. Its region concept is coarser-grained than Rust's per-reference lifetimes but finer-grained than Midori's process-level isolation.

### 5.3 Koka

Koka is a research language (originating from Microsoft Research, led by Daan Leijen) focused on **algebraic effect handlers** with a novel memory management strategy:

- **Effect system.** Koka tracks every side effect (I/O, state, exceptions, async, nondeterminism) in the type of every function. Pure and effectful computations are statically distinguished. Effect handlers allow defining custom control abstractions (async/await, coroutines, probabilistic programming) as libraries rather than language primitives.
- **Perceus reference counting.** Koka uses compile-time-optimized reference counting instead of tracing GC. Perceus performs extensive static analysis to eliminate redundant reference-count operations, achieving "garbage-free" execution where only live references are retained. Further optimizations include **reuse analysis** (detecting when a data structure is about to be freed and reusing its memory for a new allocation of the same shape, enabling guaranteed in-place updates for functional code).

Koka's effect system is relevant to Cobalt if Cobalt wants to track resource effects (e.g., "this function allocates," "this function performs I/O") at the type level. Perceus demonstrates that reference counting, with sufficient compiler optimization, can approach the performance of manual memory management.

### 5.4 Hylo (formerly Val)

Hylo is a programming language designed by Dave Abrahams (of C++ STL fame) and Dimitri Racordon, built on the principle of **mutable value semantics**:

- **All types are value types.** There are no reference types in the language. Every instance behaves like an integer -- assignment copies, and copies are independent.
- **Borrowing without references.** Hylo achieves memory safety and data-race freedom without reference counting, tracing GC, or Rust-style lifetime annotations. Instead, the compiler enforces exclusivity of access through a law of exclusivity: if a value is being mutated, no other access to that value (or any part of it) is permitted.
- **Subscripts and projections.** Rather than returning references, Hylo functions can yield projected access to sub-values, which the compiler can verify for exclusivity.

Hylo was presented at ECOOP 2025, and the team has moved past the "experimental" phase, with a working compiler and standard library, though it is not yet production-ready.

Hylo's mutable-value-semantics approach is appealing for Cobalt because .NET structs are already value types, and Hylo demonstrates that value semantics alone (without explicit lifetimes or ownership annotations) can provide safety. The challenge is that .NET also has reference types (classes), and Cobalt would need to handle both.

### 5.5 Cone

Cone is a language designed by Jonathan Goodwin that offers **region-based memory management** with per-object strategy selection:

- Each heap-allocated object specifies a **region** (a library-provided memory management strategy) at allocation time.
- Available strategies include single-owner (`so`), reference counting (`rc`), tracing GC (`gc`), arena allocation, and object pools.
- All strategies produce **borrowed references** that are type-compatible, so code that reads or mutates objects is generic over the memory management strategy.

Cone's "memory management menu" is relevant to Cobalt because it shows how a language can offer multiple memory management strategies while maintaining a uniform reference type. Cobalt could potentially let developers choose between GC-managed references (for .NET interop) and owned/borrowed references (for performance-critical code) within the same program.

### 5.6 Lobster

Lobster is a game-programming language created by Wouter van Oortmerssen (also known for FlatBuffers and work on the Bflex parser generator):

- Lobster uses a **compile-time ownership analysis** to convert runtime reference counting into static destruction in most cases. The analysis identifies a single "owning" pointer for each object and inserts compile-time deallocation at the point where the owner goes out of scope. Other pointers are classified as "borrows" that do not affect the reference count.
- The ownership analysis is **fully automatic** -- the programmer writes no annotations. The compiler falls back to runtime reference counting only when it cannot statically determine ownership.
- Lobster compiles to C++ and has a JIT mode for development.

Lobster's automatic ownership inference is relevant to Cobalt's "augmented C#" approach: if ownership can be inferred without programmer annotation in many cases, the annotation burden of a borrow checker can be reduced significantly.

---

## 6. Roslyn Analyzer Capabilities

This section evaluates whether Cobalt's "augmented C#" approach -- adding borrow-checker-style static analysis on top of standard C# via Roslyn analyzers -- is technically feasible.

### What Roslyn Analyzers Can Do

Roslyn analyzers operate on the compiler's semantic model and can perform:

- **Syntax analysis.** Inspect the syntax tree for patterns (e.g., flagging `using` without `IDisposable`).
- **Semantic analysis.** Resolve types, symbols, and member accesses. Determine whether a variable is a ref, a `Span<T>`, etc.
- **Data flow analysis.** Track how values flow through a method: which variables are read, written, assigned, or captured. The `DataFlowAnalysis` API can determine whether a variable "flows in" or "flows out" of a region.
- **Control flow analysis.** Construct a control flow graph (CFG) of a method body. Determine reachability, identify all exit points, and reason about branching.
- **IOperation-based analysis.** The `IOperation` tree provides a language-agnostic, lowered representation of operations. A `DataFlowOperationVisitor` framework supports writing custom abstract-interpretation-style dataflow analyses over the CFG.

### Inter-Procedural Analysis

Roslyn's dataflow framework supports **context-sensitive inter-procedural analysis** for methods within the same compilation (same project/assembly):

- Each callee is analyzed with context from the call site.
- The default analysis depth is configurable but bounded for performance.
- Cross-assembly calls are **not** analyzed inter-procedurally. The analysis treats external method calls as black boxes.

This is a significant limitation for borrow checking: if a method in a referenced NuGet package takes a `ref` parameter, the analyzer cannot determine whether the callee stores that reference, aliases it, or lets it escape. The analyzer must rely on method-level contracts (attributes, annotations) rather than whole-program analysis.

### Existing Ownership-Adjacent Analyzers

There are no production Roslyn analyzers that implement full ownership or borrow checking. However, several existing analyzers perform related analysis:

- **CA2000 (Dispose objects before losing scope)**: detects `IDisposable` objects that are not disposed before going out of scope. This is a simplified form of "linear type" checking -- ensuring a resource is consumed (disposed) exactly once.
- **CA1001 (Types that own disposable fields should be disposable)**: ensures that owning types propagate disposal obligations.
- **IDE0058/IDE0059 (Unused value/variable)**: detects values that are assigned but never read -- related to affine-type "use at most once" reasoning.
- **Ref safety rules**: the C# compiler itself (not an analyzer) enforces `ref struct` lifetime rules, the `scoped` keyword, and ref-safe-context analysis. These are effectively a limited borrow checker for stack-allocated references.

### C#'s Existing "Borrow Checker"

C# already has a nascent borrow-checking system built into the compiler (not as an analyzer, but as core language rules). A detailed comparison by em-tg shows:

- C# has two implicit lifetimes per function: **caller-context** (the reference can escape) and **function-member** (the reference is scoped to the method). The `scoped` keyword selects function-member lifetime.
- C#'s "ref safe context" is analogous to Rust's lifetime -- it defines the region where a reference is valid.
- C# does not need Rust's "shared XOR mutable" rule because C# mutation cannot invalidate a `ref` the way Rust mutation can (e.g., by reallocating a `Vec`). However, C# lacks Rust's guarantees about data races and aliased mutation.
- C# cannot express that two references have *different* lifetimes (no named lifetime parameters), which limits the expressiveness of its borrow checking.

### Limitations for a Full Borrow Checker

Implementing a Rust-equivalent borrow checker as a Roslyn analyzer faces several fundamental challenges:

1. **No cross-assembly analysis.** The analyzer cannot see into external method bodies, so it must trust annotations. This means creating and maintaining a set of ownership-contract attributes.
2. **No named lifetimes.** C#'s type system has no way to express relationships between lifetimes of multiple references. An analyzer could track these internally, but cannot express them in method signatures without language extensions.
3. **GC interaction.** .NET's GC can move objects and invalidate raw pointers at any time. A borrow checker for C# must account for the fact that `ref` and `Span<T>` are "managed pointers" that are GC-aware, while `unsafe` pointers are not.
4. **Performance.** Complex dataflow analysis (especially inter-procedural) can be slow. Roslyn analyzers run in the IDE on every keystroke and during compilation. An ownership analysis that slows the IDE noticeably will not be adopted.
5. **Escape hatches.** C# has `unsafe` blocks, `Unsafe.As`, pointer arithmetic, and interop marshaling. A borrow checker must either forbid these or treat them as trust boundaries.
6. **Runtime vs. compile-time guarantees.** Even if an analyzer detects a violation, it produces a diagnostic (warning or error). Unless the analyzer is integrated at the compiler level (like the existing ref-safety rules), violations can be suppressed.

### Feasibility Assessment

A Roslyn analyzer can implement a **useful subset** of borrow checking for the "augmented C#" approach:

- Tracking ownership of `IDisposable` resources (strengthening CA2000 to a linear-type-like discipline).
- Enforcing that `Span<T>` and `ref struct` values are not captured in closures or stored in fields (extending the existing ref-safety rules).
- Detecting common aliasing violations within a single method or compilation unit.
- Providing "ownership contracts" via attributes (`[Owned]`, `[Borrowed]`, `[Scoped]`) that the analyzer verifies.

A Roslyn analyzer **cannot** implement a full, Rust-equivalent borrow checker because the C# type system lacks the expressiveness (no named lifetimes, no exclusive-reference guarantees, no trait-based ownership contracts). For a complete solution, Cobalt would need to either extend the C# language (new syntax + compiler support) or design a new language that compiles to .NET IL.

---

## 7. Lessons for Cobalt

### Design Patterns to Adopt

1. **Midori's permission hierarchy (`isolated`/`immutable`/`readonly`).** This is the most directly applicable model for .NET. It maps onto existing .NET concepts (`ref readonly`, `ReadOnlySpan<T>`, the `scoped` keyword) and can be extended with ownership annotations. Midori proved this works at OS scale in a C#-derived language.

2. **Mojo's argument conventions (`borrowed`/`inout`/`owned`).** These are intuitive, explicit, and avoid Rust's implicit move semantics. They would work well in a C#-like syntax:
   ```
   fn process(borrowed data: List<int>, inout counter: int, owned buffer: Buffer) { ... }
   ```

3. **Vale's generational references as a safety net.** For cases where full static borrow checking is too restrictive (e.g., complex graph data structures), Vale's generational-reference technique can provide memory safety with minimal runtime cost. This could serve as Cobalt's "escape hatch" -- safer than `unsafe` blocks, cheaper than GC.

4. **Verona's region-based concurrent ownership.** If Cobalt wants to provide data-race freedom (which .NET does not guarantee), Verona's region + cown model is the state of the art. Regions map naturally to .NET's `ref struct` scoping, and cowns could be implemented as monitor-like wrappers.

5. **Austral's capability system.** Fine-grained capabilities for dangerous operations (FFI, unsafe memory access, I/O) provide defense-in-depth. In a .NET context, this could restrict access to `System.Runtime.InteropServices`, `unsafe` blocks, and raw pointer APIs.

6. **Koka's effect tracking.** Tracking whether a function allocates, performs I/O, or captures references in its type signature would give Cobalt's type system additional power for reasoning about resource usage.

### Mistakes and Dead Ends to Avoid

1. **Midori's "replace the world" strategy.** Midori failed organizationally because it required replacing the entire Windows ecosystem. Cobalt must be incrementally adoptable within existing .NET codebases. The language should interop seamlessly with standard C# libraries without requiring those libraries to be rewritten.

2. **Excessive annotation burden.** Rust's lifetime annotations are the #1 barrier to adoption for developers coming from GC'd languages. Cobalt should follow Vale's and Hylo's lead in minimizing explicit annotations through inference and region-level (rather than reference-level) borrow checking.

3. **Ignoring the GC.** .NET has a GC, and fighting it is counterproductive. Cobalt should embrace GC for heap-allocated class instances (maintaining .NET interop) and use ownership/borrowing for value types, stack allocation, and performance-critical paths. Mojo's dual-world model (owned Mojo values + GC'd Python objects) is the right template.

4. **Treating the analyzer approach as sufficient.** The Roslyn analyzer path ("augmented C#") can provide useful safety improvements, but it cannot achieve full borrow-checker guarantees due to fundamental limitations of the C# type system. Cobalt should pursue the analyzer approach as a pragmatic stepping stone, while designing the full language for the "new language" approach.

5. **Linear types without exception handling.** Austral's choice of linear types over affine types is intellectually clean but incompatible with .NET's exception model. .NET pervasively uses exceptions, and any Cobalt design must accommodate them. Affine types (use at most once, with implicit drop on exception unwind) are a better fit for .NET.

### Approaches That Map Best to .NET's Runtime Model

1. **`ref struct` + `scoped` as the foundation for borrowing.** C# already has a limited borrow checker for ref structs. Cobalt can extend this with named lifetimes and exclusivity guarantees, building on infrastructure the runtime already supports.

2. **`IDisposable` + `using` as the foundation for linear/affine resource management.** Strengthening `using` blocks to enforce that disposable resources are always disposed (and never used after disposal) is a natural extension of existing .NET patterns.

3. **Value types for ownership.** .NET structs are already value types with copy semantics. Adding move semantics (like Mojo's `^` transfer operator) to structs would enable ownership transfer without fighting the GC.

4. **`Span<T>` and `Memory<T>` for safe buffer access.** These types already provide bounds-checked, lifetime-scoped access to contiguous memory. Cobalt can build on them rather than inventing a new slice type.

5. **CIL as the compilation target, with safety enforced by the Cobalt compiler.** The rustc_codegen_clr project proves that ownership-safe code can compile to standard CIL. The safety guarantees live in the source language and compiler, not in the runtime -- just as Rust's guarantees live in `rustc`, not in the CPU. Cobalt should follow the same model: compile to standard CIL for runtime compatibility, but enforce ownership rules at compile time.

---

## Sources

### Midori
- [Joe Duffy - Blogging about Midori](https://joeduffyblog.com/2015/11/03/blogging-about-midori/)
- [Joe Duffy - A Tale of Three Safeties](https://joeduffyblog.com/2015/11/03/a-tale-of-three-safeties/)
- [Joe Duffy - The Error Model](https://joeduffyblog.com/2016/02/07/the-error-model/)
- [Joe Duffy - Objects as Secure Capabilities](https://joeduffyblog.com/2015/11/10/objects-as-secure-capabilities/)
- [Joe Duffy - Safe Native Code](https://joeduffyblog.com/2015/12/19/safe-native-code/)
- [Joe Duffy - Asynchronous Everything](https://joeduffyblog.com/2015/11/19/asynchronous-everything/)
- [Joe Duffy - 15 Years of Concurrency](https://joeduffyblog.com/2016/11/30/15-years-of-concurrency/)
- [Safe Systems Programming in C# and .NET (InfoQ)](https://www.infoq.com/presentations/csharp-systems-programming/)
- [Midori Concepts Materialize in .NET (SD Times)](https://sdtimes.com/microsoft/midori-concepts-materialize-in-net/)
- [Midori (operating system) - Wikipedia](https://en.wikipedia.org/wiki/Midori_(operating_system))

### Vale
- [The Vale Programming Language](https://vale.dev/)
- [Vale's Memory Safety Strategy: Generational References and Regions](https://verdagon.dev/blog/generational-references)
- [Zero-Cost Borrowing with Vale Regions, Part 1](https://verdagon.dev/blog/zero-cost-borrowing-regions-part-1-immutable-borrowing)
- [Vale's First Prototype for Immutable Region Borrowing](https://verdagon.dev/blog/first-regions-prototype)
- [GitHub - ValeLang/Vale](https://github.com/ValeLang/Vale)

### Austral
- [The Austral Programming Language](https://austral-lang.org/)
- [Introducing Austral (Fernando Borretti)](https://borretti.me/article/introducing-austral)
- [Interview with Fernando Borretti about Austral](https://blog.lambdaclass.com/austral/)
- [Linear Types and Exceptions (Borretti)](https://borretti.me/article/linear-types-exceptions)
- [Type Systems for Memory Safety (Borretti)](https://borretti.me/article/type-systems-memory-safety)
- [What Austral Proves](https://animaomnium.github.io/what-austral-proves/)
- [GitHub - austral/austral](https://github.com/austral/austral)

### Mojo
- [Mojo Ownership Documentation](https://docs.modular.com/mojo/manual/values/ownership/)
- [Deep Dive into Ownership in Mojo (Modular blog)](https://www.modular.com/blog/deep-dive-into-ownership-in-mojo)
- [Mojo Value Ownership Proposal](https://github.com/modular/mojo/blob/main/proposals/value-ownership.md)
- [Mojo (programming language) - Wikipedia](https://en.wikipedia.org/wiki/Mojo_(programming_language))

### Rust on CLR
- [rustc_codegen_clr (GitHub)](https://github.com/FractalFir/rustc_codegen_clr)
- [Compiling Rust for .NET (FractalFir blog)](https://fractalfir.github.io/generated_html/rustc_codegen_clr_v0_0_1.html)
- [Rust to .NET compiler - GSoC 2024 experiences](https://fractalfir.github.io/generated_html/rustc_codegen_clr_v0_2_0.html)

### Verona
- [Project Verona](https://microsoft.github.io/verona/)
- [Project Verona (GitHub)](https://github.com/microsoft/verona)
- [Project Verona Publications](https://microsoft.github.io/verona/publications.html)

### Koka
- [The Koka Programming Language](https://koka-lang.github.io/koka/doc/book.html)
- [Koka - Microsoft Research](https://www.microsoft.com/en-us/research/project/koka/)
- [Perceus: Garbage Free Reference Counting with Reuse (MSR)](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/)

### Hylo
- [Hylo Programming Language](https://hylo-lang.org/)
- [Hylo (GitHub)](https://github.com/hylo-lang/hylo)
- [Designing Hylo (ECOOP/PLSS 2025)](https://2025.ecoop.org/details/plss-2025-papers/12/Designing-Hylo-a-programming-language-for-safe-systems-programming)

### Cone
- [Cone Programming Language](https://cone.jondgoodwin.com/)
- [Cone - Memory Managed Your Way](https://cone.jondgoodwin.com/memory.html)
- [GitHub - jondgoodwin/cone](https://github.com/jondgoodwin/cone)

### Lobster
- [The Lobster Programming Language](https://strlen.com/lobster/)
- [Memory Management in Lobster](https://aardappel.github.io/lobster/memory_management.html)
- [GitHub - aardappel/lobster](https://github.com/aardappel/lobster)

### Roslyn Analyzers
- [Roslyn Analyzers Overview (Microsoft Learn)](https://learn.microsoft.com/en-us/visualstudio/code-quality/roslyn-analyzers-overview)
- [Writing Dataflow Analysis Based Analyzers](https://github.com/dotnet/roslyn-analyzers/blob/main/docs/Writing%20dataflow%20analysis%20based%20analyzers.md)
- [Analyzing Control Flow with Roslyn](https://www.atmosera.com/blog/analyzing-control-flow-with-roslyn/)
- [A Comparison of Rust's Borrow Checker to the One in C#](https://em-tg.github.io/csborrow/)
- [C# Low-Level Struct Improvements Specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-11.0/low-level-struct-improvements)
