# Feasibility Assessment: Augmented C# Approach

This document assesses the technical feasibility of the "Augmented C#" approach to Cobalt: keeping standard C# as the language and adding Rust-style ownership and borrow-checking guarantees via Roslyn analyzers. Developers would write C# with custom attributes (e.g., `[Owned]`, `[Borrowed]`, `[MustDispose]`) and receive ownership/borrowing errors from the analyzer at compile time and in the IDE.

The assessment is grounded in the Phase 1 research findings documented in `docs/research/`, particularly the comparative analyses of type systems (01), ownership and borrowing (02), .NET IL constraints (05), prior art (07), and the tooling landscape (08).

---

## 1. Can Roslyn Analyzers Express Ownership and Borrowing Rules with Sufficient Precision?

**Verdict: Viable with constraints**

### What Roslyn's Data Flow and Control Flow Analysis Can Express

Roslyn provides a substantial foundation for intra-procedural analysis. The `ControlFlowGraph` API exposes an `IOperation`-based CFG with basic blocks, conditional branches, and explicit representations of exception regions. The `DataFlowAnalysis` API reports which variables are read, written, captured by closures, and flowing in/out of a region. The `PointsToAnalysis` framework in `roslyn-analyzers` tracks which storage locations a reference might point to -- conceptually similar to the alias analysis a borrow checker needs.

Within a single method body, an analyzer can:

- Track initialization state of variables and detect use-after-assignment patterns analogous to use-after-move.
- Identify when a variable holding an `[Owned]` value is assigned to a second variable (a "copy" that should be a "move") and flag the original as consumed.
- Detect when a `[Borrowed]` reference escapes its intended scope (stored in a field, captured in a closure, returned from the method).
- Enforce that a `[MustDispose]` value is disposed on all control flow paths, strengthening the existing CA2000 analysis.
- Check that mutable access (`ref` parameters, direct mutation) does not coexist with other live references to the same object within the method, approximating the aliasing XOR mutability invariant.

These capabilities are sufficient for a useful subset of ownership discipline within method bodies.

### Tracking Ownership Transfer (Moves) Across Method Calls

This is where fundamental limitations emerge. A move means that the caller's variable becomes invalid after passing ownership to a callee. An analyzer can model this for annotated methods: if a parameter is marked `[Owned]`, the analyzer can treat the argument as consumed at the call site and flag any subsequent use.

However, the analysis depends entirely on the annotations being correct. The analyzer cannot verify that the callee actually takes exclusive ownership -- it can only trust the annotation. If the callee stores the reference in a static field, shares it with other threads, or returns it, the analyzer has no way to detect this unless it inspects the callee's body.

Within the same compilation (same project/assembly), Roslyn's inter-procedural analysis can examine callee bodies with configurable depth. But this analysis is bounded for performance -- the default depth is shallow, and increasing it imposes significant compile-time and IDE-responsiveness costs. Cross-assembly calls (NuGet packages, framework libraries) are opaque: the analyzer treats them as black boxes and must rely entirely on metadata annotations.

### Enforcing Exclusive Mutable Access (Aliasing XOR Mutability)

Within a single method, alias analysis via `PointsToAnalysis` can determine whether two variables might refer to the same object. If they do, and one is used mutably, the analyzer can flag this as an aliasing violation.

The difficulty is precision. .NET's reference model makes aliasing pervasive -- passing an object to a method, storing it in a collection, or capturing it in a closure all create aliases. A strict aliasing analysis that flagged all such patterns would reject most idiomatic C# code. To be usable, the analyzer must distinguish between "checked" code (where aliasing constraints apply) and "unchecked" code (where they do not), creating a two-world model within the same language.

Furthermore, the aliasing XOR mutability invariant cannot be enforced at the type system level in C#. Roslyn analyzers produce diagnostics -- they do not alter type checking. A user can suppress warnings or disable the analyzer entirely, at which point no guarantees hold. This is fundamentally different from Rust's borrow checker, which is integrated into the compiler and cannot be bypassed.

