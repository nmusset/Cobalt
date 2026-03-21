# Phase 1.2: Ownership & Borrowing — Comparative Analysis

This document covers the second research topic from the Cobalt roadmap: move semantics, borrowing rules, the borrow checker, lifetimes, and their implications for a language targeting .NET with Rust-style ownership semantics.

---

## 1. Move Semantics

### Rust's Ownership Model and Move Mechanics

#### Single-Owner Invariant

Every value in Rust has exactly one owner at any point in the program. Ownership is a compile-time concept with no runtime representation — there is no reference count, ownership tag, or GC root associated with ownership itself. The compiler's borrow checker and move analysis together enforce the invariant statically.

When the owner goes out of scope, the value is dropped (its `Drop` implementation runs, if any, followed by recursive field drops). Because there is exactly one owner, there is exactly one drop — no double-free, no use-after-free, no dangling cleanup.

#### When Ownership Transfers (Moves)

Ownership transfers occur in three contexts:

1. **Assignment.** `let y = x;` — if `x`'s type does not implement `Copy`, ownership of the value moves from `x` to `y`. The binding `x` becomes uninitialized and cannot be used again unless reassigned.
2. **Function call (pass by value).** `consume(x);` — ownership of `x` moves into the function parameter. The caller can no longer use `x`.
3. **Return.** `return x;` — ownership moves from the function's local scope to the caller.

These are all instances of the same rule: when a place expression is evaluated in a value expression context, and the type does not implement `Copy`, the value is moved and the source is deinitialized.

#### What Happens at the Machine Level

A move is a **bitwise copy** of the value's bytes from the source to the destination, followed by the compiler treating the source as uninitialized. There is no runtime "move operation," no special instruction, and no overhead beyond the memcpy-equivalent. The source memory is not zeroed, cleared, or deallocated — the compiler simply prevents any subsequent access to it.

This is a critical distinction from C++ move semantics, where a move constructor runs user-defined code, can throw exceptions, and leaves the source in a "valid but unspecified state." Rust moves are unconditional bitwise copies with source invalidation — always zero-overhead beyond the copy, always statically checked.

For larger types, the compiler frequently optimizes the copy away entirely through **named return value optimization (NRVO)**: the value is constructed directly at the destination, and no copy occurs at all.

#### How the Compiler Tracks Ownership

The Rust compiler performs move analysis on **MIR** (Mid-level Intermediate Representation). It maintains **move paths** — a hierarchical data structure representing every location whose initialization state must be tracked. Move paths form a tree: for a struct `s` with fields `a` and `b`, the move paths are `s`, `s.a`, and `s.b`. The compiler tracks initialization state at each granularity independently.

At every point where a value is used, the compiler checks that the corresponding move path is in the "initialized" state. If not — because the value was previously moved — the compiler rejects the program.

#### Conditional Moves and Drop Flags

When control flow introduces ambiguity — a value is moved in one branch but not another — the compiler inserts **drop flags**: boolean values stored on the stack that track at runtime whether a particular value needs to be dropped. Drop flags are stack-allocated booleans — their overhead is minimal (one byte per conditionally-moved variable, one branch instruction at scope exit). In practice, the vast majority of Rust code does not trigger drop flag insertion.

#### Partial Moves

Rust allows moving individual fields out of a struct. After a partial move:

- The moved field is deinitialized and cannot be read.
- Other fields remain initialized and accessible.
- The struct as a whole cannot be used (passed, assigned, or matched completely).
- The struct can be made whole again by assigning a new value to the moved field.

Partial moves are **not permitted on types that implement `Drop`**, because `Drop::drop(&mut self)` expects all fields to be valid. The compiler prevents this statically.

#### Destructuring Moves

Pattern matching can move all fields simultaneously. After destructuring, the struct is fully deinitialized, and each field becomes an independent owned value. The `Drop` implementation does **not** run — the programmer has taken ownership of each field individually. This is sometimes used deliberately to prevent a destructor from running while still cleaning up each component.

### C#'s Lack of Move Semantics

