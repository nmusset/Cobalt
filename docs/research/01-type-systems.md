# Phase 1.1: Type Systems — Comparative Analysis

This document covers the first research topic from the Cobalt roadmap: a comparative analysis of C# and Rust type systems, identifying common ground, key differences, and implications for a language targeting .NET with Rust-style semantics.

---

## 1. Value Types and Reference Types

### C# Type System: The Value/Reference Dichotomy

C# enforces a strict, runtime-level split between **value types** and **reference types**. This distinction is not merely a language-level abstraction — it is baked into the Common Language Runtime (CLR) and the Common Intermediate Language (IL). Every type in .NET is definitively one or the other, and the runtime handles them differently at every level: allocation, assignment, parameter passing, method dispatch, and garbage collection.

#### Value Types

Value types store their data directly in the memory location associated with the variable. When a value type is assigned to another variable or passed to a method, the data is copied in full.

**Categories of value types:**

- **Primitive numeric types**: `int`, `long`, `float`, `double`, `decimal`, `byte`, `char`, `bool`, etc. These are aliases for CLR types (`System.Int32`, `System.Int64`, etc.) and all inherit from `System.ValueType`.
- **User-defined structs**: Declared with the `struct` keyword. Cannot participate in inheritance hierarchies (they are implicitly sealed), though they can implement interfaces. C# 10 introduced `record struct`, which provides value-based equality semantics and synthesized members while remaining a value type.
- **Enums**: Named constants backed by an integral type (default `int`, configurable to `byte`, `short`, `long`, etc.). C# enums are purely numeric labels — they cannot carry associated data. The `[Flags]` attribute enables bitwise combination but does not change the underlying representation.
- **Tuples**: `ValueTuple<T1, T2, ...>` types are value types (unlike the older `Tuple<T1, T2, ...>` which are reference types).

**Allocation behavior:**

- When used as local variables or method parameters, value types are typically allocated on the **stack** (or in CPU registers).
- When a value type is a field of a reference type (class), it is stored **inline** within the object's heap allocation — no separate allocation occurs.
- When a value type is an element of an array, all elements are stored contiguously in a single heap allocation. Arrays themselves are reference types.

**Struct restrictions:**