### Inter-Procedural Analysis Across Method Boundaries

The Phase 1 tooling research (08-tooling-landscape.md) identifies this as the central feasibility bottleneck. Key findings:

- **Roslyn's flow analysis is intra-procedural by design.** The nullable reference type analysis -- the most sophisticated flow analysis built into Roslyn -- is explicitly documented as performing no inter-procedural analysis. Calling a method that assigns a field does not update the caller's flow state for that field.
- **The `roslyn-analyzers` dataflow framework supports bounded inter-procedural analysis**, but it is designed for security-oriented taint tracking, not ownership tracking. The analysis depth is configurable but performance-constrained.
- **Infer# (Microsoft's adaptation of Facebook's Infer for .NET) performs inter-procedural analysis but does not run as a Roslyn analyzer.** It operates on compiled assemblies as a separate post-build step. The tooling research notes that this architectural choice is telling: the Roslyn analyzer framework was not sufficient for the inter-procedural analysis Infer# requires.
- **Cross-assembly analysis is not supported.** External method calls are black boxes. This means any method defined outside the current project -- including the entire BCL, all NuGet packages, and all referenced assemblies -- cannot be analyzed for ownership behavior.

The practical consequence is that a Roslyn-based borrow checker must work on a contract-based model: method signatures carry annotations (via attributes), and the analyzer trusts those annotations without verifying them against the method body when the body is in a different assembly. This is viable but produces weaker guarantees than a compiler-integrated borrow checker.

### Assessment

The analyzer can enforce ownership rules within method bodies with good precision and across method boundaries within the same project with moderate precision. Cross-assembly enforcement depends entirely on the availability and correctness of annotations on external APIs. The system is best understood as a sophisticated lint -- valuable for catching bugs, but not a compile-time proof of safety.

---

## 2. Can Lifetime Analysis Be Performed Without Language-Level Annotations (Inference Only)?

**Verdict: Viable with constraints**

### What Can Be Inferred from C#'s Existing Type System and Ref Safety Rules

C# already has a limited lifetime-tracking system for `ref` returns, `ref struct` types, and `Span<T>`. As documented in the Phase 1 ownership research (02-ownership-and-borrowing.md), C# assigns each expression a "safe-context" from a fixed set: `caller-context`, `function-member`, and `declaration-block`. The `scoped` keyword narrows a parameter's safe-context to `function-member`, preventing it from escaping the method.

An analyzer can leverage these existing rules as a foundation:

- **`scoped` parameters** already express "this reference does not escape." The analyzer can treat `scoped ref T` and `scoped Span<T>` parameters as having a known lifetime bound to the current method.
- **`ref` returns** carry implicit lifetime information: the returned reference must not outlive the data it points to, and the compiler already enforces this.
- **`ref struct` types** cannot escape to the heap. This provides a coarse-grained lifetime guarantee that the analyzer can build on.
- **`in` / `ref readonly` parameters** express immutable borrowing intent, even though C# does not enforce exclusivity.

For simple patterns -- a method takes a reference parameter and returns void, or a method returns a reference tied to `this` -- the existing ref safety rules provide enough information to infer lifetime relationships without any additional annotations. This parallels Rust's first two lifetime elision rules, which cover the majority of simple function signatures.

### Patterns That Require Explicit Annotations

Several patterns cannot be analyzed without additional information:

1. **Multiple reference parameters with a reference return.** When a method takes two `ref` parameters and returns a `ref`, the analyzer cannot determine which input's lifetime the return value is tied to. Rust requires explicit lifetime annotations here (`fn longest<'a>(x: &'a str, y: &'a str) -> &'a str`). C# has no way to express this relationship -- not even via attributes, since attributes cannot create parametric relationships between positions in a signature.

