# Cross-Cutting Feasibility Assessment

This document assesses feasibility questions that affect both Cobalt approaches (augmented C# and new language) equally. Each concern spans the fundamental challenge of combining Rust-style ownership with the .NET runtime, regardless of whether the implementation is a Roslyn analyzer layer or a standalone compiler emitting CIL.

The analysis is grounded in the Phase 1 research documents (03-memory-management, 04-concurrency, 05-il-constraints, 06-interop, 07-prior-art).

---

## 1. Rust Interop

**Verdict: Viable with constraints**

### Current State: FFI Is the Established Path

Today, .NET-to-Rust interop is exclusively FFI-based. The standard pattern is well-documented (Phase 1.6 analysis): Rust exposes `extern "C"` functions from a `cdylib`, and C# calls them via `LibraryImport` (or legacy `DllImport`). Several toolchains automate this:

- **csbindgen** (Cysharp): generates C# P/Invoke declarations from Rust `extern "C"` signatures. Handles primitives, structs, unions, enums, and function pointers. No lifecycle management.
- **Interoptopus**: generates idiomatic C# bindings from annotated Rust code. C# is the only Tier 1 target. Measures ~20-52ns per FFI call for blittable types.
- **uniffi-bindgen-cs** (NordSecurity): generates C# bindings from UniFFI's UDL interface definitions. Handles complex types through serialization. Still at major version 0.x.

All three approaches share fundamental limitations: ownership information is lost at the boundary, strings require encoding conversion (UTF-8 to UTF-16), collections must be copied or accessed through opaque handles, and error handling is bolted on through conventions rather than types.

### Could Cobalt Provide Better Rust Interop Than Plain C#?

Yes, in specific ways that neither approach (augmented C# nor new language) can avoid needing:

**Shared ownership model.** Cobalt's borrow checker could track ownership across the interop boundary. When Cobalt transfers ownership of a value to Rust (via `Box::into_raw` / `Box::from_raw` patterns), the borrow checker can statically prevent the Cobalt side from accessing the value afterward. When Cobalt lends a reference to Rust, it can enforce that the reference does not outlive the borrowed data. Current C# achieves this only through the `SafeHandle` runtime pattern -- Cobalt could make it a compile-time guarantee.

**Compatible type representations.** Cobalt could define types with `#[repr(C)]`-compatible layouts that are usable from both .NET and Rust without marshalling. For value types containing only blittable fields, the same memory could be pinned on the .NET side and passed directly to Rust. The new-language approach has more freedom here (it controls type layout emission), but the augmented-C# approach could also enforce this through analyzer-checked attributes on struct definitions.

**Dual code generation.** A Cobalt compiler (new-language approach) could emit both .NET CIL and Rust-compatible C ABI exports from the same source definition, along with generated `.rs` binding files. This is analogous to what `cxx` does for C++/Rust but at the language level. The augmented-C# approach could achieve a weaker version through source generators that produce Rust FFI wrapper code from annotated C# types.

**Automatic pinning insertion.** The compiler could detect when a GC-heap value's address is shared with Rust and automatically insert pinning (`fixed` blocks for short-lived pins, POH allocation for long-lived buffers). This removes a category of manual, error-prone work.

### rustc_codegen_clr: A Deeper Integration Path?

The `rustc_codegen_clr` project compiles Rust MIR directly to CIL, producing .NET assemblies from Rust source code. As of 2025, it passes approximately 95% of the Rust `core` and `std` test suites and participated in Google Summer of Code. It remains experimental, Linux-only (x86_64), and tested primarily on .NET 8 with CoreCLR.

This project demonstrates a critical distinction: Rust's ownership model can be *compiled to* CIL, but CIL cannot *enforce* ownership. The safety guarantees come from the Rust compiler frontend, not from the .NET runtime. The emitted CIL is standard -- the runtime sees ordinary managed code.

For Cobalt, `rustc_codegen_clr` opens a theoretical path where Rust libraries could be compiled to .NET assemblies and consumed directly, eliminating the FFI boundary entirely. However, this depends on the project reaching production quality and supporting all target platforms -- neither of which is guaranteed. The project's single maintainer and narrow platform support make it a high-risk dependency.

**Realistic assessment:** `rustc_codegen_clr` is worth monitoring as a long-term possibility but cannot be relied upon for Cobalt's initial Rust interop story. If it matures, Cobalt could adopt it as an optional deeper-integration path alongside traditional FFI.

### WASM Component Model: An Alternative Interop Path?

The WASM Component Model defines a language-neutral interface mechanism through WIT (WebAssembly Interface Types). Both Rust and .NET can compile to WASM, and the specification was standardized in late 2024. WASI 0.3 is expected in 2026, with WASI 1.0 targeted for completion in late 2026.

Current limitations for Cobalt's use case:

- **Performance gap.** WASI file I/O has been benchmarked at 10x slower than native. The serialization overhead of the Component Model's canonical ABI adds latency to every cross-component call.
- **Single-threaded execution model.** The Component Model does not yet support threading, making it unsuitable for concurrent workloads.
- **.NET tooling maturity.** The `componentize-dotnet` project (Bytecode Alliance) requires .NET 10+ preview and is currently Windows-only.
- **Not suited for tight in-process integration.** The Component Model is designed for sandboxed, capability-based composition -- a different use case than the tight in-process integration Cobalt targets.

**Realistic assessment:** The WASM Component Model is a viable *future* interop path for sandboxed or edge deployment scenarios, but it is not suitable as Cobalt's primary Rust interop mechanism. Direct FFI will remain more practical for native desktop/server applications for the foreseeable future.

### Realistic Interop Timeline

**Initial version (v0.1):** FFI-based interop using `LibraryImport` with ownership-aware wrappers. Cobalt provides attribute-driven binding generation (similar to csbindgen but with ownership annotations that the borrow checker enforces). Blittable types only for zero-copy paths. This is achievable with either approach.

**Medium-term (v0.x):** Shared type definitions that generate both CIL representations and `#[repr(C)]` Rust structs. Automatic pinning insertion. Async interop bridging (Rust futures to .NET Tasks via callback chains). Ownership tracking across the FFI boundary.

**Long-term (v1.0+):** If `rustc_codegen_clr` matures, optional direct compilation of Rust dependencies to CIL assemblies. WASM Component Model as an alternative interop path for sandboxed scenarios. Zero-copy iteration over Rust collections from Cobalt.

### Approach Comparison

Both approaches share the same FFI foundation. The new-language approach has an advantage for dual code generation (it controls the compiler pipeline end-to-end), while the augmented-C# approach is constrained to source generators and analyzers for Rust binding generation. The difference is moderate -- the core interop challenges (marshalling, ownership at the boundary, string encoding) are identical.

---

## 2. GC Interaction

**Verdict: Viable with constraints**

### The Fundamental Tension

Rust ownership assumes deterministic destruction: when an owner goes out of scope, the value is destroyed immediately and its memory is freed. .NET's GC is non-deterministic: objects remain on the heap until the GC decides to collect them, which may be long after they become unreachable. This tension is the central design challenge for Cobalt and applies equally to both approaches.

The Phase 1.3 research document identifies the key asymmetry: in Rust, value lifetime and memory lifetime are identical. On .NET, they are decoupled. Cobalt can guarantee deterministic *value destruction* (resource cleanup via `Drop`/`Dispose`) while delegating *memory reclamation* to the GC. This is weaker than Rust's guarantee but is the strongest achievable on the CLR without reimplementing the runtime.

### Can Cobalt Types Opt Out of GC?

Partially, through existing .NET mechanisms:

**Value types (structs).** Value types are stack-allocated or inline-allocated within their containing object. They are not individually GC-tracked, not heap-allocated (unless boxed), and reclaimed deterministically at scope exit. Value types are the closest CIL analog to Rust's non-`Box` types. Cobalt could default to value-type representation for owned data, using the heap only when necessary (boxing, interface dispatch, or an explicit `Box<T>` equivalent).

Limitations: CIL structs cannot participate in inheritance, are copied on assignment (Cobalt's move semantics would prevent unintentional copies at the source level, but the IL still performs bitwise copies), and large structs degrade performance. The JIT's inlining and register allocation heuristics are optimized for small structs (up to ~16 bytes).

**Ref structs.** Ref structs (`IsByRefLike`) provide runtime-enforced stack confinement: they cannot be boxed, stored in class fields, used as array elements, or (before C# 13) used as generic type arguments. These restrictions are enforced by the runtime at type-load time, not just by the compiler. Ref structs model a restricted subset of ownership semantics: stack-confined values with bounded lifetimes. They are a useful building block but insufficient alone -- Rust's ownership model also covers heap-allocated owned values, cross-function moves, and parametric lifetimes.

**Stack allocation via escape analysis.** .NET 10 significantly expanded the JIT's escape analysis capabilities, enabling stack allocation of small arrays (both value-type and reference-type elements), delegates, and enumerators when the JIT can prove they do not escape the method. Benchmarks show nearly 3x performance improvement in some delegate scenarios and zero GC allocations. A language with ownership tracking has far more information available than the JIT's escape analysis: if a value is owned and not borrowed beyond the current scope, it is provably non-escaping. Cobalt could communicate this to the JIT via appropriate IL patterns (allocating as value types, using `ref struct` wrappers) rather than relying on the JIT to rediscover what the compiler already knows.

### Mixed Object Graphs: GC-Managed References to Owned Values

This is the hardest sub-problem. Consider the scenarios:

**Cobalt-owned value referenced by a GC-managed object.** If a GC-managed object (e.g., a C# class instance) holds a reference to a Cobalt-owned value, the ownership boundary is violated: the GC-managed object may outlive the owned value, creating a dangling reference. Solutions:

1. **Prohibit the pattern statically.** The borrow checker could reject any code that stores a reference to an owned value in a GC-managed object. This is safe but restrictive -- it prevents common patterns like caching or observer registration.
2. **Extend the lifetime.** When an owned value is stored in a GC-managed object, the compiler promotes it to GC-managed lifetime (essentially boxing it). The value loses its deterministic destruction guarantee but remains safe. The compiler could warn when this promotion occurs.
3. **Weak ownership.** The GC-managed object holds a weak reference to the owned value. When the owned value is destroyed, the weak reference becomes null. This is the `WeakReference<T>` pattern already in .NET.

**Cobalt-owned value referencing a GC-managed object.** This is less problematic. GC-managed objects are alive as long as any root references them. An owned Cobalt value that holds a reference to a GC-managed object acts as a root, keeping the GC object alive. When the Cobalt value is dropped, the reference is released, and the GC object becomes eligible for collection if no other roots exist. This aligns with how `IDisposable` types in C# already work.

**Circular references across the boundary.** A Cobalt-owned value references a GC-managed object, which in turn references the Cobalt-owned value. This is the most dangerous pattern. The Cobalt borrow checker cannot see through GC-managed objects (it does not control the GC's reachability analysis). The GC sees the Cobalt value as live (because the GC-managed object references it), so the owned value's destructor may never run (the ownership system thinks it should drop it, but the GC keeps it alive through the back-reference). Cobalt must either prohibit this pattern or require explicit cycle-breaking (e.g., weak references on one side).

### Drop/Dispose Integration

The Phase 1.3 research document evaluates four approaches for deterministic destruction on .NET. The pragmatic synthesis:

**Compiler-inserted `Dispose()` calls.** The Cobalt compiler treats owned values like `using` declarations, automatically inserting `Dispose()` calls when ownership ends. This works within the existing .NET model and interoperates cleanly with C#. All Cobalt types with destructors implement `IDisposable`. Drop order is deterministic and compiler-controlled (reverse declaration order for locals, matching Rust's convention).

**Null-on-drop.** After calling `Dispose()`, the compiler nulls out the reference, making the object unreachable immediately. Combined with ownership tracking, the compiler guarantees no other references exist. The object becomes eligible for collection in the next Gen 0 sweep. This reduces the window between "value is dead" and "memory is reclaimed."

**Finalization as a safety net -- or not.** If the Cobalt compiler guarantees that `Drop`/`Dispose` runs at ownership boundaries, finalizers become unnecessary for Cobalt-authored types. This avoids the two-pass collection penalty, the finalization thread overhead, and the resurrection hazard. Cobalt should not emit finalizers for its own types. For C# types consumed by Cobalt that have finalizers, Cobalt treats their finalization as opaque -- handled by the .NET runtime.

**The reliability question.** Can deterministic `Drop` be reliable when the GC may collect dependencies? Yes, if the ownership graph is acyclic and the drop order is well-defined. The Cobalt compiler controls drop order. Dependencies of a dropping value are still alive at the time `Drop` runs (they are dropped after their dependents, per reverse declaration order). GC-managed objects referenced by a dropping value are alive because the dropping value is a root until its drop completes. The problematic case is when a dependency has *already* been dropped by another ownership path -- which the borrow checker prevents (double-drop is a use-after-move error).

### The Ownership Boundary

The key design principle: **the ownership boundary must be explicit and compiler-enforced.** Every value in a Cobalt program is either:

1. **Owned by the Cobalt ownership system.** Subject to move semantics, borrow checking, and deterministic `Drop`. May be stack-allocated (value types) or heap-allocated (boxed). Deterministic destruction of resources; memory reclamation deferred to GC for heap-allocated values.
2. **Managed by the .NET GC.** Ordinary .NET reference types with no ownership tracking. These are the default when consuming C# libraries. Cobalt treats them as it treats `unsafe` foreign types in Rust -- the compiler makes no ownership guarantees about them.
3. **At the boundary.** Values transitioning between systems (e.g., an owned value being stored in a GC-managed collection) undergo explicit promotion or wrapping. The compiler flags these transitions.

This boundary exists regardless of approach. The augmented-C# approach encodes it through analyzer-checked attributes (`[Owned]`, `[Borrowed]`). The new-language approach encodes it through syntax and compiler-enforced semantics. The new-language approach provides stronger guarantees (the compiler *is* the enforcement mechanism, not an advisory analyzer), but the conceptual model is the same.

### Approach Comparison

The GC interaction challenge is identical for both approaches. The new-language approach can enforce the ownership boundary more strictly (compiler errors vs. analyzer warnings), but neither approach can change the fundamental runtime behavior: the GC is non-deterministic, value types are copied in CIL, and ref structs have hard restrictions. The augmented-C# approach is somewhat more constrained because it must work within C#'s existing semantics (e.g., it cannot introduce move semantics for classes -- only suppress use-after-move through diagnostics). The new-language approach can define its own semantics that compile *down* to CIL, giving it more design freedom.

---

## 3. Performance

**Verdict: Viable -- ownership provides tangible benefits beyond correctness, though the magnitude depends on workload**

### Can Ownership Semantics Reduce GC Pressure?

Yes. The Phase 1.3 research document identifies four concrete mechanisms, and .NET 10's JIT improvements validate the approach:

**1. Stack allocation of owned values.** If the compiler can prove a value has a single owner and does not escape the current method, it can emit it as a value type (CIL struct) allocated on the stack. No heap allocation, no GC tracking, no object header overhead (16 bytes per reference-type object on 64-bit .NET), no collection cost. .NET 10's expanded escape analysis already achieves this for some cases automatically -- benchmarks show nearly 3x speedups for delegates and zero allocations for small arrays. A language with ownership tracking has strictly more information than the JIT's escape analysis, because the JIT must conservatively analyze compiled code while the Cobalt compiler knows the ownership semantics by construction.

The magnitude depends on allocation patterns. For allocation-heavy workloads (parsers, compilers, game loops, request handlers that allocate per-request objects), reducing heap allocations directly reduces GC frequency. For workloads dominated by long-lived objects (caches, connection pools), the benefit is smaller.

**2. Smaller live-set.** In standard .NET, objects remain on the heap until the GC collects them, which may be long after they become unreachable. With ownership semantics, the compiler nulls out references at ownership boundaries, making objects eligible for collection immediately. This reduces the live-set at any given point, making GC mark phases faster (cost is proportional to live objects) and collections less frequent (less memory pressure triggers fewer Gen 0 collections).

**3. Fewer defensive copies.** C#'s value-type semantics require defensive copies in many scenarios: passing structs to methods by value, accessing members of `readonly` struct fields (the compiler copies the struct to avoid potential mutation through a `readonly` reference). Ownership and borrowing semantics eliminate these copies. When a function takes a `&T` (shared borrow), the compiler knows the callee will not mutate the value, so no defensive copy is needed. When a function takes `owned T`, the value is moved (bitwise copy, but only one) and the source is invalidated -- no redundant copy exists.

**4. Reduced finalization overhead.** If Cobalt guarantees deterministic `Drop`/`Dispose` at ownership boundaries, finalizers become unnecessary for Cobalt types. Each finalizable object survives at least two GC cycles (one to move it to the f-reachable queue, one to collect it after the finalizer runs), is promoted to a higher generation, and requires processing by the dedicated finalizer thread. Eliminating finalizers for owned types removes this overhead entirely.

### Can the Compiler Use Ownership Information to Optimize?

Yes, through several mechanisms:

**Elide copies.** When a value is moved, the compiler knows the source will not be accessed again. For value types in CIL, the "move" is a bitwise copy followed by source invalidation (conceptually). The compiler can optimize the copy away if the source location is being reused immediately -- for example, returning an owned value from a function can use the caller's storage directly. This is analogous to Rust's move semantics and C++'s Named Return Value Optimization (NRVO), which the .NET JIT does not currently perform for value types.

**Avoid defensive cloning.** .NET APIs that accept shared references to mutable data often defensively clone the data to prevent aliasing issues. With borrow checking, the compiler guarantees that shared borrows (`&T`) cannot be used to mutate the value, eliminating the need for defensive clones. This is particularly impactful for collection types and string processing.

**Enable move-based APIs.** APIs that currently accept and return cloned data (e.g., builder patterns that clone intermediate state) can instead accept and return owned values, transferring ownership without copying. This is a design-level optimization enabled by the type system.

**Write barrier elimination.** Every reference-type field assignment in .NET requires a write barrier -- a small piece of JIT-emitted code that updates the GC's card table for cross-generational reference tracking. If ownership analysis can prove that a reference assignment does not create a cross-generational reference (e.g., a newly allocated object referencing another newly allocated object, both in Gen 0), the write barrier could theoretically be elided. This would require cooperation from the JIT, which currently emits write barriers unconditionally for reference-type stores. The benefit would be measurable in allocation-heavy code but requires JIT-level integration that neither Cobalt approach can achieve unilaterally.

### Evidence from Related Projects

**Midori.** Over eight years, the Midori team narrowed the gap between their managed, safe C# variant and classical C/C++ systems programming to the point where "basic code quality, in both size and speed dimensions, was seldom the deciding factor." The ability to co-design the language, runtime, frameworks, and compiler gave the compiler far more symbolic information about program semantics, enabling it to "exceed C and C++ performance in a non-trivial number of situations." Midori's permission types (isolated, immutable, readonly) provided ownership-like information that the AOT compiler exploited for optimization. This is the strongest existence proof that ownership semantics on a managed runtime can yield real performance gains -- but Midori had the advantage of co-designing the runtime, which Cobalt does not.

**Mojo.** Mojo claims up to 35,000x speedups over Python, though this comparison is misleading (Python is interpreted, not JIT-compiled). The relevant comparison is Mojo's ownership-managed values versus Python's GC-managed objects. Mojo's owned values avoid GC overhead entirely within pure Mojo code, with deterministic destruction and no garbage collector. The dual-world model (owned Mojo values + GC'd Python objects) is directly analogous to what Cobalt faces. Mojo's MLIR foundation gives it more optimization latitude than CIL provides.

**rustc_codegen_clr.** Performance data from this project is limited. Compiling Rust to CIL can potentially improve JIT optimization -- the JIT sees all code and can make better inlining and devirtualization decisions. However, the CIL representation loses information that LLVM exploits (alias analysis based on ownership, `noalias` annotations). The performance profile of Rust-on-CLR versus native Rust is not well-characterized.

**.NET 10 escape analysis.** The JIT's own improvements validate the thesis. Stack allocation through escape analysis in .NET 10 shows "performance nearly triples" in delegate scenarios, with memory usage dropping from ~88 bytes to ~24 bytes. These gains come from the JIT *partially* recovering ownership information that a language like Cobalt would have from the start. Cobalt's ownership tracking is strictly more powerful than the JIT's escape analysis, because the JIT must be conservative while Cobalt has explicit ownership annotations.

### Is "Correctness Only" a Sufficient Justification?

Yes. Even if ownership semantics provided zero performance benefit, the correctness guarantees alone justify Cobalt:

- **Elimination of use-after-free.** .NET's GC prevents use-after-free for memory, but not for resources. Calling methods on a disposed `IDisposable` object throws `ObjectDisposedException` at runtime. Ownership tracking catches this at compile time.
- **Elimination of data races.** .NET provides no compile-time data-race prevention (Phase 1.4 analysis). Cobalt's `Send`/`Sync` traits, combined with borrow checking, make data races in safe Cobalt code impossible. This is the same guarantee Rust provides.
- **Elimination of resource leaks.** .NET's `IDisposable` is convention-based. Forgetting `using` is a common bug. Ownership tracking makes resource management a compiler responsibility.
- **Elimination of null reference exceptions.** Combined with non-nullable reference types (already partially in C# via NRT), ownership tracking can strengthen the guarantee from advisory to enforced.

Midori demonstrated that safety and performance are not in conflict -- they are complementary. Safe code enables more aggressive optimization because the compiler can make stronger assumptions. The correctness benefits alone provide sufficient value, and the performance benefits are a bonus.

### Compile-Time Overhead of Borrow Checking

Borrow checking adds a dataflow analysis pass to compilation. In Rust, the borrow checker is a significant fraction of compile time -- but Rust's compile times are dominated by LLVM codegen and monomorphization, not borrow checking per se. The borrow checker's analysis is roughly O(n) in the size of the function body for most practical code (it is a flow-sensitive analysis over the MIR control flow graph).

For the augmented-C# approach, the borrow checker would run as a Roslyn analyzer. Roslyn analyzers run in the IDE on every keystroke and during compilation. A borrow-checking analyzer that noticeably slows the IDE will not be adopted. The Phase 1.7 analysis notes this risk and recommends bounding analysis depth. Intra-procedural analysis (within a single method) is fast; inter-procedural analysis (across method calls within the same assembly) is configurable but expensive. Cross-assembly analysis is not possible -- the analyzer must rely on attribute-based contracts.

For the new-language approach, borrow checking is part of the compiler pipeline. The overhead is comparable to Rust's borrow checker, proportional to code complexity. Since the Cobalt compiler controls the entire pipeline, it can optimize the analysis and parallelize it across compilation units.

**Realistic assessment:** Compile-time overhead is not a blocker. The borrow checker adds measurable but manageable cost, comparable to other advanced type-checking passes (nullability analysis, generic constraint checking). The augmented-C# approach faces tighter performance constraints (IDE responsiveness) than the new-language approach.

### Approach Comparison

Performance benefits are identical for both approaches at the *semantic* level -- ownership information enables the same optimizations regardless of how it is tracked. The difference is in what optimizations the implementation can *act on*:

- The **augmented-C# approach** can inform developers about optimization opportunities (analyzer diagnostics suggesting stack allocation, flagging unnecessary copies) but cannot change code generation. The C# compiler and JIT make the actual optimization decisions.
- The **new-language approach** controls code generation and can directly emit optimized CIL: preferring value types for owned data, avoiding redundant copies, inserting null-on-drop, and choosing between stack and heap allocation based on escape analysis performed during compilation.

The new-language approach has a moderate advantage for performance optimization, though the gap narrows as the .NET JIT improves its own escape analysis and optimization capabilities.

---

## Summary

| Concern | Verdict | Shared or Approach-Specific | Notes |
|---|---|---|---|
| **Rust interop via FFI** | Viable with constraints | Shared (identical foundation) | Both approaches use the same P/Invoke infrastructure. New-language has a moderate advantage for dual code generation. |
| **Deeper Rust integration (rustc_codegen_clr)** | Viable with constraints | Shared | Depends on external project maturity. High-risk dependency for either approach. |
| **WASM Component Model interop** | Viable with constraints | Shared | Not production-ready for Cobalt's primary use case. Monitor as a future option. |
| **Ownership-aware Rust interop** | Viable with constraints | Slight advantage: new language | New-language approach can enforce ownership at the FFI boundary more strictly. |
| **GC coexistence with owned values** | Viable with constraints | Shared | Both approaches face the same runtime constraints. Value types, ref structs, and null-on-drop work identically. |
| **Mixed ownership graphs** | Viable with constraints | Shared | Requires explicit boundary marking. Cyclic references across the boundary must be prohibited or use weak references. |
| **Drop/Dispose integration** | Viable with constraints | Shared | Compiler-inserted `Dispose()` is the pragmatic path. Finalizers should be unnecessary for Cobalt types. |
| **GC pressure reduction** | Viable | Slight advantage: new language | New-language controls codegen and can directly emit stack-allocating value types. Augmented-C# can only advise. |
| **Performance via copy elision** | Viable | Moderate advantage: new language | New-language can emit optimized IL. Augmented-C# depends on the C# compiler and JIT. |
| **Correctness guarantees** | Viable | Moderate advantage: new language | New-language enforces via compiler errors. Augmented-C# enforces via analyzer warnings (suppressible). |
| **Compile-time overhead** | Viable | Slight advantage: new language | Augmented-C# has IDE responsiveness constraints. New-language has full pipeline control. |

### Overall Assessment

All three cross-cutting concerns -- Rust interop, GC interaction, and performance -- are **viable with constraints** for both approaches. None is a blocker.

**Rust interop** is constrained to FFI for the foreseeable future, with ownership-aware wrappers providing value over plain C# in either approach. Deeper integration paths (rustc_codegen_clr, WASM Component Model) are promising but immature. The practical interop story for Cobalt v1 is FFI with ownership-tracked wrappers -- achievable by both approaches.

**GC interaction** requires accepting a key asymmetry: value lifetime (deterministic `Drop`) and memory lifetime (GC reclamation) are decoupled on .NET. This is weaker than Rust's unified model but is the strongest achievable without reimplementing the runtime. The design principle of an explicit, compiler-enforced ownership boundary (owned values vs. GC-managed values) is sound and maps onto existing .NET mechanisms (value types, ref structs, `IDisposable`). Both approaches can implement this boundary, but the new-language approach enforces it more strictly.

**Performance** benefits are real and supported by evidence from Midori, Mojo, and .NET 10's own JIT improvements. Ownership information enables stack allocation, copy elision, smaller live-sets, and finalization elimination. The magnitude depends on workload, but allocation-heavy workloads (common in servers, parsers, and game loops) will see measurable gains. Even without performance benefits, the correctness guarantees alone justify the project. The new-language approach has a moderate advantage because it controls code generation directly, while the augmented-C# approach depends on the existing C# compiler and JIT to act on the information.

The new-language approach has a consistent, moderate advantage across all three concerns -- it controls the compiler pipeline and can enforce guarantees strictly rather than advisorily. However, no concern is infeasible for the augmented-C# approach; the difference is in the strength of guarantees and the degree of optimization possible.