C# has no concept of ownership transfer:

**Value types**: Assignment always produces a full, independent copy. Both source and destination are valid afterward. The compiler's definite assignment analysis is one-directional — once assigned, a variable remains assigned for the rest of its scope.

**Reference types**: Assignment copies the reference, not the object. Both references are valid, pointing to the same heap object. Multiple references are the norm.

C# has several features that superficially resemble aspects of move semantics:

- **`ref` parameters**: Pass a managed pointer to the caller's variable — aliasing, not ownership transfer.
- **`in` parameters**: Readonly ref — a performance optimization, not ownership semantics.
- **`out` parameters**: Callee must assign before returning — about initialization, not ownership.
- **`ref struct` and `Span<T>`**: Stack-only value types that cannot escape to the heap — about preventing escape, not transferring ownership. Still copied on assignment.
- **`scoped` modifier (C# 11)**: Constrains a reference so it cannot escape the method — a lifetime constraint, not an ownership one.

#### Why C# Cannot Express Moves

The CLR lacks the foundational concept: **variable deinitialization after use**. C#'s definite assignment analysis transitions variables from "not assigned" to "assigned" but never back. There is no IL instruction for "mark this local as invalid," no verifier rule for "reject reads of invalidated locals." Any move semantics on .NET must be implemented entirely by the language compiler, with no runtime safety net.

### Implementing Move Semantics on .NET

#### Option A: Compile-Time-Only Enforcement

The Cobalt compiler performs move analysis and rejects use-after-move programs. The emitted IL does nothing special — the moved-from variable still contains its old value at runtime.

- **Advantages**: Zero runtime cost. The JIT and GC behave optimally because the IL is conventional.
- **Disadvantages**: No defense in depth (compiler bugs leave the runtime unprotected). C# code consuming Cobalt types can freely access "moved" variables. No runtime assertions for debugging.

#### Option B: Runtime Clearing After Move

After a move, the Cobalt compiler emits IL that zeroes/clears the source variable (value types via `initobj`, reference types via storing `null`).

- **Advantages**: Defense in depth (moved references produce NullReferenceException, not stale data). Debugger-friendly. Helps the GC (cleared references release objects sooner).
- **Disadvantages**: Runtime cost (one write per move). Zeroing value types produces valid-looking default values rather than errors. Still not CLR-enforceable.

#### Option C: Hybrid Approach

Compile-time enforcement as primary mechanism. Debug builds emit runtime clearing. Types crossing the interop boundary always emit clearing.

**Additional techniques:**
- **Roslyn-style analyzers for interop consumers**: Cobalt could ship a .NET analyzer that warns C# consumers when they access Cobalt metadata-marked "moved" variables.
- **Sentinel patterns**: Type-specific "poisoned" states distinct from valid values.

#### The Struct Copy Problem at the Interop Boundary

A Cobalt value type exposed to C# will be freely copyable. C# has no mechanism to prevent copying a struct. Possible mitigations:
- Wrap move-only values in reference types (sacrifices value semantics).
- Encapsulate in ref structs (limits scope but still copies within that scope).
- Accept the limitation and document that move-only types are safe only within Cobalt code.

### Partial Moves on .NET

C# has no per-field initialization tracking after construction. The CLR treats structs as atomic units. If Cobalt supports partial moves, it must enforce them entirely at compile time. The emitted IL will contain a struct with all fields present and accessible — other .NET languages will see all fields as valid.

### Move Semantics — Summary

| Aspect | Rust | C# | Cobalt Design Challenge |
|--------|------|-----|------------------------|
| **Assignment of non-Copy types** | Move: source invalidated | Value types: copy. Reference types: reference copy | Must choose compile-time-only or runtime-clearing enforcement |
| **Use-after-move** | Compile-time error | Not applicable | Compiler must implement full move analysis; CLR provides no assistance |
| **Runtime cost of a move** | Zero (bitwise copy + static invalidation) | N/A (always copy or share) | Option A: zero cost. Option B: one write per move |
| **Conditional moves** | Drop flags (stack booleans) | N/A | Must implement equivalent drop-flag logic in IL |
| **Partial moves** | Supported for non-Drop types | Not supported | Must implement per-field tracking in the compiler |
| **Interop safety** | FFI boundary is explicitly unsafe | N/A | Other .NET code can freely copy/access "moved" values |

---

## 2. Borrowing Rules & The Borrow Checker

### The Aliasing XOR Mutability Invariant

Rust's borrowing system is built on a single fundamental invariant: at any given point in the program, for any piece of data, either multiple shared (read-only) references exist, or exactly one mutable (read-write) reference exists, but never both simultaneously.

#### Shared References (`&T`)

A shared reference `&T` grants read-only access. Any number of shared references to the same data may coexist simultaneously. The compiler guarantees that while any shared reference is live, no mutable reference to the same data can exist. Shared references are `Copy` — duplicating a `&T` is free and implicit. The guarantee they provide is **temporal stability**: the value behind the reference will not change while the reference is live (barring interior mutability).

#### Mutable References (`&mut T`)

A mutable reference `&mut T` grants exclusive read-write access. While live, no other reference — shared or mutable — to the same data may exist. Mutable references are **not** `Copy` and are moved by default.

#### The Invariant Stated Precisely

For any region of memory, at any point in execution:
1. **Zero or more `&T` exist**, and no `&mut T` exists. Data is frozen.
2. **Exactly one `&mut T` exists**, and no `&T` exists. Exclusive access.
3. **No references exist.** The owner may do anything.

### How the Borrow Checker Enforces These Rules

The borrow checker is a compile-time static analysis pass operating on MIR (the control-flow graph representation).

#### Non-Lexical Lifetimes (NLL)

NLL (RFC 2094, fully stabilized in Rust 1.63) changed how the compiler determines reference lifetimes. Instead of tying lifetimes to syntactic scopes (the original lexical approach), NLL derives lifetimes from the **control-flow graph**: a reference's lifetime extends only through points where it is "live" (might still be used).

Key changes:
1. **Lifetimes shrink to usage.** A mutable reference's lifetime ends at its last use, not at the enclosing block's end.
2. **Control-flow sensitivity.** The borrow checker understands branches — a reference used only in one branch doesn't block borrows in the other.
3. **Liveness-based analysis.** Two borrows conflict only if their liveness ranges overlap at a problematic program point.
4. **Improved error messages.** The compiler points to exact lines where borrows conflict.

#### The Polonius Project

Polonius is the next-generation borrow checking algorithm, still under active development. It tracks **origins** (where each reference came from) and accepts strictly more programs than NLL without sacrificing safety. The native implementation inside rustc handles almost all in-tree tests and passes crater runs (early 2026), available behind `-Zpolonius` on nightly, but performance is not yet satisfactory for default use. The motivating use case is lending iterators — iterators that yield references borrowing from the iterator itself.

### Reborrowing

Reborrowing is an implicit coercion that creates a new, shorter-lived reference from an existing reference, temporarily suspending the original. When you pass `&mut T` to a function expecting `&mut T`, Rust creates a reborrow rather than moving the reference. The reborrow borrows from the original reference, and while it's live, the original is suspended. Once the reborrow expires, the original becomes usable again.

Reborrowing happens implicitly in function calls, method calls, and coercion to shared reference (`&mut T` → `&T`). Without it, every function call taking `&mut T` would move the reference, making it unusable afterward.

### Two-Phase Borrows

Two-phase borrows (RFC 2025) permit expressions like `vec.push(vec.len())` by splitting a mutable borrow into two phases:

1. **Reservation phase**: The mutable borrow exists but hasn't been used for writing. Additional shared borrows are permitted.
2. **Activation phase**: When the mutable borrow is first used for mutation, standard exclusivity rules apply.

This is deliberately narrow in scope — it applies only to autoref-generated borrows in method calls, function arguments, and compound assignment operators.

### Interior Mutability

Interior mutability allows mutation through shared references (`&T`) in controlled circumstances, shifting enforcement from compile time to runtime or `unsafe` code.

**`UnsafeCell<T>`** — The only type through which it is legal to obtain a mutable pointer to data behind a shared reference. Foundation for all other interior mutability types.

**`Cell<T>`** — Copy-based interior mutability. Never gives out references to its interior; copies values in and out. Zero-cost at runtime. Not `Sync`.

**`RefCell<T>`** — Runtime-checked borrowing. Tracks active shared/mutable borrows with an internal counter. Panics on violation. Not `Sync`.

**`Mutex<T>` / `RwLock<T>`** — Thread-safe interior mutability using OS synchronization primitives. `Mutex` provides exclusive access; `RwLock` provides shared-read/exclusive-write. Both are `Sync`.

| Mechanism | Thread-safe | Cost | Failure mode |
|---|---|---|---|
| Static borrow checker | N/A (compile-time) | Zero | Compile error |
| `Cell<T>` | No | Zero | N/A (cannot fail) |
| `RefCell<T>` | No | Small (counter) | Panic at runtime |
| `Mutex<T>` | Yes | OS mutex overhead | Poisoning on panic |
| `RwLock<T>` | Yes | OS rwlock overhead | Poisoning on panic |

### C#'s Reference Model

C# takes a fundamentally different approach. There is no aliasing XOR mutability invariant — multiple mutable references to the same object are normal and expected.

**No aliasing restrictions.** Any number of variables can hold a reference to the same heap object and mutate it at any time. The GC handles memory safety; data-race prevention is left to the programmer.

**Parameter modifiers** provide control over how values are passed but enforce no aliasing restrictions:
- `ref`: managed pointer, read-write — roughly analogous to `&mut T` in capability but with no exclusivity guarantee.
- `in` / `ref readonly`: readonly managed pointer — closer to `&T` in intent but the caller can still mutate through other paths.
- `out`: callee must assign — about initialization, not ownership.

**`scoped` keyword (C# 11)** restricts the lifetime of a reference, preventing it from escaping the method. This is the closest C# comes to lifetime tracking but provides no aliasing restrictions or mutation discipline.

**How C# manages without borrow checking:**
1. **GC**: Prevents dangling references, use-after-free, double-free.
2. **Threading primitives as library concerns**: `lock`, `Monitor`, `Mutex`, etc. — optional, not compiler-enforced.
3. **Immutability as convention**: `readonly`, `init`, `record`, `IReadOnlyList` — encourage but don't deeply enforce immutability.
4. **Concurrent collections**: `ConcurrentDictionary`, `Channel<T>` — safe but opt-in.

### Borrowing — Implications for Cobalt on .NET

#### What a Borrow Checker Would Enforce

Since the GC already handles memory safety, a Cobalt borrow checker would primarily target:

1. **Data-race freedom** — The most compelling value proposition. The aliasing XOR mutability invariant would make data races impossible within Cobalt code. This is arguably even more valuable on .NET than in Rust, given .NET's heavy use for concurrent server applications.
2. **Mutation discipline** — Preventing aliased mutation eliminates iterator invalidation, inconsistent state from concurrent modification, and TOCTOU races.
3. **Lifetime safety for unmanaged resources** — Preventing use-after-dispose, replacing `IDisposable`/`using` with compile-time guarantees.

#### What GC Already Handles vs What a Borrow Checker Adds

| Concern | GC (.NET) | Borrow Checker (Rust) | Cobalt Opportunity |
|---|---|---|---|
| Dangling references | Prevented | Prevented | Already solved by GC |
| Use-after-free | Prevented | Prevented | Already solved by GC |
| Double-free | Prevented | Prevented | Already solved by GC |
| Data races | **Not prevented** | Prevented | **Key value-add** |
| Aliased mutation bugs | **Not prevented** | Prevented | **Key value-add** |
| Iterator invalidation | **Not prevented** (runtime exception) | Prevented | **Key value-add** |
| Use-after-dispose | **Not prevented** (runtime exception) | Prevented | **Significant value-add** |
| Thread-safety of shared state | **Programmer's responsibility** | **Compiler-enforced** | **Key value-add** |

#### Interaction with Existing .NET APIs

The entire .NET ecosystem assumes unrestricted aliasing. Strategies for interop:

1. **Boundary types**: Cobalt-native types with borrow-checking; explicit conversion at .NET boundary.
2. **Gradual adoption**: Annotate types with borrowing constraints (like NRT's gradual rollout). Unannotated .NET types treated as "unchecked."
3. **Effect system / color marking**: Distinguish "borrow-checked" vs "unchecked" code regions.
4. **Runtime-checked wrappers**: `RefCell`-like wrappers for .NET objects from unchecked code.

#### Design Recommendations

1. **Focus the borrow checker on mutation discipline and data-race freedom**, not memory management.
2. **Adopt `Send`/`Sync`-like marker traits** to classify types by thread-safety.
3. **Implement NLL-style lifetime analysis** for stack-allocated data and ref structs, building on C#'s `scoped` and ref-safety infrastructure.
4. **Provide interior mutability types** mirroring Rust's `Cell`/`RefCell`/`Mutex`/`RwLock`, built on .NET synchronization primitives.
5. **Use a gradual/opt-in model for .NET interop**, similar to NRT rollout.
6. **Support an `unsafe` block mechanism** for bypassing the borrow checker at interop boundaries.

---

## 3. Lifetimes

### Rust's Lifetime System

A lifetime in Rust is the span of program execution during which a reference is valid. Every reference has a lifetime, and the borrow checker verifies at compile time that no reference outlives the data it points to. Lifetimes are purely compile-time — they are erased before code generation with no runtime cost or representation.

Lifetimes describe *how long a reference is permitted to exist*, not *when memory is allocated or freed*.

#### Named Lifetime Parameters

When the compiler cannot determine lifetime relationships from context, the programmer provides explicit annotations: `fn longest<'a>(x: &'a str, y: &'a str) -> &'a str`. The annotations describe relationships the caller must satisfy. The actual concrete region `'a` is determined at each call site.

#### Lifetime Annotations on Language Constructs

- **Functions/methods**: Lifetime parameters in angle brackets annotating reference parameters and return types.
- **Structs**: A struct holding a reference must declare a lifetime parameter: `struct Excerpt<'a> { part: &'a str }`. The struct cannot outlive the referenced data.
- **Enums**: Same rules — `Option<&'a T>` carries the lifetime through the wrapper.
- **Impl blocks**: Must re-declare the type's lifetime parameters.
- **Trait definitions**: Can carry lifetime parameters; higher-ranked trait bounds (`for<'a>`) express universal quantification over lifetimes.

### Lifetime Elision Rules

Three inference rules reduce annotation burden:

1. **Each elided input lifetime becomes a distinct parameter.** `fn foo(x: &str, y: &str)` → `fn foo<'a, 'b>(x: &'a str, y: &'b str)`.
2. **If exactly one input lifetime exists, it's assigned to all outputs.** `fn first_word(s: &str) -> &str` → `fn first_word<'a>(s: &'a str) -> &'a str`.
3. **If one input is `&self` or `&mut self`, its lifetime is assigned to all outputs.** `fn name(&self) -> &str` → `fn name<'a>(&'a self) -> &'a str`.

A survey of the Rust standard library (RFC 141) found these rules cover 87% of function signatures that would otherwise need annotations. When elision leaves ambiguity, the compiler demands explicit annotations — flagging a genuine decision point.

### Lifetime Subtyping and Variance

**Outlives relationship**: `'a: 'b` means lifetime `'a` outlives `'b`. This is subtyping — a longer lifetime is a subtype of a shorter one.

**Variance in structs:**
- **Covariant**: `&'a T` is covariant in `'a` — a longer-lived reference can substitute for a shorter-lived one. Safe because shared references don't permit mutation.
- **Invariant**: `&'a mut T` is invariant in `'a` — no substitution allowed. Without this, you could store a short-lived value through a mutable reference expecting a longer lifetime, creating a dangling reference.
- **Contravariant**: `fn(&'a str)` is contravariant in `'a` — rare in practice.
- **Struct variance**: Determined by the most restrictive field usage. `PhantomData<T>` can influence variance without adding a real field.

### The `'static` Lifetime

`'static` is the longest possible lifetime. The critical distinction:
- `&'static T` — a reference valid for the entire program (string literals, leaked allocations).
- `T: 'static` — T contains no non-static references (it owns all its data). `String`, `Vec<u8>`, `Box<dyn Error>` all satisfy `T: 'static` despite being runtime-created and droppable.

`T: Send + 'static` is the standard requirement for spawning threads/tasks — the value must be self-contained and thread-safe.

### Lifetime Bounds on Generic Parameters

- `T: 'a` — all references within T must outlive `'a`. Trivially satisfied for owned types.
- `where 'a: 'b` — lifetime ordering constraints.
- Combined bounds: `T: Iterator<Item = &'a u32> + 'a` constrains both behavior and lifetime.

### Self-Referential Structs

Rust makes self-referential structs fundamentally difficult because:
1. **Lifetime declaration is impossible** — the reference would need to refer to "the lifetime of this struct instance," but that's not a named parameter.
2. **Move semantics invalidate the reference** — when a struct moves, its memory address changes, and there's no relocation hook.
3. **Initialization ordering** — the owner field must exist before the borrow, but once borrowed, the struct cannot move.

**`Pin<T>`** prevents moves after pinning (used for async state machines) but doesn't solve the general case. **`ouroboros`** and **`self_cell`** crates provide workarounds via mandatory heap allocation.

### .NET's Approach to Reference Validity

The GC ensures managed references are always valid — no dangling references. This makes Rust-style lifetimes unnecessary for memory safety. However, the GC does not prevent:
- **Use of disposed resources** — `ObjectDisposedException` is a runtime error, not a compile-time one.
- **Data races through aliased mutable access** — no compiler enforcement of exclusive access.
- **Logical lifetime violations** — pooled buffers used after return, scoped transactions used after completion.

### C#'s Ref Safety Rules (C# 11+)

C# has incrementally built a limited lifetime-tracking system focused on `ref` returns, `ref struct` types, and `Span<T>`. It uses **safe-context** and **ref-safe-context** scopes (four levels: declaration-block, function-member, return-only, caller-context) to determine where references can flow.

The `scoped` keyword restricts a parameter's safe-context to the current method.

**Comparison with Rust lifetimes:**

| Capability | Rust | C# Ref Safety |
|---|---|---|
| Named lifetime parameters | Yes (`'a`, `'b`) | No |
| Relationships between lifetimes | Yes (`'a: 'b`) | No (binary escape/no-escape) |
| Lifetimes on structs | Yes | No (ref struct has implicit scope) |
| Lifetime elision with fallback | Yes | No (rules are fixed) |
| Return lifetime tied to specific parameter | Yes | Partially (safe-context rules) |
| Variance tracking | Yes | No |

C#'s system is sufficient for `Span<T>` safety but cannot express relationships like "the return value borrows from the first parameter but not the second."

### Lifetimes — Implications for Cobalt

#### What Lifetimes on .NET Would Achieve

Since the GC prevents dangling references, Cobalt lifetimes would target:
- **Use-after-dispose prevention** — turning `ObjectDisposedException` into compile-time errors.
- **Data race prevention** — enforcing exclusive mutable access within Cobalt code.
- **Logical lifetime tracking** — pooled/scoped resources express lifetime constraints in their types.
- **Safe stack-allocated references** — extending C#'s ref safety with full lifetime annotations.

#### Encoding Lifetimes in .NET IL

**Option 1: Full erasure.** Checked at compile time, erased from IL. Simplest but means no cross-assembly lifetime checking and no interop safety.

**Option 2: Custom attributes (recommended).** Following the NRT precedent (`NullableAttribute`, `NullableContextAttribute`): zero runtime cost, full metadata preservation, cross-assembly visibility, graceful degradation. Would encode lifetime parameter declarations, annotations on parameters/returns, outlives relationships, and scoped constraints.

**Option 3: Modreqs/modopts.** Participate in type identity (modreqs change method signatures), causing interop problems.

**Option 4: Sidecar metadata.** Separate file alongside the assembly. Avoids metadata pollution but creates deployment dependency.

Custom attributes are the strongest candidate — the .NET ecosystem has established this pattern and tools know how to handle it.

#### Open Questions

1. **Lifetime inference scope.** Could Cobalt infer more aggressively than Rust, since lifetimes are for logical safety rather than memory safety?
2. **GC-managed lifetimes.** GC references are effectively `'static` — how does this interact with non-GC resources?
3. **Async and lifetimes.** C#'s async captures locals into heap-allocated state machines — how do lifetime annotations interact?
4. **Variance with GC.** All .NET class references are mutable by default — variance rules must account for this.
5. **BCL compatibility.** The BCL has no lifetime annotations — strategy needed for existing APIs (extern declarations, attribute overlays, conservative defaults).
6. **Self-referential types.** The GC updates references during compaction, making self-referential managed objects inherently possible on .NET — a significant difference from Rust.

---

## Sources

### Move Semantics
- [What is Ownership? — The Rust Programming Language](https://doc.rust-lang.org/book/ch04-01-what-is-ownership.html)
- [Ownership and moves — Rust By Example](https://doc.rust-lang.org/rust-by-example/scope/move.html)
- [MIR — Rust Compiler Development Guide](https://rustc-dev-guide.rust-lang.org/mir/index.html)
- [Drop Elaboration — Rust Compiler Development Guide](https://rustc-dev-guide.rust-lang.org/mir/passes/drop-elaboration.html)
- [C# Method Parameters — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/method-parameters)

### Borrowing & Borrow Checker
- [RFC 2094: Non-Lexical Lifetimes](https://rust-lang.github.io/rfcs/2094-nll.html)
- [NLL Fully Stable — Rust Blog](https://blog.rust-lang.org/2022/08/05/nll-by-default.html)
- [RFC 2025: Two-Phase Borrows](https://rust-lang.github.io/rfcs/2025-nested-method-calls.html)
- [Two-Phase Borrows — Rust Compiler Development Guide](https://rustc-dev-guide.rust-lang.org/borrow_check/two_phase_borrows.html)
- [Polonius Current Status](https://rust-lang.github.io/polonius/current_status.html)
- [Reborrowing in Rust — haibane_tenshi](https://haibane-tenshi.github.io/rust-reborrowing/)
- [std::cell Module Documentation](https://doc.rust-lang.org/std/cell/)
- [C# 11 Low-Level Struct Improvements — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-11.0/low-level-struct-improvements)
- [Managed Pointers, Span, ref struct, scoped — NDepend Blog](https://blog.ndepend.com/managed-pointers-span-ref-struct-c11-ref-fields-and-the-scoped-keyword/)

### Lifetimes
- [Lifetime Elision — The Rust Reference](https://doc.rust-lang.org/reference/lifetime-elision.html)
- [RFC 141: Lifetime Elision](https://rust-lang.github.io/rfcs/0141-lifetime-elision.html)
- [Subtyping and Variance — The Rustonomicon](https://doc.rust-lang.org/nomicon/subtyping.html)
- [Common Rust Lifetime Misconceptions (pretzelhammer)](https://github.com/pretzelhammer/rust-blog/blob/master/posts/common-rust-lifetime-misconceptions.md)
- [Roslyn Nullable Metadata Encoding](https://github.com/dotnet/roslyn/blob/main/docs/features/nullable-metadata.md)
- [C# Span Safety Specification](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-7.2/span-safety.md)
- [ouroboros crate](https://docs.rs/ouroboros/latest/ouroboros/attr.self_referencing.html)
- [self_cell crate](https://docs.rs/self_cell/latest/self_cell/)