2. **Stored references with complex lifetime relationships.** A struct that holds a reference (only possible with `ref struct` and ref fields) has an implicit lifetime bound to its ref field's referent. But if the struct holds multiple ref fields with potentially different lifetimes, there is no way to express which lifetime applies to which field. C#'s ref safety system treats all ref fields as having the same safe-context.

3. **Ownership transfer chains.** When ownership passes through multiple methods (`A` calls `B` calls `C`, each transferring ownership), the analyzer needs to know at each boundary whether the callee consumes, borrows, or shares the value. Without annotations, the analyzer must assume the worst case (the value may be aliased and escape).

4. **Conditional lifetime relationships.** Patterns where the return value's lifetime depends on runtime conditions (e.g., "returns a reference to the first argument if the condition is true, otherwise to the second") require lifetime annotations that C# cannot express.

### Comparison with Rust's Lifetime Elision

Rust's three elision rules cover approximately 87% of function signatures that would otherwise need explicit annotations (per RFC 141's survey of the standard library). The coverage is high because most functions fall into one of three categories: (a) takes one reference, returns one reference (tied to the input); (b) takes `&self`, returns a reference (tied to `self`); (c) takes references but returns no references (no annotation needed).

A C# analyzer could replicate similar elision rules:

- **Single `ref`/`in`/`Span<T>` parameter + reference return:** infer the return is tied to the input.
- **Instance method with reference return:** infer the return is tied to `this`.
- **No reference return:** no lifetime tracking needed for the return.

However, the coverage would likely be lower than Rust's 87% for two reasons:

1. C# methods more frequently take multiple reference-like parameters (particularly with `ref`, `in`, and `out` all available), creating more ambiguous cases.
2. C# objects are predominantly heap-allocated reference types, whose lifetimes are managed by the GC and do not need (or benefit from) static lifetime tracking. The cases where lifetime tracking adds value -- `ref struct`, `Span<T>`, `IDisposable` resources -- are a smaller fraction of total method signatures.

An estimate of 60-70% coverage by inference alone is reasonable, with the remaining 30-40% requiring explicit attributes or being inherently inexpressible.

### Expressiveness of Custom Attributes

Custom attributes like `[Owned]`, `[Borrowed]`, `[MustDispose]` can express ownership mode (who is responsible for the value) but cannot express parametric lifetime relationships.

What attributes can express:

| Attribute | Semantics | Expressiveness |
|---|---|---|
| `[Owned]` | The recipient takes exclusive ownership; the caller must not use the value after passing it | Equivalent to Rust's move-by-value |
| `[Borrowed]` | The recipient receives a temporary, non-owning reference | Equivalent to `&T` in Rust (without lifetime parameter) |
| `[MutBorrowed]` | The recipient receives exclusive mutable access | Equivalent to `&mut T` (without lifetime parameter) |
| `[MustDispose]` | The value must be disposed before leaving scope | Linear-type-like constraint on `IDisposable` |
| `[Scoped]` | The value must not escape the current method | Already exists as `scoped` keyword for ref structs; could be generalized |
| `[NoAlias]` | This reference is the only reference to the object | Equivalent to `isolated` in Midori |

What attributes cannot express:

- **Named lifetime parameters.** There is no way to write `[Lifetime("a")]` on two parameters and a return type to express that they share a lifetime. Attributes are isolated annotations -- they cannot create typed relationships between multiple positions in a signature.
- **Lifetime subtyping.** `'a: 'b` (lifetime `a` outlives lifetime `b`) has no attribute encoding.
- **Variance.** Whether a container is covariant or invariant in the lifetime of its contents cannot be expressed.
- **Higher-ranked lifetimes.** `for<'a> F: Fn(&'a str)` is inexpressible.

The Phase 1 IL constraints research (05-il-constraints.md) proposes a `CobaltLifetimeAttribute` with a byte-array encoding scheme mapping lifetime parameters to indices. This could work for Cobalt-to-Cobalt interop (the Cobalt compiler reads and writes these attributes), but it would be opaque to C# developers and invisible to C# tooling. It is a metadata encoding, not a language feature -- developers cannot write lifetime-parameterized types in C# syntax.