- Cannot be inherited from or serve as a base type (implicitly sealed).
- Cannot declare a parameterless constructor prior to C# 10 (the runtime always provides a default zero-initialization constructor). C# 10 lifted this restriction.
- Default assignment zero-initializes all fields.
- The CLR controls layout but allows `[StructLayout]` attributes for interop: `Sequential` (default in C#), `Explicit` (manual field offsets with `[FieldOffset]`), and `Auto` (CLR reorders for optimal packing).

#### Reference Types

Reference types store a **reference** (a managed pointer) in the variable's memory location. The actual object data lives on the **managed heap** and is subject to garbage collection.

**Categories of reference types:**

- **Classes**: The primary reference type. Supports single inheritance, virtual methods, and full object-oriented semantics. The CLR stores an **ObjectHeader** (sync block index for locking, hash code caching) and a **MethodTable pointer** (for virtual dispatch and type identification) alongside every heap-allocated object instance.
- **Interfaces**: Define contracts. Cannot contain instance state (though C# 8 introduced default interface methods with implementations). An interface reference always points to a heap-allocated object.
- **Delegates**: Type-safe function pointers. Each delegate instance is a heap-allocated object containing the target method and (for instance methods) the target object reference.
- **Strings**: Immutable reference types (`System.String`). Always heap-allocated. Interning can reduce duplication but does not change the fundamental allocation model.
- **Arrays**: Always reference types, always heap-allocated, even arrays of value types (e.g., `int[]` is a heap object containing contiguous `int` values).
- **Records** (without `struct`): `record` and `record class` declarations produce reference types with value-based equality semantics. They remain heap-allocated with full GC management.

#### Boxing and Unboxing

When a value type must be treated as a reference type (e.g., assigned to a variable of type `object`, passed to a method expecting `object`, or used through an interface reference), the CLR performs **boxing**: it allocates a new object on the heap, copies the value type data into it, and returns a reference to the boxed copy. **Unboxing** extracts the value back, requiring a type check and a copy.

**Performance implications of boxing:**

- **Heap allocation**: Every boxing operation allocates a new object. A boxed `int` (4 bytes of data) requires an additional ~16 bytes of object overhead (ObjectHeader + MethodTable pointer + alignment), plus the GC tracking cost.
- **GC pressure**: Frequent boxing in tight loops creates significant garbage collection load.
- **Cache disruption**: Boxed values lose the cache-locality benefits of stack allocation.
- **Implicit occurrence**: Boxing can happen silently — string interpolation with value types, passing structs to non-generic collections, calling `ToString()` or `GetHashCode()` on a struct that doesn't override them (dispatching through `System.ValueType` requires boxing in some cases).

The introduction of generics in .NET 2.0 eliminated most boxing in collection scenarios (`List<int>` vs the old `ArrayList`), but boxing remains a concern in interface dispatch, reflection, and dynamic scenarios.

#### Ref Structs and Span\<T\>

C# 7.2 introduced **ref structs** — value types with a compiler-enforced guarantee that they can never escape to the heap. This is the closest C# comes to Rust's stack-discipline guarantees.

**Constraints on ref structs (compiler-enforced):**

- Cannot be used as array element types.
- Cannot be fields of classes or non-ref structs.
- Cannot be boxed (attempting to box a ref struct is a compile-time error).
- Cannot be captured by lambda expressions or local functions.
- Cannot be used as generic type arguments (relaxed in C# 13 with `allows ref struct` anti-constraint).
- Cannot be used across `await` boundaries in async methods (relaxed slightly in C# 13, but still cannot be live across an `await` point).
- Cannot implement interfaces (relaxed in C# 13, but with restrictions: the ref struct can implement the interface but cannot be used through the interface reference in ways that would box it).

`Span<T>` and `ReadOnlySpan<T>` are the flagship ref struct types. They provide a safe, bounds-checked view over contiguous memory (stack-allocated buffers, array slices, native memory) without heap allocation. `Span<T>` internally holds a managed pointer (`ref T`) and a length — a representation that is inherently incompatible with heap storage because managed pointers can become invalid if the GC moves objects while the span is not properly tracked.

**Relevance to Cobalt:** Ref structs represent .NET's partial acknowledgment that stack-only lifetime guarantees are valuable. They are the closest existing .NET mechanism to Rust-style borrow scoping, but they are opt-in, limited to specific type declarations, and cannot express the full generality of Rust's lifetime system.

### Rust Type System: The Uniform Value Model

Rust does not have C#'s value/reference split. Instead, Rust has a **uniform value model**: all types are values by default. Every binding owns its value. The distinction between stack and heap allocation is orthogonal to the type system — it is determined by how the value is stored, not by what type it is.

#### Default Behavior: Stack Allocation and Ownership

When a variable is declared in Rust, its value is placed on the **stack** by default, regardless of whether it is a primitive, a struct, an enum, or a tuple. Every value has exactly one owner. When the owner goes out of scope, the value is dropped (its `Drop` implementation runs, if any, and its memory is reclaimed).

**Rust's struct types** are the primary mechanism for composite data:

- Structs can have named fields, tuple-style fields, or no fields (unit structs).
- Structs cannot inherit from other structs. Composition and trait implementation replace inheritance.
- Structs can implement any number of traits (Rust's equivalent of interfaces).
- Struct layout is not guaranteed by default (the compiler may reorder fields for optimal alignment), but `#[repr(C)]` forces C-compatible sequential layout, and `#[repr(packed)]` or `#[repr(align(N))]` provide further control.

#### Move vs Copy Semantics

By default, assigning a Rust value to another variable or passing it to a function **moves** the value: ownership transfers, and the original binding becomes invalid. This is a compile-time concept — no runtime cost is incurred for the move itself (it is a bitwise copy at the machine level, with the compiler invalidating the source).

**The `Copy` trait** opts a type into implicit copy-on-assignment semantics. When a type implements `Copy`, assignment duplicates the value and both bindings remain valid. `Copy` is only available for types that can be safely duplicated with a simple bitwise copy — types that do not own heap resources or other non-trivially-copyable state.

Types that implement `Copy`:
- All primitive numeric types (`i32`, `f64`, `bool`, `char`, etc.).
- Tuples of `Copy` types.
- Fixed-size arrays of `Copy` types.
- Shared references (`&T`).
- Raw pointers.
- User-defined structs and enums where all fields are `Copy` (requires explicit `#[derive(Copy, Clone)]`).

Types that do **not** implement `Copy` (and therefore move):
- `String`, `Vec<T>`, `HashMap<K, V>` — any type that owns heap-allocated data.
- `Box<T>`, `Rc<T>`, `Arc<T>` — smart pointers.
- Any type containing a non-`Copy` field.
- Any type that implements `Drop` (the `Copy` and `Drop` traits are mutually exclusive — if a type needs custom cleanup logic, it cannot be trivially copied).

**The `Clone` trait** provides explicit duplication via `.clone()`. Unlike `Copy`, `Clone` can perform deep copies involving heap allocation. All `Copy` types automatically implement `Clone`, but not vice versa. `Clone` is always explicit — the programmer must call `.clone()` — which makes the cost visible in the source code.

#### Heap Allocation via Smart Pointers

Rust provides explicit opt-in mechanisms for heap allocation:

- **`Box<T>`**: Single-owner heap allocation. `Box::new(value)` moves `value` to the heap and returns an owning pointer. When the `Box` goes out of scope, the heap allocation is freed (via `Drop`). `Box<T>` has no runtime overhead beyond the allocation itself — it is a thin pointer, the same size as a raw pointer.
- **`Rc<T>`**: Reference-counted shared ownership, single-threaded. Multiple `Rc` pointers can point to the same heap-allocated value. The value is dropped when the last `Rc` is dropped. `Rc` does not implement `Send` or `Sync` — it is not thread-safe. Cloning an `Rc` increments the reference count (cheap) without cloning the underlying data.
- **`Arc<T>`**: Atomically reference-counted shared ownership, thread-safe. Identical to `Rc` in semantics but uses atomic operations for the reference count, making it safe to share across threads. The atomic operations incur a small performance cost compared to `Rc`.
- **`Cow<'a, T>`**: Clone-on-write. Holds either a borrowed reference or an owned value. Defers cloning until mutation is actually needed.

The key insight is that heap allocation in Rust is always **explicit and visible** in the type signature. There is no hidden heap allocation behind an innocent-looking variable declaration. If a type is heap-allocated, the programmer knows because `Box`, `Rc`, or `Arc` appears in the type.

### Struct Semantics: A Direct Comparison

| Aspect | C# Struct | Rust Struct |
|--------|-----------|-------------|
| **Assignment** | Always copies all fields (bitwise copy) | Moves by default; copies only if `Copy` is implemented |
| **Inheritance** | Cannot inherit or be inherited; can implement interfaces | Cannot inherit or be inherited; can implement traits |
| **Default initialization** | Zero-initialized by default (all fields set to 0/null/false) | No default initialization; all fields must be explicitly initialized (unless `Default` trait is implemented and invoked) |
| **Destructors** | No destructor (structs cannot have finalizers; `IDisposable` is possible but not automatic) | `Drop` trait provides deterministic, automatic cleanup when value goes out of scope |
| **Heap escapability** | Can be boxed to heap; can be a field in a class | On stack by default; only reaches heap via explicit `Box`/`Rc`/`Arc` |
| **Layout** | `Sequential` by default (for interop); `Auto` or `Explicit` via attributes | Unspecified by default (compiler may reorder); `#[repr(C)]` for sequential |
| **Size constraints** | Microsoft guidelines recommend <=16 bytes for structs (performance heuristic, not enforced) | No size guidelines; large structs are moved efficiently (bitwise copy + source invalidation) |
| **Mutability** | Fields mutable by default; `readonly struct` makes all fields immutable | Fields immutable by default; `mut` binding makes all fields mutable |
| **Self-referencing** | A struct cannot contain a field of its own type (infinite size), but can contain a reference to its own type via boxing | A struct cannot contain a field of its own type; can via `Box<Self>` |
| **Generic behavior** | .NET reifies generics — each value type instantiation gets specialized JIT code, avoiding boxing | Monomorphization — each generic instantiation produces separate machine code at compile time |

The most consequential difference is assignment semantics. In C#, every struct assignment is a full copy, which is why Microsoft recommends keeping structs small. In Rust, assignment is a move — the source is invalidated, and no duplication occurs. This makes large structs in Rust free to move without performance penalty (the bitwise copy still happens, but the compiler can often optimize it away entirely via named return value optimization or by constructing the value in-place at the destination).

### Key Architectural Differences

#### The C# Class/Struct Duality vs Rust's Uniform Model

C# forces a **design-time decision** between value type and reference type semantics. Choosing `struct` vs `class` has cascading implications: allocation strategy, assignment semantics, equality behavior, inheritance capabilities, and GC interaction. This decision is difficult to change later without breaking API consumers.

Rust sidesteps this by making all types values and providing **explicit, composable mechanisms** for heap allocation (`Box`), shared ownership (`Rc`/`Arc`), and interior mutability (`Cell`/`RefCell`/`Mutex`). The programmer assembles the desired semantics from orthogonal building blocks rather than choosing from a fixed menu.

#### Deterministic Destruction

Rust's `Drop` trait provides automatic, deterministic cleanup tied to scope exit. This is fundamental to RAII (Resource Acquisition Is Initialization) and enables safe, leak-free resource management without a garbage collector.

C#'s `IDisposable`/`using` pattern provides deterministic cleanup for reference types, but it is **opt-in and manually enforced**. Forgetting a `using` statement is a bug that the compiler does not catch (though analyzers can warn). Finalizers (`~ClassName()`) provide a safety net for reference types but run non-deterministically on the GC finalizer thread. Value types in C# cannot have finalizers at all; they can implement `IDisposable` but cannot be used with `using` unless boxed or handled carefully.

#### Nullability

C# reference types are nullable by default (though C# 8's nullable reference types add compile-time annotations to track this). Value types are non-nullable unless wrapped in `Nullable<T>` (`T?`). Boxing a `Nullable<T>` with no value produces a null reference — a subtle semantic bridge between the two type worlds.

Rust has no null. `Option<T>` is a regular enum (`Some(T)` or `None`) with no special runtime representation except for niche optimizations. This is uniform across all types — there is no separate nullability story for "stack types" vs "heap types."

#### Generic Specialization and Boxing Avoidance

.NET generics are reified: the JIT compiler generates specialized code for each value type instantiation (e.g., `List<int>` gets different machine code than `List<string>`). This avoids boxing in generic contexts but means generic code over value types still cannot use inheritance-based polymorphism without boxing.

Rust's monomorphization achieves a similar result (specialized code per type parameter) but goes further: because all types are values, there is never a question of boxing in generic code. Trait objects (`dyn Trait`) provide dynamic dispatch when needed, using a vtable pointer alongside the data — conceptually similar to boxing but explicit and distinct from the default.

### Reconciliation Challenges for Cobalt

A language targeting .NET with Rust-style semantics must confront the fact that the CLR's type system is fundamentally built around the value/reference dichotomy. The following are the critical friction points.

#### The IL-Level Type Distinction Is Not Erasable

The CLR distinguishes value types and reference types at the IL instruction level. Reference types are allocated with `newobj` (which invokes a constructor and returns a heap reference). Value types are initialized with `initobj` (which zero-fills a stack-local or inline location) or constructed with a `call` to a constructor on an already-allocated location. The JIT compiler, GC, and runtime type system all depend on knowing whether a type is a value type or reference type. **This distinction cannot be papered over by a compiler frontend** — Cobalt must decide, for every type it emits, which .NET category it belongs to.

#### Mapping Rust-Style Ownership onto .NET Types

**Value types** are a natural fit for owned, stack-allocated data with move/copy semantics. A Cobalt compiler could emit structs for types that are stack-allocated and implement move semantics by generating IL that clears or invalidates the source after a move (though the CLR does not enforce this — it would be a compile-time-only guarantee, with no runtime safety net if the invariant is violated through reflection or interop).

**Reference types** are required for any data that needs: polymorphism (virtual dispatch), identity-based equality, participation in inheritance hierarchies, or GC-managed lifetime. A Cobalt type that needs to be heap-allocated (equivalent to `Box<T>` in Rust) would most naturally map to a .NET class. But .NET classes are always GC-managed — there is no way to express "this heap object is uniquely owned and should be freed when its owner dies" in IL. The GC will collect it whenever it determines there are no more references, which could be later than expected (especially in debug builds where the JIT extends variable lifetimes).

#### The Ref Struct Opportunity and Its Limits

Ref structs are the most promising existing .NET feature for Cobalt's purposes. They provide compiler-enforced stack-only semantics, which aligns well with Rust's borrow model. A Cobalt compiler could potentially:

- Emit borrowed references as `ref` parameters or `Span<T>`-style ref struct wrappers.
- Use ref struct constraints to enforce that borrows do not outlive their referent.
- Leverage `scoped` parameter annotations (C# 11) to express lifetime constraints.

However, ref structs have significant limitations that do not exist in Rust:
- They cannot be stored in fields of heap-allocated objects (no equivalent of Rust's lifetime-parameterized structs that store references).
- They cannot be used as generic type arguments in most contexts (the `allows ref struct` anti-constraint in C# 13 partially addresses this, but the feature is narrowly scoped).
- They cannot cross `await` boundaries, which severely limits their use in async code.
- The .NET lifetime model is much simpler than Rust's — there is no equivalent of named lifetime parameters (`'a`, `'b`) or lifetime subtyping.

#### Boxing as a Semantic Barrier

In Rust, moving a value to the heap (`Box::new(x)`) is explicit, and the resulting `Box<T>` is a different type from `T`. In C#, boxing is implicit and produces a `System.Object` — the original type information is erased at the variable level (though retained in runtime metadata). A Cobalt language would need to either:

- Prevent implicit boxing entirely (breaking compatibility with .NET APIs that expect `object`).
- Make boxing explicit in the type system (similar to Rust's `Box<T>`) while providing automatic bridging for .NET interop boundaries.
- Accept boxing as an unsafe operation that exits the ownership-tracked world.

#### Enum Representation

Rust-style enums with data require a .NET representation. The options are:

- **Value-type tagged union**: Emit a struct with a discriminant field and a union-like layout (`[StructLayout(Explicit)]` with overlapping fields). This preserves value semantics and stack allocation but is tricky to make type-safe at the IL level and interacts poorly with GC (fields containing managed references cannot overlap).
- **Class hierarchy**: Emit a sealed abstract base class with a nested class per variant. This is idiomatic .NET but forces heap allocation for every enum instance, defeating Rust's value semantics.
- **Hybrid**: Use value-type representation for enums whose variants contain only unmanaged data, and class hierarchies for enums with managed references in variants. This is pragmatic but introduces a semantic split that Cobalt would need to explain.

#### The GC Interaction Problem

Rust's ownership model eliminates the need for a garbage collector. .NET fundamentally relies on one. A Cobalt type that is compile-time ownership-tracked still lives in a GC-managed runtime. This creates several tensions:

- **Deterministic drop timing**: Rust's `Drop` runs at a precisely known point. .NET's GC runs whenever it decides to. A Cobalt compiler can emit `IDisposable` and `using` blocks to simulate deterministic cleanup, but this is a codegen pattern rather than a runtime guarantee. Other .NET code interacting with Cobalt objects will not automatically respect this discipline.
- **Shared ownership**: `Rc<T>` and `Arc<T>` in Rust use reference counting because there is no GC. On .NET, reference counting is redundant — the GC already tracks reachability. A Cobalt equivalent might simply use regular .NET references for shared ownership, accepting that cleanup timing is non-deterministic.
- **GC pressure from ownership-tracked types**: Even if a Cobalt compiler tracks ownership, the runtime still treats class instances as GC objects. The ownership information can reduce unnecessary allocations (by preferring structs and stack allocation), but cannot make the GC aware of ownership semantics.

#### Interop Boundary Semantics

Any Cobalt type exposed to or consumed from C# must conform to .NET's type system rules. This means:

- A Cobalt type that is a value type in IL will be freely copyable by C# code, potentially violating move semantics.
- A Cobalt type that is a reference type in IL will be subject to GC timing, potentially violating deterministic drop expectations.
- Lifetime-constrained references have no representation in .NET metadata that C# can understand or respect.

The interop boundary will likely require a "trust boundary" concept: inside Cobalt code, the compiler enforces ownership and borrowing; at the boundary with C#, ownership annotations are erased or converted to runtime checks, and the programmer accepts weaker guarantees.

### Value/Reference Types — Summary

| Dimension | C# | Rust | Cobalt Implication |
|-----------|-----|------|--------------------|
| Type categorization | Binary: value type or reference type, decided at declaration | Uniform: all types are values; heap allocation is opt-in and explicit | Cobalt must pick a .NET type category for each type but could hide this choice behind Rust-like syntax |
| Stack vs heap | Determined by type category (struct = stack-inline, class = heap) | Determined by usage (`T` = stack, `Box<T>` = heap) | Could use structs by default and emit classes only when heap allocation is explicitly requested |
| Assignment | Structs: copy. Classes: reference copy | Move by default; copy only with `Copy` trait | Move semantics enforced at compile time; IL-level enforcement limited to clearing source variables |
| Destruction | Non-deterministic GC for classes; no destructors for structs; `IDisposable` is opt-in | Deterministic `Drop` at scope exit | Emit `IDisposable` + `using` patterns; accept that .NET interop weakens the guarantee |
| Enums | Integer labels only | Algebraic data types (tagged unions) | Must choose between struct-based tagged unions and class hierarchies per enum, depending on variant content |
| Nullability | Nullable reference types (annotations); `T?` for value types | `Option<T>` enum; no null | Could map `Option<T>` to `Nullable<T>` for value types and nullable references for reference types, or emit as a genuine enum |
| Generic boxing | Avoided via reification for value types | N/A (no boxing concept) | .NET reification is an advantage here — Cobalt can leverage it directly |

---

## 2. Generics

### C#/.NET Reified Generics

.NET generics are *reified*: generic type information is preserved at runtime rather than erased (as in Java) or expanded away (as in C++ templates or Rust). The CLR's type system natively understands generic type definitions and constructs closed generic types on demand.

**Runtime representation.** The JIT compiler treats reference-type and value-type instantiations differently:

- **Reference types.** All closed constructions of a generic type with reference-type arguments (e.g. `List<string>`, `List<object>`, `List<Stream>`) share a single JIT-compiled body. Internally, the CLR substitutes `System.__Canon` for every reference-type parameter and dispatches through a shared method table. Because all references are pointer-sized, field layout and calling conventions are identical regardless of the actual reference type.
- **Value types.** Each distinct value-type argument (e.g. `List<int>`, `List<Guid>`) produces a separately JIT-compiled specialization. This is necessary because value types differ in size, alignment, and field layout, which affects stack frame sizes, register allocation, and struct-passing conventions. The result is a limited form of monomorphization within the CLR, but driven by the JIT at runtime rather than by the ahead-of-time compiler.

**Reflection and runtime type queries.** Because generic type information survives to runtime, code can call `typeof(T)`, inspect `Type.GetGenericArguments()`, construct new closed generic types via `Type.MakeGenericType(...)`, and invoke generic methods via reflection. This is fundamental to many .NET patterns: serialization, dependency injection, ORM mapping, and expression tree compilation all rely on runtime generic type identity.

**Constraint system.** C# generic constraints (the `where` clause) restrict which type arguments are valid. The full list as of C# 13 / .NET 9:

| Constraint | Meaning |
|---|---|
| `where T : struct` | T must be a non-nullable value type |
| `where T : class` | T must be a reference type (non-nullable in nullable context) |
| `where T : class?` | T must be a reference type (nullable or non-nullable) |
| `where T : notnull` | T must be a non-nullable type (value or reference) |
| `where T : unmanaged` | T must be an unmanaged type (no GC references, recursively) |
| `where T : new()` | T must have a public parameterless constructor |
| `where T : BaseClass` | T must derive from (or be) the specified base class |
| `where T : IInterface` | T must implement the specified interface |
| `where T : U` | T must derive from (or be) another type parameter U |
| `where T : default` | Disambiguates `struct`/`class` in override or explicit interface scenarios |
| `where T : allows ref struct` | T may be a `ref struct` (an "anti-constraint" that widens rather than narrows) |

Constraints are enforced at compile time by the C# compiler and again at runtime by the CLR verifier. They are encoded in IL metadata and are therefore part of the binary contract.

**Key limitation of C# constraints.** Constraints can only express "T implements interface X" or "T has a base class Y." They cannot express:
- "T has a specific static method" (partially addressed by `static abstract` interface members in C# 11, but only through interfaces)
- "T supports operator +" (addressed in C# 11 via `IAdditionOperators<T,T,T>` and related generic math interfaces, but it requires the type to explicitly implement the interface)
- Conditional implementation: "this type implements interface X only when its type parameter satisfies constraint Y" (no equivalent)
- Negative constraints: "T is not a reference type" (only the positive `struct` or `unmanaged` constraints exist)

**Variance.** C# supports declaration-site variance on generic interfaces and delegate types:
- **Covariance** (`out T`): the type parameter appears only in output positions. `IEnumerable<out T>` is covariant, so `IEnumerable<string>` is assignable to `IEnumerable<object>`.
- **Contravariance** (`in T`): the type parameter appears only in input positions. `Action<in T>` is contravariant, so `Action<object>` is assignable to `Action<string>`.

Variance has several restrictions:
- Only interfaces and delegates may be variant; classes and structs are always invariant.
- Variance applies only to reference-type arguments. `IEnumerable<int>` cannot be treated as `IEnumerable<object>` because `int` is a value type (boxing would be required).
- A type parameter cannot appear in both covariant and contravariant positions within the same interface (unless wrapped in another variant type).

### Rust Monomorphized Generics

Rust generics are *monomorphized*: the compiler generates a separate copy of each generic function or type for every distinct set of concrete type arguments used in the program. There is no runtime concept of a "generic type" — after compilation, only concrete types exist in the binary.

**Compile-time expansion.** When Rust compiles `Vec<i32>` and `Vec<String>`, it produces two completely independent types with separate method bodies, vtables, and type metadata. This is conceptually similar to C++ template instantiation. The compiler can inline, constant-fold, and optimize each instantiation independently, producing code that is as efficient as hand-written type-specific code.

**No runtime type information.** Rust has no reflection system. There is no way to query a value's type at runtime, construct a generic type dynamically, or enumerate the fields of a struct through a runtime API. The `TypeId` mechanism offers limited runtime type identity (for `'static` types only) but nothing comparable to .NET reflection. All generic dispatch is resolved at compile time.

**Trait bounds.** Where C# uses constraints, Rust uses *trait bounds* to specify requirements on type parameters:

```
fn process<T: Clone + Debug + PartialOrd>(item: T) { ... }
fn process<T>(item: T) where T: Clone + Debug + PartialOrd { ... }  // equivalent
```

Trait bounds can express:
- That a type implements one or more traits (analogous to C# interface constraints)
- Lifetime requirements: `T: 'a` means T must be valid for lifetime `'a`
- Higher-ranked trait bounds (HRTBs): `for<'a> T: Fn(&'a str)` means "for all lifetimes `'a`, T implements `Fn(&'a str)`"
- Compound bounds with `+`: `T: Read + Write + Send + 'static`

**Conditional trait implementations.** A major expressiveness advantage over C#. Rust allows `impl` blocks to carry their own trait bounds independently of the type definition:

```
impl<T: Display> ToString for Vec<T> { ... }
```

This says "`Vec<T>` implements `ToString` only when `T` implements `Display`." The generic type `Vec<T>` exists for all `T`, but the `ToString` implementation is conditionally available. C# has no equivalent — a generic class either implements an interface for all type arguments or for none.

**Associated types.** Traits can declare associated types, which are type-level outputs determined by the implementing type:

```
trait Iterator {
    type Item;
    fn next(&mut self) -> Option<Self::Item>;
}
```

The key distinction from generic parameters on traits: an associated type is uniquely determined per implementation. A type can implement `Iterator` only once, and that implementation fixes `Item`. By contrast, a hypothetical `trait Iterator<Item>` would allow a type to implement `Iterator<i32>` and `Iterator<String>` simultaneously, creating ambiguity. Associated types eliminate this ambiguity and simplify downstream code.

C# has no direct equivalent to associated types. The closest approximation is using a generic interface with a constrained relationship, but this still permits multiple implementations and requires callers to specify the associated type explicitly.

**Const generics.** Rust supports compile-time constant values as generic parameters:

```
struct Array<T, const N: usize> {
    data: [T; N],
}
```

As of early 2026, stable Rust supports const generics with integer types (`usize`, `i32`, `bool`, `char`, etc.). More advanced features — const generic expressions, struct/enum const parameters, and associated const generics — are still in development.

C# has no equivalent to const generics. Array sizes, buffer lengths, and similar compile-time constants cannot be parameterized through the type system. The typical workaround is runtime values or, for fixed-size buffers, using `fixed` arrays or `System.Runtime.CompilerServices.InlineArray` (introduced in .NET 8).

**`dyn Trait` — opting into dynamic dispatch.** When monomorphization is undesirable (e.g. to reduce binary size or enable heterogeneous collections), Rust provides trait objects via `dyn Trait`. A `dyn Trait` value is a fat pointer: one pointer to the data and one pointer to a vtable containing the trait's method implementations. This is semantically similar to calling through an interface reference in C#, but it is opt-in and explicit rather than the default.

### Key Trade-offs: Monomorphization vs Reification

| Dimension | Monomorphization (Rust) | Reification (.NET) |
|---|---|---|
| **Runtime dispatch** | Static dispatch by default; each instantiation is direct calls and inlinable. Dynamic dispatch (`dyn Trait`) is opt-in. | Reference-type instantiations use shared code with indirect dispatch through shared method tables. Value-type instantiations are specialized (effectively monomorphized by the JIT). |
| **Code size** | Can cause *monomorphization bloat*: N concrete types produce N copies of every generic function they use. Large generic-heavy libraries (e.g. `serde`) are a known contributor to Rust binary sizes and compile times. | Compact for reference types (one shared body). Value-type specializations add some code, but JIT compilation means unused instantiations are never generated. |
| **Compile time** | Monomorphization is a major contributor to Rust compile times. Every instantiation must be independently type-checked, optimized, and codegen'd. | Generics add minimal compile-time cost. The JIT bears the specialization cost at runtime (or at AOT time with NativeAOT, but even then sharing for reference types limits the expansion). |
| **Runtime flexibility** | None. Generic types do not exist at runtime. Cannot construct new instantiations, reflect on type arguments, or serialize/deserialize based on runtime type information without explicit manual plumbing. | Full. Runtime reflection, dynamic type construction, and generic method invocation are first-class capabilities. |
| **Optimization potential** | Maximum. The compiler sees the concrete types and can inline, devirtualize, specialize branches, and eliminate dead code per instantiation. | Good for value types (JIT specializes). Limited for reference types (shared code cannot make type-specific optimizations). |
| **Cache behavior** | More code copies can increase instruction cache pressure if many instantiations are hot simultaneously. | Shared code for reference types is cache-friendly. Fewer distinct code addresses means better I-cache utilization. |

### Generics — Implications for Cobalt

A language compiling to .NET IL must work within the CLR's generic type system. This has concrete consequences:

**What .NET reification provides that monomorphization does not:**
- Runtime type identity for generic types. `typeof(List<int>) != typeof(List<string>)` is evaluable at runtime. This enables the entire reflection-based ecosystem.
- Runtime construction of generic types. Code can create `List<T>` for a `T` discovered at runtime.
- Compact representation for reference-type instantiations.
- Constraint verification by the runtime.

**What is lost compared to full monomorphization:**
- Per-instantiation optimization for reference types.
- Guaranteed static dispatch.
- Compile-time-only type relationships. Rust's lifetime parameters have no runtime representation; .NET IL has no mechanism to encode or enforce lifetime relationships.

**Practical design implications:**
- Cobalt's generics will necessarily be reified. The language should embrace runtime type identity and build its ownership/lifetime system as a compile-time-only layer on top.
- Trait bounds can be translated to .NET generic constraints where there is a direct mapping (interface constraints, `struct`/`class` constraints, `unmanaged`). Richer bounds (lifetime bounds, conditional implementations, const generics) must be enforced by the Cobalt compiler and erased from emitted IL, potentially preserved as custom attributes for tooling.
- Conditional trait implementations have no direct .NET IL encoding. Cobalt could emulate them via compiler-generated wrapper types or extension methods, but this introduces complexity and may impede interop.

### Trait Bounds vs C# Constraints: Expressiveness Comparison

**What Rust trait bounds can express that C# constraints cannot:**

1. **Conditional implementation.** In Rust, a generic type can implement a trait only when its type parameter satisfies a bound. In C#, if `Wrapper<T>` implements `IFormattable`, it does so for *all* `T`.
2. **Associated types.** Rust traits can declare associated types that are uniquely determined by the implementor. C# has no mechanism for this.
3. **Lifetime bounds.** Rust can express `T: 'a`, meaning "T contains no references shorter than lifetime `'a`." No C# equivalent.
4. **Higher-ranked trait bounds.** `for<'a> F: Fn(&'a str) -> &'a str` — universally quantified over lifetimes. No C# equivalent.
5. **Negative reasoning (auto traits).** Rust's `Send` and `Sync` are automatically implemented for types whose members are all `Send`/`Sync`, and automatically *not* implemented when a member is not. C# constraints can only assert positive requirements.
6. **Const generics.** Parameterizing types by compile-time constant values. No C# equivalent.
7. **`Sized` and `?Sized` bounds.** Rust distinguishes between types with a known compile-time size and dynamically-sized types. No C# equivalent.

**What C# constraints can express that Rust trait bounds cannot:**

1. **Constructor constraint (`new()`).** Enabling `new T()` inside generic code.
2. **Base class constraint.** `where T : SomeBaseClass` — Rust has no class inheritance.
3. **Runtime variance.** Declaration-site covariance/contravariance on interfaces, enforced at runtime.
4. **Runtime type inspection.** `typeof(T)`, pattern matching on `T`, runtime casts.
5. **`allows ref struct` anti-constraint.** Widening a generic parameter to accept stack-only types.

### Higher-Kinded Types

Neither C# nor Rust has full support for higher-kinded types (HKTs).

**Rust** provides Generic Associated Types (GATs), stabilized in Rust 1.65, which cover the most commonly needed subset. A GAT allows an associated type in a trait to itself be generic. Rust does not plan to add full HKT support; the language team's position is that GATs and future extensions will cover practical use cases.

**C#** has no language feature analogous to GATs. Higher-kinded polymorphism has been requested (dotnet/csharplang#339) but is not on the language roadmap. Workarounds include static abstract interface methods and defunctionalization.

**Implications for Cobalt:** The most pragmatic approach is to support associated types (including generic associated types) in the trait system, resolve them at compile time, and emit the appropriate concrete types in IL. This provides much of the expressiveness of Rust's GATs while remaining compatible with .NET's type system.

### Generics — Summary

| Feature | C# / .NET | Rust |
|---|---|---|
| Generic model | Reified; type info preserved at runtime | Monomorphized; no runtime generic types |
| Specialization strategy | JIT: shared for ref types, specialized for value types | AOT: one copy per concrete instantiation |
| Constraints/bounds | Interface, base class, `struct`, `class`, `new()`, `unmanaged`, `notnull`, `allows ref struct` | Trait bounds, lifetime bounds, HRTBs, `Sized`/`?Sized`, const generics |
| Conditional implementation | Not supported | Supported via bounded `impl` blocks |
| Associated types | Not supported | Supported, including GATs |
| Const generics | Not supported | Supported (integers; more types in progress) |
| Variance | Declaration-site on interfaces/delegates (`in`/`out`) | Lifetime subtyping only; no type-parameter variance |
| Higher-kinded types | No native support; workarounds via static abstract interfaces | No native support; GATs cover primary use cases |
| Runtime reflection | Full: `typeof(T)`, `MakeGenericType`, runtime construction | None; `TypeId` for limited identity checks only |
| Dynamic dispatch for generics | Always for ref-type instantiations (shared code path) | Opt-in via `dyn Trait` |

---

## 3. Traits vs Interfaces

### C# Interfaces

#### Abstract Members and the Traditional Model

A C# interface defines a contract: a set of method signatures, property signatures, indexers, and events that implementing types must provide. Prior to C# 8, interfaces were purely abstract. A class or struct that declares it implements an interface must supply concrete definitions for every member, or be declared `abstract` itself.

C# interfaces participate in a full nominal type hierarchy. A class can implement multiple interfaces, and interfaces can extend other interfaces. The relationship is declared explicitly at the type definition site — there is no structural conformance.

Explicit interface implementation allows a type to provide a member implementation that is only accessible when the reference is typed as the interface, not as the concrete class. This is used to resolve ambiguity when two interfaces define members with the same signature, or to hide an interface member from the class's public surface.

#### Default Interface Methods (C# 8 / .NET Core 3.0+)

C# 8 introduced default interface methods (DIM), allowing interfaces to provide method bodies. If an implementing type does not override a DIM member, the runtime uses the interface-provided default. This required changes to the CLR itself: the runtime resolves to the "most specific" implementation available. A class-provided implementation always wins over a DIM. When multiple interfaces contribute defaults, the "most specific override" rule applies; if the result is ambiguous (a diamond scenario), the implementing class must provide an explicit override.

A subtle but important detail: DIM members are only callable through an interface-typed reference. If you have a concrete class variable, you must cast to the interface to invoke the default.

#### Static Abstract Members (C# 11 / .NET 7+)

C# 11 introduced static abstract (and static virtual) members in interfaces, primarily motivated by generic math: `INumber<T>`, `IAdditionOperators<TSelf, TOther, TResult>`, etc. Static abstract members can only be accessed through a generic type parameter constrained to the interface — there is no dynamic dispatch for static members. This is conceptually close to how Rust uses trait bounds on generic parameters.

#### Interface Variance

C# supports declaration-site variance on generic interface type parameters using `out` (covariant) and `in` (contravariant) keywords. Variance enables implicit reference conversions but only works with reference types; value types are always invariant.

Rust has no equivalent of declaration-site variance on traits. Rust's variance rules apply to lifetime parameters and type parameters within data structures, not on trait definitions.

### Rust Traits

#### Methods, Associated Types, and Associated Constants

A Rust trait defines methods (with `&self`, `&mut self`, `self`, or no receiver), associated types, and associated constants. Unlike C# interfaces, Rust traits are not part of a nominal class hierarchy. Trait implementation is declared via `impl Trait for Type` blocks, which can appear anywhere in the same crate as either the trait or the type (subject to coherence rules). This separation of data definition from behavior implementation is a fundamental architectural difference.

#### Default Implementations

Traits can provide default method bodies. Default methods can call other methods on the same trait, including non-defaulted ones, enabling a pattern where a trait requires only a small number of methods and derives the rest from defaults.

#### Trait Objects and Dynamic Dispatch (`dyn Trait`)

A trait object is a fat pointer consisting of two machine words: a pointer to the data and a pointer to a vtable. Unlike C++ or C#, the vtable pointer is not embedded in the object itself — it lives in the reference. This means the same struct can be referenced through different trait object types without any modification to the struct's layout.

Not all traits can be used as trait objects. A trait is "dyn compatible" (formerly "object safe") only if its methods do not use `Self` in return position, do not have generic type parameters, and the trait does not require `Self: Sized`.

#### Marker Traits: `Send`, `Sync`, `Copy`, `Sized`

Rust has marker traits that carry no methods but provide compile-time guarantees:

- **`Send`**: A type can be safely transferred to another thread.
- **`Sync`**: A shared reference `&T` can be safely sent to another thread.
- **`Copy`**: A type can be duplicated by simple bitwise copying.
- **`Sized`**: A type's size is known at compile time.

`Send` and `Sync` are auto traits: the compiler automatically implements them for types whose fields are all `Send`/`Sync`. C# has no equivalent of marker traits — thread safety is not encoded in the type system.

### Coherence and Orphan Rules

**Rust** enforces coherence: for any given combination of trait and type, there is at most one `impl`. The orphan rule requires that you own either the trait or the type. Without this, two independent crates could each write `impl Display for Vec<i32>`, creating an unresolvable conflict. The standard workaround is the **newtype pattern**: wrapping a foreign type in a local tuple struct.

**C#** has no coherence or orphan rules. Interface implementation is always declared at the type definition site — you cannot retroactively add an interface implementation to a type defined in another assembly. This makes conflicts impossible but prevents retroactive implementation.

### Extension Traits (Rust) vs Extension Methods (C#)

C# extension methods are syntactic sugar for static method calls — they do not participate in interface dispatch or virtual calls. Rust extension traits are real trait implementations subject to orphan rules and the full trait machinery.

### Static Dispatch vs Dynamic Dispatch

**Rust** offers explicit control: static dispatch via generics/`impl Trait` (monomorphized, zero-cost) and dynamic dispatch via `dyn Trait` (fat pointer, vtable indirection). The choice is explicit and local.

**C#** uses Virtual Stub Dispatch (VSD) for all interface method calls. The JIT can devirtualize when it can prove the concrete type. Current devirtualization rates are approximately 15% for virtual calls and ~5% for interface calls. For generic methods with struct type arguments, the runtime generates specialized code, naturally eliminating virtual dispatch. Static abstract interface members provide a form of static dispatch analogous to Rust's trait-bounded generics.

### Derive Macros (Rust) vs Source Generators (C#)

Rust derive macros receive token streams and produce new code. Source generators receive a semantic model and can only add new files (not modify existing source). Source generators are less flexible but offer better tooling integration.

### Traits vs Interfaces — Implications for Cobalt

**Trait implementation as separate blocks.** Rust's `impl Trait for Type` model could be accepted as syntax and compiled into standard .NET metadata (interface implementation on the type). For types in external assemblies, retroactive trait implementation cannot be expressed without wrapper types or extension methods.

**Associated types.** Could be encoded as generic parameters on the .NET interface, or erased at the .NET level and tracked only by the Cobalt compiler. The former changes ergonomics; the latter breaks cross-assembly interop.

**Marker traits.** `Send` and `Sync` would be Cobalt-only compile-time constructs with no .NET representation. They could be encoded as custom attributes for cross-assembly consumption by other Cobalt code.

**Orphan rules and .NET assemblies.** The hardest design question. Cobalt type + Cobalt trait works naturally. Cobalt trait + foreign .NET type is problematic (cannot modify compiled assemblies). A pragmatic approach: enforce orphan rules within Cobalt; provide newtype or `extend` blocks for adapting foreign types, with clear documentation that such adaptations are assembly-local.

**Trait objects on .NET.** The most natural mapping is standard .NET interface dispatch. A `dyn CobaltTrait` would compile to a .NET interface reference, with method calls going through VSD. This sacrifices Rust's fat-pointer model but gains full compatibility with .NET's GC, casting, and type-checking.

### Traits vs Interfaces — Summary

| Dimension | Rust Traits | C# Interfaces | Cobalt Consideration |
|---|---|---|---|
| Implementation site | Decoupled `impl` blocks | Declared on the type | Accept `impl` syntax, compile to .NET metadata on the type |
| Retroactive implementation | Yes, subject to orphan rules | No | Limited by .NET metadata model; newtype or compile-time-only workarounds |
| Coherence guarantee | Compiler-enforced orphan rules | Structural impossibility | Enforce within Cobalt; define clear rules for cross-language boundary |
| Static dispatch | Monomorphization (zero-cost) | JIT specialization for value types; shared code for reference types | Use .NET generics with constraints; static abstract members for operators |
| Dynamic dispatch | Fat pointer + vtable | Object reference + VSD | Map to .NET interface dispatch |
| Associated types | First-class feature | Encoded as generic parameters | Compiler could provide syntactic sugar over .NET generic parameters |
| Marker traits | `Send`, `Sync`, `Copy` — compile-time only | No equivalent | Cobalt-only compile-time feature; encode as custom attributes |
| Default methods | Trait default methods (compiled into impl) | DIM (runtime-resolved) | Could use either model; DIM provides .NET compatibility |

---

## 4. Enums and Algebraic Data Types

### C# Enums: Integer-Backed Labels

C# enums are thin wrappers over integral types. Every enum member maps to an underlying integer value. They carry no associated data and cannot define methods.

The `[Flags]` attribute enables bitwise combination of enum values.

**Fundamental limitations relative to algebraic data types:**

- **No associated data.** A C# enum variant cannot carry a payload.
- **Not closed under safety.** Any integer can be cast to any enum type (`(Color)42` is legal), so switch statements can never be truly exhaustive without a default arm.
- **No structural guarantees.** Two unrelated enum types can be confused via casting.

### C# Workarounds for Discriminated Unions

#### Sealed Class Hierarchies

The most common encoding uses an abstract base class with sealed derived types, often using `record` types (C# 9+) for structural equality. This is idiomatic C# with good tooling support, but lacks compiler-enforced exhaustiveness and closedness.

#### The OneOf Library

Generic struct types `OneOf<T0, T1, ..., Tn>` that hold exactly one value with exhaustive `.Match(...)` dispatch. Exhaustive at compile time but variants are positional (fragile), and every capturing lambda allocates a closure.

#### The C# Discriminated Unions Proposal (C# 15)

Union types are appearing in .NET 11 previews as a C# 15 feature. The `union` keyword declares a struct with a single `object` reference field. Value types are boxed on entry. The compiler enforces exhaustive switch expressions over union types. This design explicitly complements other in-progress C# work on closed hierarchies.

### Rust Enums: Full Algebraic Data Types

Rust enums are sum types where each variant can carry distinct associated data. `Option<T>` and `Result<T,E>` are ordinary enums deeply integrated with the language through syntactic sugar (`?` operator, `if let`, `while let`).

#### Enum Memory Layout and Niche Optimization

Rust enums are tagged unions. The compiler performs **niche optimization**: for types with invalid bit patterns, `Option<T>` uses those patterns to represent `None` with zero additional space:

- `Option<&T>` is one pointer width (null = `None`). This is ABI-stable.
- `Option<NonZeroU32>` is 4 bytes (zero = `None`).
- `Option<bool>` is 1 byte (value 2 = `None`).
- `Option<Option<bool>>` is still 1 byte.

### Pattern Matching

#### Rust's match

- **Exhaustive by default** — omitting a variant is a compile error, not a warning.
- Deep destructuring, guards, binding modes (by value/ref/mut ref), or-patterns.
- Adding a variant to an enum causes every non-wildcard `match` to fail compilation.

#### C# Pattern Matching (C# 7–14)

Progressively enhanced: type patterns, property patterns, positional patterns, relational/logical combinators, list patterns.

**Critical difference in exhaustiveness:** Rust treats it as a hard error; C# treats it as a warning and (pre-C# 15) lacks closed-type knowledge.

### .NET Representation Strategies for Rust-Style Enums

| Strategy | Approach | Advantages | Disadvantages |
|---|---|---|---|
| **Class hierarchies** | Abstract base + sealed derived classes | Natural .NET fit, works with tooling | Heap allocation for every value |
| **Explicit-layout structs** | Overlapping value-type fields | Stack-allocated, cache-friendly | Cannot hold reference types in overlapping fields |
| **Struct-with-object-field** | Tag + single `object` reference (C# 15 approach) | Works with all types, compact | Boxing for value types |
| **Hybrid** | Select strategy per enum based on variant content | Best-of-both-worlds | Implementation complexity |

### F# Discriminated Unions: A .NET Precedent

F# compiles discriminated unions to class hierarchies: an abstract base class with a private constructor (enforcing closedness), sealed nested subclasses per variant, a `Tag` property, factory methods, and structural equality/comparison. Struct DUs (F# 4.1+) avoid heap allocation but fields are not overlapped — the struct contains all variant fields, making it larger than Rust's union layout.

**Key lessons for Cobalt:**
1. Class hierarchies work but cost heap allocation per value.
2. Struct-based unions avoid allocation but suffer size bloat.
3. Cross-language interop is hindered when closedness/exhaustiveness cannot be expressed in .NET metadata.
4. Niche optimization is not feasible through standard .NET mechanisms (GC reference tracing prevents bit-level layout tricks).

### Enums/ADTs — Summary

| Dimension | C# (current) | Rust | F# (.NET precedent) |
|-----------|--------------|------|---------------------|
| **Enum data** | Integer labels only | Full ADTs with arbitrary data | Full ADTs |
| **Closedness** | Not enforced (pre-C# 15) | Enforced | Enforced (private constructor) |
| **Exhaustiveness** | Warning only (pre-C# 15) | Hard error | Hard error within F#; invisible to C# |
| **Memory layout** | N/A (enums are ints) | Tagged union with niche optimization | Class hierarchy (heap) or struct (oversized) |
| **Pattern matching** | Rich and growing, no exhaustiveness for type hierarchies | Full destructuring, guards, exhaustiveness | Full destructuring, exhaustiveness |

---

## 5. Nullability

### The Billion-Dollar Mistake

In 1965, Tony Hoare introduced the null reference into ALGOL W. In 2009, he called this his "billion-dollar mistake." The fundamental problem is that null conflates the absence of a value with an uninitialized or invalid state. When any reference can be null at any time, every reference access is a partial operation that may fail at runtime.

### Rust's Approach: Null Does Not Exist

Rust has no null keyword, no null literal, and no concept of a null reference. A variable of type `T` always contains a valid value of type `T`. This eliminates null-pointer dereferences entirely within safe code.

**Option\<T\>: Explicit Absence.** Rust models absence through `Option<T>` (`Some(T)` or `None`). The compiler forces handling both cases. `Option<T>` composes naturally: `Option<Option<T>>` is valid and semantically distinct. Null pointer optimization ensures `Option<&T>` is the same size as a raw pointer.

**Result\<T, E\>: Error Handling Without Null.** Where C# might return `null` to signal failure, Rust uses `Result` to carry structured error information. The `?` operator provides ergonomic propagation.

**The Never Type (!).** Represents computations that never produce a value. `Result<T, !>` is an infallible result.

**Unsafe code.** Raw pointers (`*const T`, `*mut T`) may be null but can only be dereferenced inside `unsafe` blocks, containing unsafety to clearly marked boundaries.

### C# Nullable Reference Types (C# 8+)

NRT is enabled per-project/file via `#nullable enable`. The compiler applies static flow analysis:
- `string` = non-nullable annotation
- `string?` = nullable annotation

**Crucially, NRT violations are warnings, not errors.** There is no runtime enforcement — annotations are encoded as metadata attributes with zero effect on IL. A `string` annotated non-nullable can still hold null at runtime.

The null-forgiving operator (`!`) silences the analysis with no runtime effect. It is widely regarded as an anti-pattern.

The nullable context has four modes (annotation on/off × warnings on/off), allowing gradual adoption but creating permanent holes at library boundaries where code is "null-oblivious."

### C# Nullable Value Types

`Nullable<T>` / `T?` wraps a value type with `HasValue`/`Value`. The CLR has special boxing behavior: boxing a null `Nullable<T>` produces a null reference, and boxing a non-null one produces a boxed `T` (not a boxed `Nullable<T>`).

### The Core Difference: Elimination vs. Annotation

| Dimension | Rust | C# (NRT) |
|---|---|---|
| **Null in the language** | Does not exist | Exists; NRT is an overlay |
| **Enforcement** | Compile-time error | Compile-time warning |
| **Runtime behavior** | No null dereferences in safe code | Null dereferences still possible |
| **Representation** | `Option<T>` is a real type | `string?` vs `string` is a metadata annotation; same IL |
| **Composability** | `Option<Option<T>>` is distinct from `Option<T>` | `string??` = `string?`; annotations do not nest |
| **Escape hatch** | `unsafe` blocks (auditable, rare) | `!` operator (silent, common) |

### Comparison with Other .NET Languages

**F#** has had `Option<'T>` since inception. `None` is represented as a null reference to `FSharpOption<T>` at the IL level — F#'s "no null" guarantee is a compiler-level fiction. F# 9 added NRT interop. `ValueOption<'T>` (struct) avoids heap allocation but is a separate type.

**Kotlin** (JVM analogy) inserts runtime null-checks at interop boundaries, uses "platform types" for un-annotated Java types, and treats nullability as a first-class type system concern. This is more enforceable than C#'s advisory model.

### Nullability — Implications for Cobalt

**Null elimination is achievable within Cobalt code.** The F# and Kotlin precedents prove that a language can present a null-free surface on a null-permissive runtime.

**Boundary strategies:**
1. **Runtime null-checks at interop boundary** (Kotlin model): insert checks when calling .NET methods.
2. **Consume NRT annotations** from .NET metadata for smarter boundary handling.
3. **Platform types** for un-annotated .NET libraries.
4. **Wrapper generation** for critical APIs.

**IL representation of Option\<T\>:**
1. Null-as-None for reference types, `Nullable<T>` for value types (most interop-friendly, but `Option<Option<T>>` collapses for reference types).
2. Dedicated `Cobalt.Option<T>` struct (maximum fidelity, worst interop).
3. Hybrid: null-as-None for single-level `Option<ReferenceType>`, discriminated struct for nested cases.

**Key design takeaways:**
1. Null elimination is achievable within Cobalt code.
2. Interop is the hard problem — the Kotlin model (runtime checks + platform types) is the most proven approach.
3. NRT metadata is an asset Cobalt should consume.
4. `Option<T>` should use null internally for reference types (zero-cost, natural interop).
5. The null-forgiving operator should not exist in Cobalt. Pattern matching and combinators should be the only extraction mechanism.
6. Null-safety violations must be compile errors, not warnings.

---

## Sources

### Value Types and Reference Types
- [ref struct types — C# reference | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct)
- [C# 13 ref struct interfaces and the allows ref struct anti-constraint — NDepend Blog](https://blog.ndepend.com/c-13-ref-struct-interfaces-and-the-allows-ref-struct-generic-anti-constraint/)
- [What is Ownership? — The Rust Programming Language](https://doc.rust-lang.org/book/ch04-01-what-is-ownership.html)
- [Value Types vs Reference Types — Adam Sitnik](https://adamsitnik.com/Value-Types-vs-Reference-Types/)
- [Boxing and Unboxing — C# | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/types/boxing-and-unboxing)
- [Memory Management — Rust for C#/.NET Developers](https://microsoft.github.io/rust-for-dotnet-devs/latest/memory-management/index.html)

### Generics
- [Constraints on type parameters — C# (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/generics/constraints-on-type-parameters)
- [Covariance and Contravariance in Generics — .NET (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/generics/covariance-and-contravariance)
- [Design and Implementation of Generics for the .NET CLR (Microsoft Research)](https://www.microsoft.com/en-us/research/wp-content/uploads/2001/01/designandimplementationofgenerics.pdf)
- [Generics — Rust for C#/.NET Developers (Microsoft)](https://microsoft.github.io/rust-for-dotnet-devs/latest/language/generics.html)
- [Advanced Traits — The Rust Programming Language](https://doc.rust-lang.org/book/ch20-02-advanced-traits.html)
- [GATs Initiative](https://rust-lang.github.io/generic-associated-types-initiative/explainer/motivation.html)

### Traits vs Interfaces
- [Static Abstract Members in Interfaces — C# 11 (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-11.0/static-abstracts-in-interfaces)
- [Default Interface Methods — Under the Hood (Matt Warren)](https://mattwarren.org/2020/02/19/Under-the-hood-of-Default-Interface-Methods/)
- [Defining Shared Behavior with Traits — The Rust Programming Language](https://doc.rust-lang.org/book/ch10-02-traits.html)
- [Rust Orphan Rules (Ixrec / GitHub)](https://github.com/Ixrec/rust-orphan-rules)
- [Virtual Stub Dispatch — .NET Runtime (GitHub)](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/virtual-stub-dispatch.md)

### Enums and Algebraic Data Types
- [C# 15 Unions — NDepend Blog](https://blog.ndepend.com/csharp-unions/)
- [Discriminated Unions Proposal — dotnet/csharplang](https://github.com/dotnet/csharplang/blob/main/proposals/unions.md)
- [F# Discriminated Unions — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions)
- [std::option — Rust Documentation](https://doc.rust-lang.org/std/option/)
- [Rust Memory Layout Optimization (Enum)](https://frehberg.com/2022/01/rust-memory-layout-optimization/)
- [Struct discriminated unions — dotnet/fsharp#9368](https://github.com/dotnet/fsharp/issues/9368)

### Nullability
- [Null References: The Billion Dollar Mistake — Tony Hoare, QCon London 2009](https://www.infoq.com/presentations/Null-References-The-Billion-Dollar-Mistake-Tony-Hoare/)
- [Nullable reference types — C# | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/nullable-references)
- [Nullability and Optionality — Rust for C#/.NET Developers](https://microsoft.github.io/rust-for-dotnet-devs/latest/language/nullability-and-optionality.html)
- [Null safety — Kotlin Documentation](https://kotlinlang.org/docs/null-safety.html)
- [Nullable Reference Types in F# 9 — .NET Blog](https://devblogs.microsoft.com/dotnet/nullable-reference-types-in-fsharp-9/)