### Assessment

Inference can handle the simple cases, and attributes can handle ownership modes and basic constraints. But the core of Rust's lifetime system -- parametric lifetime relationships between references -- is inexpressible in C#'s type system or attribute system. The analyzer can provide useful guarantees for the common case (single-owner resources, scoped borrows, disposable tracking) but cannot provide the full precision of Rust's borrow checker for complex reference relationships.

---

## 3. How Do Existing C# Patterns Interact with Borrow-Checking Constraints?

**Verdict: Viable with constraints (some sub-patterns are blockers)**

This is the most complex feasibility question. C# has evolved a rich ecosystem of patterns that assume unrestricted aliasing and GC-managed lifetimes. Imposing borrow-checking constraints on top of these patterns creates friction that ranges from manageable to fundamental.

### async/await and Task Interaction with Ownership

**Sub-verdict: Blocker for full ownership; viable with constraints for resource tracking**

The async/await pattern is deeply incompatible with Rust-style ownership and lifetime tracking:

1. **State machine capture.** When a method is marked `async`, the compiler transforms it into a state machine struct (or class) that captures all local variables live across `await` points. This capture is a form of aliasing -- the state machine holds references to values that the original method scope also conceptually owns. An ownership analyzer must understand this transformation, which is performed by the Roslyn compiler itself and is not visible in the pre-transformation syntax tree that analyzers operate on.

2. **Task lifetime.** A `Task<T>` returned from an async method represents a computation whose lifetime is decoupled from the calling method. If the async method captures an `[Owned]` value, when does ownership transfer? At the `await` point? When the `Task` is created? When it completes? Rust handles this with the `T: Send + 'static` bound on spawned tasks -- the value must own all its data and be thread-safe. C# has no equivalent constraint mechanism.

3. **Ref struct incompatibility.** Ref structs -- the closest C# has to lifetime-bounded types -- cannot be live across `await` points (even with C# 13 relaxations). This means `Span<T>`, the primary safe-borrowing type in .NET, is fundamentally incompatible with async code. An analyzer tracking borrowed references would need to prevent any borrowed value from being used across an `await`, which is a severe restriction given that modern .NET code is pervasively async.

4. **Cancellation and exception semantics.** An async method can be cancelled or throw at any `await` point. If the method holds an `[Owned]` value, the ownership cleanup must be deterministic regardless of the completion path. The state machine's `Dispose` method handles this for `IAsyncDisposable`, but this is convention-based, not type-system-enforced.

**Practical scope:** An analyzer could track `IDisposable`/`IAsyncDisposable` ownership through async methods (ensuring `await using` is used correctly) but could not enforce full Rust-style lifetime tracking across `await` boundaries.

### LINQ, Lambda Captures, and Closures

**Sub-verdict: Viable with constraints**

Lambda expressions and closures create implicit aliasing that directly conflicts with borrow-checking:

1. **Closure capture semantics.** When a lambda captures a local variable, the compiler generates a closure class that stores a reference to (or a copy of) the variable. If the captured variable is `[Owned]`, the closure's capture creates a second reference to the owned value. The analyzer must determine whether the capture is by-value (which could be a move) or by-reference (which is a borrow), and this depends on whether the lambda modifies the variable.

2. **Deferred execution.** LINQ operators like `.Where()`, `.Select()`, and `.SelectMany()` accept lambdas that are stored and invoked later. The lifetime of the lambda (and its captures) is tied to the lifetime of the LINQ query, which may extend beyond the method that created it. An analyzer would need to treat LINQ chains as potentially escaping all captured values, severely limiting what can be captured from checked code.

3. **Multiple capture.** A single lambda can capture multiple variables. If any of them are borrowed, the lambda represents a simultaneous borrow of all captured values -- which interacts with the aliasing XOR mutability invariant. If one captured variable is mutably borrowed and another is shared, the analyzer must track this.

4. **Materialization.** LINQ methods like `.ToList()`, `.ToArray()`, and `.ToDictionary()` materialize a query, at which point the relationship between the source collection and the result is severed. An analyzer could use materialization as a point where borrows are released, but this requires understanding the semantics of specific BCL methods.

**Practical scope:** An analyzer could flag capture of `[Owned]` or `[MutBorrowed]` values in lambdas as warnings, guiding developers to either clone the value before capture or restructure the code. Full tracking of borrow relationships through LINQ chains would require per-method annotations on all LINQ extension methods, which is a large but finite annotation effort.

### Events and Delegates

**Sub-verdict: Viable with constraints**

Events are inherently aliased: multiple subscribers hold references to the event source (via the delegate's target object), and the event source holds references to all subscriber delegates. This is a many-to-many reference graph that cannot satisfy the aliasing XOR mutability invariant.

1. **Delegate storage.** Subscribing to an event (`obj.Event += handler`) stores a delegate that holds a strong reference to the handler's target object. The event source (the object with the `event` field) holds a multicast delegate containing all subscribers. This is shared mutable state by design.

2. **Lifetime issues.** Event subscriptions are a well-known source of memory leaks in .NET -- a subscriber that is not unsubscribed keeps the subscriber object alive as long as the event source lives. An analyzer could detect when an `[Owned]` object subscribes to an event without a corresponding unsubscription, but this is a resource-tracking problem, not a borrow-checking problem.

3. **Thread safety.** Events are frequently raised from threads different from the subscribing thread. Without the aliasing XOR mutability invariant, C# relies on conventions (null-check before raise, lock-free patterns using `Volatile.Read`) rather than compiler enforcement.

**Practical scope:** An analyzer should treat event subscription/unsubscription as an "unchecked" operation that exits the borrow-checked world, similar to how Rust treats `unsafe` blocks. Event-heavy code would simply not participate in ownership checking.

### Dependency Injection

**Sub-verdict: Viable with constraints**

DI containers manage object lifetimes using three scoping strategies:

1. **Singleton:** One instance for the entire application lifetime. Singletons are inherently shared -- multiple consumers hold references to the same instance. This conflicts with exclusive ownership but is safe from a lifetime perspective (the singleton outlives all consumers).

2. **Scoped:** One instance per scope (typically per HTTP request in ASP.NET). Scoped services are shared within a scope but not across scopes. A scoped service captured by a singleton is a well-known bug ("captive dependency") that existing analyzers already detect.

3. **Transient:** A new instance per injection. Transients are the closest to owned values -- each consumer gets an exclusive instance. But the DI container creates and injects them, so ownership transfer happens through the container's opaque infrastructure.

An analyzer could model DI lifetimes as follows:

- **Singleton:** treat as `[Shared]` -- read-only access, no mutation through the injected reference.
- **Scoped:** treat as `[Borrowed]` for the duration of the scope.
- **Transient:** treat as `[Owned]` by the consumer, with `IDisposable` tracking.

However, DI registration happens at runtime (or via source-generated code), not in the type system. The analyzer would need to either analyze the DI configuration (which is arbitrary C# code) or rely on conventions and attributes on service interfaces.

**Practical scope:** The analyzer could provide useful warnings for captive dependency and disposal issues but could not enforce full ownership discipline through DI-injected services without significant framework-specific logic.

### Entity Framework and ORMs

**Sub-verdict: Blocker for strict borrow-checking; viable for disposal tracking**

ORMs like Entity Framework are fundamentally built on shared mutable state:

1. **Change tracking.** The `DbContext` maintains internal references to every entity it has loaded. When you query for an entity, the returned object is tracked by the context -- there are at least two references (your variable and the context's identity map). Modifications to the entity through your variable are detected by the context for persistence.

2. **Navigation properties.** Entity relationships create reference graphs: `order.Customer.Orders` navigates through shared references. These graphs are inherently aliased and cannot satisfy exclusive-ownership constraints.

3. **Lazy loading.** With lazy loading, accessing a navigation property triggers a database query and materializes a new object, silently creating new references and aliases. This is incompatible with any static analysis that tracks reference creation.

4. **DbContext lifetime.** The `DbContext` itself is typically scoped to a unit of work (a request, a transaction). It implements `IDisposable` and is a prime target for disposal tracking. An analyzer could enforce that `DbContext` is used within a `using` block and not accessed after disposal -- this is valuable and tractable.

**Practical scope:** ORM-managed entities should be treated as "unchecked" types that do not participate in borrow-checking. The `DbContext` itself can be tracked for proper disposal. This is a significant carve-out -- in many applications, the majority of domain logic involves ORM entities.

### Builder Pattern, Fluent APIs, and Observer Pattern

**Sub-verdict: Viable with constraints**

1. **Builder pattern.** Fluent builders return `this` from each method call, creating a chain of references to the same mutable object. This is inherently aliased (each method call borrows and returns the same object). An analyzer could model fluent builders as a single continuous mutable borrow, with ownership transferring when `.Build()` is called. This requires the builder type to be annotated to indicate fluent-return semantics.

2. **Fluent APIs generally.** Any API that returns `this` for chaining creates the same pattern. LINQ-to-Objects materializes new collections at each step; LINQ-to-SQL/EF builds expression trees. The analyzer must distinguish between these cases.

3. **Observer pattern.** Observers register with a subject, creating shared references similar to events. The subject holds a list of observers; each observer may hold a reference to the subject. This is a many-to-many ownership problem. Like events, observer relationships should be treated as unchecked.

### Summary of Pattern Interactions

| Pattern | Compatibility with Borrow Checking | Practical Scope |
|---|---|---|
| **async/await** | Low -- state machine capture creates implicit aliasing; ref structs cannot cross await | Disposal tracking only |
| **LINQ/closures** | Medium -- deferred execution complicates lifetime tracking; materialization helps | Flag capture of owned/borrowed values; require annotations on LINQ methods |
| **Events/delegates** | Low -- inherently many-to-many aliased references | Treat as unchecked; track subscription lifetimes |
| **Dependency injection** | Medium -- singleton=shared, transient=owned, scoped=borrowed | Model DI lifetimes via conventions; detect captive dependencies |
| **Entity Framework/ORMs** | Very low -- change tracking and navigation properties are inherently shared mutable state | DbContext disposal tracking only; entities are unchecked |
| **Builder/fluent APIs** | Medium -- continuous mutable borrow; ownership transfers at `.Build()` | Requires annotation of fluent types |
| **Observer pattern** | Low -- many-to-many references | Treat as unchecked |

---

## 4. What Escape Hatches Are Needed for Interop with Unchecked C# Libraries?

**Verdict: Viable**

### Calling into Libraries Without Ownership Annotations

The .NET ecosystem contains millions of methods across the BCL, NuGet packages, and internal libraries, none of which carry ownership annotations. The analyzer must have a strategy for handling calls to unannotated code.

**Default assumptions for unannotated methods:**

1. **Parameters:** Assume all reference-type parameters are borrowed (the callee may read but the caller retains ownership). This is the safe default -- it does not require the caller to give up ownership, and it does not assume the callee takes ownership. If the callee actually stores the reference (violating the borrow assumption), the analyzer cannot detect this, but the GC prevents memory corruption.

2. **Return values:** Assume all returned reference-type values are owned by the caller. This is the conservative default -- the caller is responsible for disposal if the type is `IDisposable`.

3. **`ref` and `out` parameters:** Assume they are mutable borrows for the duration of the call. The analyzer can enforce that no other reference to the same object is live during the call.

4. **Thread safety:** Assume nothing about thread safety for unannotated types. Values from unchecked code that cross thread boundaries should require explicit synchronization or an `[Unsafe]` acknowledgment.

These defaults are analogous to NRT's treatment of "oblivious" code: unannotated types are neither checked nor assumed to be safe. The analyzer applies its rules to the checked side of the boundary and accepts weaker guarantees at the boundary.

### Trust Boundaries

The analyzer needs a concept of trust boundaries at three levels:

1. **Method-level.** A method annotated with `[UncheckedInterop]` (or similar) is exempt from ownership analysis. Its body can perform arbitrary aliasing, store references freely, and call unannotated methods without warnings. This is analogous to Rust's `unsafe` blocks.

2. **Type-level.** A type annotated with `[Unchecked]` does not participate in ownership tracking. All references to unchecked types are treated as if they were in the unchecked world. This is necessary for types like `DbContext`, `ILogger`, and other framework types where ownership discipline is neither practical nor useful.

3. **Assembly-level.** An entire assembly can be marked as unchecked (the default for assemblies without Cobalt annotations). All types and methods from such assemblies use the default assumptions described above.

The trust boundary model allows gradual adoption: a project can enable ownership checking for its own code while interoperating freely with unchecked libraries. This is exactly how NRT's gradual rollout worked, and it is the proven model for introducing new static analysis to an existing ecosystem.

### External Annotation Packs (DefinitelyTyped Model)

For commonly used libraries (the BCL, ASP.NET Core, Entity Framework, Newtonsoft.Json, etc.), the Cobalt project could provide external annotation packs -- NuGet packages containing ownership annotations for library types and methods, without modifying the libraries themselves.

This model has precedent:

- **TypeScript's DefinitelyTyped** provides type definitions for JavaScript libraries. It is community-maintained and covers thousands of packages.
- **JetBrains' External Annotations** provide nullability annotations for .NET libraries, consumed by ReSharper and Rider.
- **NRT's nullable attributes in the BCL** were added retroactively to existing APIs, demonstrating that annotation can be done incrementally.

The annotation pack approach is viable but labor-intensive. Key considerations:

1. **BCL coverage.** The BCL is large (thousands of public types, tens of thousands of public methods). Full annotation would be a major effort. However, a Pareto approach -- annotating the most commonly used 200-300 types -- would cover the majority of real-world usage.

2. **Versioning.** Annotations must be versioned alongside the library they describe. When a library updates and changes a method's ownership semantics, the annotation pack must be updated correspondingly.

3. **Accuracy.** External annotations carry the risk of being incorrect. If an annotation claims a method borrows a parameter but the method actually stores it, the analyzer will not detect the resulting ownership violation. Community review and testing mitigate this, but errors are inevitable.

4. **Discovery and distribution.** Annotation packs should be distributed as NuGet packages that are automatically referenced when the corresponding library is referenced. The Roslyn analyzer would look for annotation assemblies following a naming convention (e.g., `Cobalt.Annotations.System.Collections` for `System.Collections`).

### Assessment

The escape hatch design is the most tractable part of this assessment. The NRT rollout has demonstrated that gradual adoption with trust boundaries works in the .NET ecosystem. The default assumptions are conservative and safe (in the sense that they may produce false negatives -- missed violations -- but not false positives). External annotation packs require effort but follow proven patterns.

---

## Summary

### Verdict Table

| Question | Verdict | Key Finding |
|---|---|---|
| **1. Can Roslyn analyzers express ownership and borrowing rules?** | **Viable with constraints** | Intra-procedural analysis is strong. Inter-procedural analysis across assembly boundaries is infeasible; the system must be contract-based (relying on annotations). Diagnostics can be suppressed, so guarantees are advisory, not absolute. |
| **2. Can lifetime analysis work without language-level annotations?** | **Viable with constraints** | Inference covers simple patterns (estimated 60-70%). Parametric lifetime relationships between references are inexpressible in C#'s type system or attribute system. Ownership-mode attributes (`[Owned]`, `[Borrowed]`) are expressive enough for resource tracking but not for full lifetime polymorphism. |
| **3. How do existing C# patterns interact?** | **Viable with constraints (some sub-patterns are blockers)** | async/await and ORM patterns are fundamentally incompatible with strict borrow-checking. Events, DI, and closures can be handled with carve-outs. The practical scope narrows to: resource disposal tracking, single-method aliasing checks, and annotated ownership transfer. Large portions of typical C# codebases would be "unchecked." |
| **4. What escape hatches are needed?** | **Viable** | The NRT gradual-adoption model (oblivious defaults, trust boundaries, external annotations) is directly applicable and proven. Default assumptions for unannotated code are conservative and safe. |

### Overall Assessment

The Augmented C# approach can deliver **meaningful but limited** safety improvements. Its sweet spot is:

1. **Strengthened `IDisposable` tracking** -- enforcing that owned resources are disposed on all paths, preventing use-after-dispose, detecting ownership transfer of disposables. This extends existing CA2000/CA2213 analyzers with ownership semantics and is the highest-value, lowest-friction application.

2. **Move-semantic discipline for annotated types** -- preventing use-after-move for types explicitly marked `[Owned]`, detecting accidental aliasing of values intended to have single-owner semantics. Useful for resource handles, connection objects, and similar types.

3. **Scoped borrowing for `ref struct` and `Span<T>`** -- extending the existing ref-safety rules with additional checks for aliasing, building on the C# compiler's own borrow-checking infrastructure.

4. **Thread-safety annotations** -- flagging when a non-`[Sync]` type is shared across threads, similar to Rust's `Send`/`Sync` marker traits but advisory rather than enforced.

What the approach **cannot deliver**:

- **Compile-time proof of data-race freedom.** The aliasing XOR mutability invariant cannot be enforced without language-level support for exclusive references. C#'s reference model fundamentally assumes unrestricted aliasing.
- **Full lifetime safety.** Without named lifetime parameters in the type system, the analyzer cannot express or verify complex lifetime relationships.
- **Guarantees that cannot be bypassed.** Roslyn analyzer diagnostics can be suppressed. Any safety guarantee that is important enough to be a hard requirement must eventually be enforced by the compiler, not an analyzer.
- **Compatibility with pervasive C# patterns.** async/await, events, ORMs, and dependency injection all resist borrow-checking constraints. A strict analyzer would reject idiomatic C# code; a lenient one provides weaker guarantees.

### Recommendation

The Augmented C# approach should be pursued as a **pragmatic stepping stone**, not as a final solution. The Phase 1 prior art research (07-prior-art.md) reaches the same conclusion: "The Roslyn analyzer path ('augmented C#') can provide useful safety improvements, but it cannot achieve full borrow-checker guarantees due to fundamental limitations of the C# type system."

The recommended strategy is:

1. **Build the analyzer** targeting the high-value use cases (disposal tracking, move semantics, scoped borrowing). Ship it as a NuGet package that developers can adopt incrementally.

2. **Use the analyzer to validate Cobalt's ownership model** before committing to a full language implementation. The annotation attributes designed for the analyzer (`[Owned]`, `[Borrowed]`, `[MustDispose]`, etc.) will inform the syntax and semantics of the full Cobalt language.

3. **Accept that the analyzer is a ceiling, not a floor.** For full ownership and borrowing guarantees -- data-race freedom, lifetime safety, mandatory enforcement -- the "new language" approach (a Cobalt language with its own compiler targeting .NET IL) is required. The analyzer demonstrates the value proposition; the language delivers on it.

This two-phase strategy aligns with the lesson from Midori: incrementally adoptable improvements succeed where "replace the world" approaches fail. The analyzer earns developer trust and builds ecosystem awareness; the language provides the guarantees that the analyzer cannot.
