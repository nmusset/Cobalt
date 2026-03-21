# Phase 1.5: .NET IL Constraints

This document covers the fifth research topic from the Cobalt roadmap: what Common Intermediate Language (CIL) and the .NET runtime can and cannot express, where metadata provides extensibility, and where the runtime imposes hard limits relevant to implementing Rust-like ownership, borrowing, and lifetime features on .NET.

Previous documents covered type systems (01), ownership and borrowing (02), memory management (03), and concurrency (04). This document focuses on the runtime and IL substrate itself: the actual capabilities of the virtual machine that will execute Cobalt code.

---

## 1. CIL Type System Capabilities

### What CIL Can Represent

The Common Type System (CTS) defined by ECMA-335 supports a rich set of types. Understanding each is necessary because Cobalt code must ultimately compile to IL that uses these primitives.

**Classes (reference types).** Heap-allocated objects managed by the GC. Support single inheritance, multiple interface implementation, virtual dispatch, and finalization. A class instance is accessed through a managed reference (an O-type in CIL terminology) that the GC tracks and may relocate during compaction. Classes are the default heap-allocated reference type in .NET.

**Structs (value types).** Types that derive from `System.ValueType`. Stored inline — on the stack, in registers, or embedded within the layout of their containing type. Copied by value on assignment. Cannot be null (without `Nullable<T>` wrapping). Cannot participate in inheritance hierarchies (they are implicitly sealed). Value types are the closest CIL analog to Rust's non-`Box` types, but they lack move semantics — assignment always copies.

**Interfaces.** Contracts defining method signatures (and, since .NET 8 / C# 8, default implementations and static abstract/virtual members). A type can implement multiple interfaces. Interface dispatch uses a separate vtable mechanism from class virtual dispatch. Since C# 13, ref structs can implement interfaces, though they cannot be boxed to the interface type.

**Delegates.** Reference types that encapsulate a method pointer plus an optional target object. The CIL equivalent of a type-safe function pointer with context. Delegate invocation goes through `callvirt` on the delegate's `Invoke` method. Delegates require a heap allocation for the delegate object itself (and often a closure object for captured state).

**Enums.** Value types with a named set of constant values, backed by an integral underlying type. CIL enums are simply named integer constants — they carry no data variants. This is fundamentally different from Rust enums, which are tagged unions capable of holding different data in each variant. CIL has no native concept of discriminated unions.

**Managed references (byrefs).** The `&` type in CIL, spelled `ref` in C#. A managed pointer that the GC understands and can track (it points into the interior of a managed object or to a stack location). Byrefs cannot be stored on the heap in general — they can only appear as local variables, parameters, and return values (ref returns, added in C# 7), and since C# 11, as fields within ref structs. The verifier enforces that byrefs do not escape their safe context.

**Unmanaged pointers.** Raw pointers (`int*`, `void*`) as in C. These are not tracked by the GC and require `unsafe` context. CIL supports pointer arithmetic, dereferencing, and the full range of unsafe memory operations on these types. Unmanaged pointers make the containing code unverifiable.

**Function pointers (C# 9 / .NET 5).** Represented in CIL using the `method` type in signature encoding. Function pointers use the `calli` instruction for invocation (rather than `callvirt` through a delegate). They include the calling convention as part of their type identity. In metadata, calling conventions are encoded using `CallKind` flags and `modopt`s for extensible conventions (`unmanaged ext`). Function pointers are pointer types — they require `unsafe` context, cannot be used as generic arguments, and cannot be converted to `object`. They avoid the allocation overhead of delegates but sacrifice safety and flexibility.

**Byref returns and ref locals (C# 7+).** Methods can return byrefs (`ref int Method()`), and local variables can hold byrefs (`ref int x = ref arr[0]`). These are fully supported in CIL and verified by the runtime. The verifier tracks the safe context of each byref to ensure it does not outlive the data it points to.

**Ref fields (C# 11 / .NET 7).** A ref struct may contain a field of byref type. In metadata, ref fields are encoded using `ELEMENT_TYPE_BYREF` in the field signature — the same encoding used for byref parameters and locals, now permitted in field positions. This was a deliberate extension of ECMA-335. Ref fields enable types like `Span<T>` to store a managed pointer directly rather than relying on the internal runtime type `ByReference<T>`. The runtime enforces that ref fields only exist inside types marked `IsByRefLike` (ref structs).

### What CIL Cannot Represent

**Lifetimes.** CIL has no concept of lifetime parameters or lifetime annotations. There is no way to express in a type signature that "this reference must not outlive that reference" with parametric generality. C#'s scoped-ref system (the safe-context rules for ref returns and ref fields) is a restricted, non-parametric form of lifetime tracking, but it is entirely a compiler-side enforcement mechanism — it does not appear in IL in a form that other compilers can reconstruct from metadata alone. The `ScopedRefAttribute` marks parameters as `scoped`, but this is a binary flag, not a parametric lifetime.

**Ownership annotations.** Nothing in CIL expresses that a value is "owned" or that passing it to a function "moves" it. All value-type assignment is a bitwise copy. All reference-type assignment is a reference copy. There is no way to mark a parameter, field, or return value as consuming (taking ownership of) the argument.

**Move semantics.** CIL has no `move` instruction or equivalent. Value types are always copied. Reference types are always aliased. There is no way to invalidate a variable after its value has been transferred elsewhere. A Cobalt compiler would need to enforce move semantics entirely at compile time, generating ordinary copy/assignment IL while statically preventing use-after-move.

**Linear or affine types.** The CIL type system does not support types that must be used exactly once (linear) or at most once (affine). There is no runtime enforcement that a value is consumed. The closest mechanism is `IDisposable` with the `using` pattern, which is convention-based and not enforced by the type system or verifier.

**Const generics.** CIL generics are parametric over types, not values. There is no way to write `Foo<const N: int>` in CIL. The `InlineArray` attribute (.NET 8) demonstrates a workaround — encoding a fixed count via an attribute — but this is a special case handled by the runtime, not a general const-generic facility.

**Associated types as a first-class concept.** CIL interfaces can have generic parameters, but there is no concept of a type member that is determined by the implementing type (Rust's associated types). The usual workaround is an additional generic parameter on the interface: `IIterator<TItem>` instead of Rust's `Iterator { type Item; }`. This works but produces different type identity (the generic parameter is part of the interface's name) and creates different ergonomic properties.

**Higher-kinded types.** CIL cannot express types parametric over type constructors (e.g., "for any `F<_>`, where `F` could be `List`, `Option`, etc."). This is a limitation shared with both C# and most .NET languages.

### The Verification Model

The CLR includes a verifier that checks IL for type safety before JIT compilation (or as part of AOT compilation). Verification is the runtime's mechanism for ensuring memory safety without trusting the compiler.

**What the verifier checks:**

- **Type safety of operations.** Every instruction's operand types must match what the instruction expects. Loading a field requires that the object on the stack is of a type that has that field. Calling a method requires that the arguments match the signature.
- **Stack balance.** Every execution path through a method must leave the evaluation stack in a consistent state. At join points (branch targets, exception handler entries), the stack depth and types must agree across all incoming paths.
- **Definite assignment.** Local variables must be assigned before use. (In practice, the CLR initializes locals to zero/null unless `SkipLocalsInit` is applied, but the verifier still tracks liveness.)
- **Byref safety.** References obtained via `ldloca`, `ldarga`, `ldelema`, or ref returns must not escape their safe context. A byref to a local variable cannot be returned from the method. A byref to a heap object is valid as long as the GC can track it.
- **Object construction.** The verifier ensures that `newobj` is called before an object reference is used, preventing access to uninitialized objects.
- **Protected member access.** The verifier enforces accessibility rules (public, private, protected, internal) at the IL level.

**What makes IL unverifiable:**

- Use of unmanaged pointers (`ldind`, `stind` on native int pointers, pointer arithmetic).
- Use of `calli` with unmanaged calling conventions.
- Use of `cpblk` (block copy) and `initblk` (block initialize) instructions.
- Bypassing type safety via `Unsafe.As<T>` or `Unsafe.AsPointer` (these compile to unverifiable IL).
- Skipping local initialization (`SkipLocalsInit`).
- Union-style overlapping fields with managed references (see Section 3).

The distinction between verifiable and unverifiable IL matters for Cobalt because: (a) a security-conscious deployment model may require verifiable IL, and (b) the verifier's rules about byref lifetimes are the closest thing CIL has to a lifetime system. Cobalt can emit verifiable IL for safe operations and reserve unverifiable IL for `unsafe` blocks, following the same model as C#.

---

## 2. Metadata Encoding

.NET assemblies store metadata in tables defined by ECMA-335: type definitions, method signatures, field definitions, custom attributes, and more. This metadata is the interface contract that other compilers and tools read when consuming an assembly. For Cobalt, the question is: where can Cobalt-specific information (lifetimes, ownership modes, move requirements) be encoded so that Cobalt assemblies can interoperate with each other and with C#?

### Custom Attributes

Custom attributes are the most flexible metadata encoding mechanism. An attribute is a class deriving from `System.Attribute`, stored in the `CustomAttribute` metadata table. Attributes can be attached to assemblies, modules, types, methods, parameters, return values, fields, properties, events, and generic parameters.

**What they can encode.** Attribute constructors accept a fixed set of argument types: the primitive types (`bool`, `byte`, `int`, `float`, `double`, `string`, etc.), `System.Type`, enums, and one-dimensional arrays of these types. Named arguments (properties and fields) follow the same type restrictions. This means attributes can carry structured data, but not arbitrary objects or complex type references.

**How they are stored.** Each custom attribute instance is stored as a blob in the `#Blob` heap, containing the encoded constructor arguments and named arguments. The blob format is defined by ECMA-335 Section II.23.3. Reading an attribute requires decoding this blob, which is a non-trivial parsing operation but is well-supported by `System.Reflection` and `System.Reflection.Metadata`.

**Performance of attribute reading.** Retrieving custom attributes via `System.Reflection` (`GetCustomAttributes`) creates new object instances each time — it is comparatively expensive. However, `System.Reflection.Metadata` provides zero-allocation access to the raw attribute blobs, making it possible to read attribute data efficiently in a compiler or analyzer context. For runtime checks (if any), the cost of reflection-based attribute reading is a concern; for compile-time checks (the primary use case for Cobalt's ownership annotations), it is not.

**The NullableAttribute precedent.** C# 8's nullable reference types introduced a critical precedent: the compiler emits `NullableAttribute` and `NullableContextAttribute` to record nullability information that is purely a compile-time concept — the runtime does not enforce it.

`NullableAttribute` uses a `byte[]` or a single `byte` to encode nullability state for each type position in a signature:
- `0` = oblivious (pre-nullable code)
- `1` = not-null
- `2` = maybe-null

For nested generic types (e.g., `Dictionary<string, List<string?>>`), the byte array encodes each type position in declaration order. `NullableContextAttribute` is an optimization: applied to a type or method scope, it establishes a default nullability byte, allowing individual `NullableAttribute` instances to be omitted when they match the default. This hierarchical defaulting reduces metadata size significantly.

This pattern — compiler-emitted attributes encoding type-system information invisible to the runtime — is directly applicable to Cobalt. Ownership annotations, lifetime scoping information, and move semantics flags could be encoded as custom attributes following the same model: visible to the Cobalt compiler when consuming Cobalt assemblies, invisible to the runtime and to C# consumers (who would see the types without ownership semantics).

Other precedents: `ScopedRefAttribute` marks parameters as `scoped` (a lifetime-narrowing annotation). `RefSafetyRulesAttribute` (module-level) tells consuming compilers which version of ref-safety rules were used during compilation. `IsReadOnlyAttribute` marks readonly references. These are all compile-time concepts encoded in attributes.

### Modreqs and Modopts

Required modifiers (`modreq`) and optional modifiers (`modopt`) are type modifiers that can be applied to types within signatures (field types, parameter types, return types). They are defined in ECMA-335 Section II.7.1.1.

**How they work.** A modifier wraps a type in the signature with a reference to a modifier type (a class or struct used purely as a marker). For example, `volatile int32` in IL is encoded as `modreq(System.Runtime.CompilerServices.IsVolatile) int32`. The modifier type itself has no members — it serves only as a tag.

**Effect on type identity.** This is the critical distinction:

- `modreq`: The modified type is considered a **different type** from the unmodified type. A compiler that does not understand the `modreq` must reject the signature — it cannot silently ignore it. This is the "required" part: understanding the modifier is required for correct use.
- `modopt`: The modified type is considered the **same type** for overloading and type-identity purposes. A compiler that does not understand the `modopt` can ignore it. This is the "optional" part.

**Existing uses.** `IsVolatile` is the canonical `modreq` — it marks a field as requiring volatile access semantics. `InAttribute` is used as a `modreq` on function pointer parameter ref specifiers to indicate `in` parameters. `IsConst` and `IsSignUnspecifiedByte` are `modopt`s used by C++/CLI to preserve C++ type system details.

**Could they encode lifetime or ownership information?** In principle, `modopt`s could annotate types with Cobalt-specific markers (e.g., `modopt(Cobalt.Owned) MyStruct`). Advantages: they are part of the type signature, so tools reading the signature encounter them naturally. Disadvantages: they do not support parameterization (you cannot encode "lifetime `'a`" as a modopt — only a fixed marker type), they complicate type identity (modreqs change it, modopts do not), and they interact poorly with generics (a `modopt` on a generic instantiation creates a different signature from the bare generic). Custom attributes are more flexible and have become the preferred encoding for modern C# compile-time concepts. Modreqs/modopts are better suited for binary-level interop markers (like calling convention annotations on function pointers) than for rich type-system extensions.

### Generic Parameter Constraints in Metadata

The runtime enforces generic parameter constraints at instantiation time. When code constructs a generic type or calls a generic method, the JIT (or AOT compiler) verifies that the type argument satisfies all constraints on the corresponding type parameter.

**Runtime-enforced constraints:**

- `class` (reference type constraint): the type argument must be a reference type. Encoded as `ReferenceTypeConstraint` in `GenericParameterAttributes`.
- `struct` (non-nullable value type constraint): the type argument must be a non-nullable value type. Encoded as `NotNullableValueTypeConstraint`.
- `new()` (default constructor constraint): the type argument must have a public parameterless constructor. Encoded as `DefaultConstructorConstraint`.
- Interface and base-class constraints: the type argument must implement a specified interface or derive from a specified base class. Encoded as entries in the `GenericParamConstraint` metadata table.
- `unmanaged` (C# 7.3): the type argument must be an unmanaged type (no managed references at any nesting level). This is enforced by the runtime in conjunction with the compiler.
- `allows ref struct` (C# 13): an anti-constraint that relaxes the default prohibition on ref struct type arguments. Encoded with a special flag in metadata. The runtime recognizes this and permits `IsByRefLike` types as arguments.

**Compiler-only constraints:**

- `notnull` (C# 8): the type argument must be a non-nullable type. This is purely a compiler diagnostic — the runtime does not enforce it. It is encoded via `NullableAttribute` on the generic parameter.
- `where T : Enum` and `where T : Delegate`: these constraints are enforced by both the compiler and the runtime (the base-class constraint in metadata points to `System.Enum` or `System.Delegate`).

The enforcement gap between runtime and compiler constraints is relevant to Cobalt. Cobalt could define new constraint kinds (e.g., `where T : Owned`, `where T : Movable`) that exist only as custom attributes and are checked only by the Cobalt compiler. The runtime would not enforce them, creating a trust boundary: code compiled by a non-Cobalt compiler could violate these constraints. This is acceptable if Cobalt treats non-Cobalt callers as "unsafe" boundary code, similar to how C#'s nullable analysis treats unannotated code as "oblivious."

### Assembly-Level Metadata

**Module attributes.** Attributes can be applied at the module or assembly level. `RefSafetyRulesAttribute` is applied at the module level to indicate the ref-safety rule version. Cobalt could similarly apply module-level attributes to indicate the Cobalt language version and which ownership rules are in effect.

**Type forwarding.** `TypeForwardedToAttribute` allows a type to be moved between assemblies without breaking binary compatibility. This is a runtime-supported feature: the loader follows the forwarding chain. Relevant to Cobalt if the standard library is refactored across assemblies.

**InternalsVisibleTo.** Allows an assembly to expose `internal` members to a specified friend assembly. This is enforced by the runtime. Cobalt could use this for its own compiler infrastructure (e.g., a Cobalt runtime support library that exposes internal helpers to Cobalt-compiled assemblies).

---

## 3. Value Type Restrictions and Layout

### Struct Size and Layout

**Practical size limits.** ECMA-335 does not specify a maximum struct size, but practical limits exist. The CLR's internal type loader has historically supported structs up to approximately 1 MB, though structs larger than a few hundred bytes are unusual and discouraged for performance reasons (copying overhead, stack frame size). The JIT compiler's register allocation and inlining heuristics are optimized for small structs (up to roughly 8-16 bytes on modern runtimes). Large structs degrade performance because every assignment, parameter pass, and return is a full copy unless passed by reference.

**Layout modes.** Three layout modes are available via `StructLayoutAttribute`:

- `LayoutKind.Auto`: The runtime chooses field order and padding for optimal performance. This is the default for classes. Fields may be reordered. The layout is not guaranteed to be stable across runtime versions.
- `LayoutKind.Sequential`: Fields are laid out in declaration order with platform-appropriate padding. This is the default for structs in C#. Required for P/Invoke interop with native code. The `Pack` field controls alignment (1, 2, 4, 8, etc.).
- `LayoutKind.Explicit`: Each field specifies its byte offset via `[FieldOffset(n)]`. This allows overlapping fields (unions), precise control over padding, and exact matching of native data structures.

The `Size` field on `StructLayoutAttribute` specifies the total size of the type in bytes, allowing trailing padding beyond the last field. This is occasionally used for interop with native structures that have a fixed size.

### The Overlapping Reference Field Restriction

This is one of the most important restrictions for Cobalt's enum design.

When `LayoutKind.Explicit` is used, fields can be placed at overlapping byte offsets, creating a union. However, the runtime enforces a hard restriction: **reference-type fields (managed object references) cannot overlap with each other or with value-type fields at the same offset.**

The reason is fundamental to how the GC works. The GC must be able to scan every field of every object to find managed references. When it encounters a field at a given offset, it must know whether that field is a managed reference (which it should trace and possibly relocate) or a value (which it should ignore). If a managed reference and a value type occupy the same bytes, the GC cannot determine what the field contains — treating a random integer as an object reference would corrupt memory or crash.

Attempting to define a type with overlapping managed references causes a `TypeLoadException` at runtime. This is not a compiler diagnostic — it is a hard runtime enforcement. The restriction applies regardless of whether the overlapping is between two reference fields, between a reference field and a value field, or between a reference field and an unmanaged pointer field.

**Impact on Rust-style enums.** Rust enums are tagged unions: each variant can hold different data, including references. The enum's memory layout overlaps the variant data, with a tag field (discriminant) indicating which variant is active. On CIL, this pattern works for enums whose variants contain only value types — overlapping `int` and `float` fields at the same offset is fine. But it breaks for enums with variants containing managed references.

Consider the Rust enum `enum Node { Leaf(String), Branch(Box<Node>, Box<Node>) }`. In Rust, `Leaf` and `Branch` share the same memory (after the tag). On .NET, `String` and `Box<Node>` would be managed references. Overlapping them in an `Explicit` layout would be rejected by the runtime.

The workaround is to avoid overlapping: store each variant's data in separate, non-overlapping fields, and use the tag to determine which fields are valid. This wastes memory (the struct is the size of the largest variant plus all other variants' fields) or requires boxing the variant data (heap allocating each variant separately, defeating the purpose of a value-type enum). Neither option is ideal. This is a significant friction point for implementing Rust-style enums on .NET.

### InlineArray Attribute (.NET 8)

`InlineArrayAttribute` allows a struct with a single field to be treated as a fixed-size array of that field's type. The runtime replicates the field `N` times in the type's layout.

```csharp
[InlineArray(8)]
struct Float8
{
    private float _element;
}
```

This creates a struct containing 8 contiguous `float` values (32 bytes total). The struct can be indexed like an array and can be sliced into a `Span<T>`. The runtime understands the attribute and adjusts the type's size accordingly.

Restrictions: the struct must have exactly one instance field. The attribute is `AllowMultiple = false` and applies only to structs. In .NET 9+, `Equals()` and `GetHashCode()` throw `NotSupportedException` by default on inline array types, requiring manual override.

`InlineArray` is relevant to Cobalt because it demonstrates a pattern where a custom attribute changes the runtime's interpretation of a type's layout. It is the closest .NET has to const generics for array sizes, though it is a special-case mechanism rather than a general facility.

**Fixed-size buffers.** C#'s `fixed` keyword in structs creates fixed-size buffers of primitive types (in unsafe code). These are an older mechanism predating `InlineArray` and are limited to primitive types. `InlineArray` is the modern replacement, supporting any element type and working in safe code.

### Span<T> and Memory<T>

`Span<T>` is a ref struct that represents a contiguous region of arbitrary memory. Internally (since .NET 7), it contains a `ref T` field and an `int` length. It provides bounds-checked, type-safe access to stack-allocated arrays, heap arrays, native memory, and slices of strings. Because it is a ref struct, it cannot escape to the heap (see Section 4).

`Memory<T>` is a regular struct (not a ref struct) that represents a region of memory that can be stored on the heap. It contains an object reference (to the backing array or `MemoryManager<T>`), an index, and a length. `Memory<T>` can be used in contexts where `Span<T>` cannot (fields of classes, async methods, collections) at the cost of an additional indirection and the inability to reference stack memory.

These types are relevant to Cobalt because they demonstrate the runtime's approach to safe, zero-copy memory access within the constraints of the GC. `Span<T>` is the closest .NET analog to Rust's slice references (`&[T]`), with the ref struct restrictions serving as a crude form of lifetime enforcement.

### Readonly Structs and Readonly Members

`readonly struct` is enforced at both the compiler and IL level. A struct marked `readonly` (the `IsReadOnlyAttribute` on the type) has `initonly` on all its fields in IL. The runtime prevents modification of fields after construction through the `readonly` enforcement. Instance methods on a readonly struct receive `this` as an `in` (readonly reference) parameter, preventing mutation.

`readonly` members (individual methods or properties on a non-readonly struct) are also encoded via `IsReadOnlyAttribute`. The compiler ensures these members do not mutate `this`, and the attribute signals this to consuming compilers.

These mechanisms provide partial immutability guarantees at the IL level — relevant for Cobalt's design of immutable value types and frozen data.

---

## 4. Ref Structs and Stack-Only Semantics

Ref structs are the closest existing .NET mechanism to Rust's stack-bound, lifetime-limited types. Understanding their capabilities and limitations is essential for Cobalt.

### What Ref Structs Are at the IL Level

A ref struct is a value type marked with the `IsByRefLike` attribute (`System.Runtime.CompilerServices.IsByRefLikeAttribute`). This attribute is recognized by the runtime, not just the compiler — the runtime enforces restrictions on types bearing this attribute.

**Runtime-enforced restrictions:**

- Cannot be boxed. An attempt to box an `IsByRefLike` type throws at runtime. This prevents ref structs from being stored on the heap via boxing.
- Cannot be used as a type argument for generic parameters (unless the parameter has the `allows ref struct` anti-constraint).
- Cannot be stored in fields of non-ref-struct types (classes or ordinary structs). The type loader rejects such types.
- Cannot be used as an element type of arrays. Arrays are heap-allocated, so this would violate the stack-only guarantee.

These restrictions are absolute — they cannot be circumvented by unverifiable IL. The runtime checks them during type loading, before any code executes.

### Ref Fields: Encoding and Guarantees

As discussed in Section 1, ref fields use `ELEMENT_TYPE_BYREF` in the field signature. The runtime guarantees:

- A ref field can only exist inside a type marked `IsByRefLike`.
- The ref field can be `null` (default-initialized). `Unsafe.IsNullRef` tests for this.
- The ref field's referent must be alive as long as the ref struct is alive. The compiler enforces this through the safe-context system; the runtime does not check it dynamically.

Ref field modifiers:
- `readonly ref`: Marked with `initonly` in IL. Can be ref-reassigned only in constructors/init accessors.
- `ref readonly`: Marked with `IsReadOnlyAttribute`. The value cannot be assigned through the ref.
- `readonly ref readonly`: Both `initonly` and `IsReadOnlyAttribute`.

### Scoped Refs and the Safe-Context System

The `scoped` keyword narrows the lifetime of a ref or ref struct value, preventing it from being returned or captured. In metadata, `scoped` is encoded via `ScopedRefAttribute` on the parameter.

The safe-context system assigns each expression one of these scopes:

- **caller-context**: The value can be returned to the caller.
- **function-member**: The value cannot escape the current method.
- **declaration-block**: The value cannot escape the current block.

The compiler tracks safe contexts for all ref and ref struct values, enforcing that narrower-scoped values do not flow into wider-scoped positions. For example, a `scoped Span<int>` parameter has safe-context of `function-member` — it cannot be returned, stored in a ref field that escapes, or assigned to a wider-scoped variable.

**Key rules:**
- `this` on struct instance methods is implicitly `scoped ref` — it cannot be returned by reference.
- `out` parameters are implicitly `scoped` — their ref-safe-context is `function-member`.
- `UnscopedRefAttribute` widens the scope, allowing (for example) a struct method to return a ref to a field.

The `RefSafetyRulesAttribute` (module-level) indicates which version of these rules was used during compilation. A consuming compiler uses this to apply the correct rules when analyzing calls to methods in the assembly.

**What is and is not in metadata.** The safe-context rules themselves are entirely compiler logic — they are not enforced by the runtime. The runtime guarantees only that ref structs cannot escape to the heap (via the boxing/field/array restrictions). The fine-grained scoping within a method is a compiler responsibility. This means: if a non-Cobalt compiler emits IL that violates scoped-ref rules, the runtime will not catch it. The program may have dangling references, just as C# code compiled without nullable analysis may have null reference exceptions.

### The `allows ref struct` Anti-Constraint

In C# 13, generic type parameters can specify `allows ref struct` in their `where` clause. In metadata, this is encoded as a special constraint flag on the generic parameter. The runtime recognizes it and permits `IsByRefLike` types as arguments.

This is an anti-constraint because it relaxes a default restriction (normally, ref structs cannot be type arguments) rather than adding a new one. Methods using such parameters must still obey ref struct rules — they cannot box the value, store it in a class field, etc. The compiler enforces these restrictions; the runtime enforces the type-loading-time check.

### Limitations Summary

- **No boxing.** Ref structs cannot be boxed, so they cannot be used with APIs that accept `object`, stored in heterogeneous collections, or passed through interfaces (except via generic constrained calls since C# 13).
- **No generic type arguments (mostly).** Before C# 13, ref structs could not be used as generic type arguments at all. Even with `allows ref struct`, usage is limited — the generic code must not attempt any operation that would escape the value to the heap.
- **No heap storage.** Ref structs cannot be fields of classes or regular structs. They live on the stack or in other ref structs.
- **No async capture.** Before C# 13, ref structs could not appear in async methods at all. Since C# 13, they can appear but cannot be live across an `await` — they must not be captured in the async state machine, which is heap-allocated.

These limitations mean that ref structs model a restricted subset of ownership semantics: stack-confined values with bounded lifetimes. They are a useful building block for Cobalt but are insufficient on their own — Rust's ownership model also covers heap-allocated owned values (`Box<T>`), owned values passed between functions (moves), and references with parametric lifetimes, none of which ref structs address.

---

## 5. IL-Level Features Relevant to Cobalt

### The `constrained.` Prefix

The `constrained.` prefix is placed before a `callvirt` instruction and specifies the type of the value on which the virtual call is being made. Its behavior depends on whether the type is a reference type or value type:

- If the type is a reference type: the managed pointer is dereferenced and the virtual call proceeds normally.
- If the type is a value type that implements the method: the managed pointer is passed directly to a `call` instruction (static dispatch), avoiding boxing.
- If the type is a value type that does not implement the method (e.g., calling `Object.ToString()` without an override): the value is boxed and the virtual call proceeds.

This mechanism is critical for generic code. When a generic method calls `t.ToString()` where `T` is an unconstrained type parameter, the compiler emits `constrained. T callvirt Object::ToString()`. At JIT time, when `T` is known to be `int` (which overrides `ToString`), the JIT replaces this with a direct call — no boxing, no virtual dispatch. When `T` is a reference type, the call becomes a normal virtual dispatch.

For Cobalt, `constrained.` is the IL primitive that makes Rust-style trait method calls on generic value types efficient. Without it, every interface method call on a generic value type would require boxing. With it, the JIT can specialize the call for each value-type instantiation.

### Tail Calls

The `tail.` prefix on a `call`, `calli`, or `callvirt` instruction requests that the current method's stack frame be removed before the call executes, so the callee reuses the caller's stack space. The subsequent instruction must be `ret`.

The `tail.` prefix is a hint, not a guarantee on all platforms. The JIT may ignore it if:
- The caller's stack frame cannot be removed (e.g., due to security checks).
- The method is `synchronized`.
- The calling conventions are incompatible.

However, on .NET Core / .NET 5+ with the RyuJIT compiler, `tail.` is reliably honored for most cases, and the runtime also performs implicit tail call optimization for certain call patterns even without the prefix. F# depends heavily on tail call optimization for functional recursion patterns.

For Cobalt, reliable tail calls are relevant if the language supports functional-style recursion or algebraic data type processing. The `tail.` prefix is the mechanism, but the language design must not depend on tail calls in situations where the runtime might not honor them.

### Span-Based Operations and the Unsafe Class

`System.Runtime.CompilerServices.Unsafe` provides low-level operations that compile to minimal IL:

- `Unsafe.As<TFrom, TTo>(ref TFrom)`: Reinterprets a reference as a different type. Compiles to a single IL instruction (no actual conversion code).
- `Unsafe.AsPointer<T>(ref T)`: Converts a managed reference to a native pointer.
- `Unsafe.SizeOf<T>()`: Returns the runtime size of type `T` (not the managed size — the actual laid-out size including padding).
- `Unsafe.IsNullRef<T>(ref T)`: Tests whether a ref is null (relevant for ref fields).
- `Unsafe.Add<T>(ref T, int)`: Pointer arithmetic on managed references.

These operations are unverifiable but are used extensively in high-performance .NET code. For Cobalt, they represent the escape hatch for unsafe operations — similar to Rust's `unsafe` blocks.

`ReadOnlySpan<T>` has several runtime intrinsics: the JIT recognizes patterns involving `ReadOnlySpan<byte>` initialized from constant data and optimizes them to direct references into the assembly's data section, avoiding heap allocation entirely. This is how C# 11's UTF-8 string literals (`"hello"u8`) work — the data is embedded in the PE file and accessed via a `ReadOnlySpan<byte>` that points directly at it.

### NativeAOT and Its Implications

NativeAOT compiles .NET code directly to native machine code at publish time, producing a self-contained executable with no JIT. This has significant implications for language features:

**Not supported under NativeAOT:**
- `System.Reflection.Emit` and runtime code generation. There is no JIT to compile dynamically generated IL.
- `Assembly.LoadFile` and dynamic assembly loading. All code must be known at compile time.
- `System.Linq.Expressions` compiled form — only the interpreted (slow) mode works.
- Unrestricted reflection. While basic reflection works, anything requiring runtime code generation (dynamic proxies, some serialization frameworks) fails.

**Implications for Cobalt:**
- NativeAOT compatibility should be a design goal. A language that relies on runtime code generation for its core semantics would be limited to JIT-only scenarios.
- Source generators (compile-time code generation that produces C# source, which is then compiled normally) are the NativeAOT-compatible alternative to Reflection.Emit. Cobalt could adopt a similar model for any metaprogramming features.
- Generic instantiations must be known at compile time under NativeAOT. The AOT compiler generates specialized code for each instantiation. If a generic type `Foo<T>` is never instantiated with `Bar`, no `Foo<Bar>` code exists in the binary. This is similar to Rust's monomorphization. Generic virtual methods are particularly expensive under NativeAOT because every possible instantiation of the method for every implementing type must be pre-generated.

### Source Generators vs Runtime Code Generation

Source generators run during compilation and produce additional source files that are compiled into the assembly. They have full access to the syntax trees and semantic model of the project being compiled. They are incremental (re-run only when inputs change) and produce deterministic output.

For Cobalt, source generators are not directly applicable (they are a Roslyn feature for C#/VB), but the pattern is relevant: compile-time code generation that avoids runtime reflection. If Cobalt has a macro system or metaprogramming facility, it should follow this compile-time model for NativeAOT compatibility.

### CompilerServices Attributes

Several attributes in `System.Runtime.CompilerServices` affect code generation:

- `MethodImpl(MethodImplOptions.AggressiveInlining)`: Requests that the JIT inline the method. Not a guarantee, but the JIT gives strong weight to this hint.
- `MethodImpl(MethodImplOptions.NoInlining)`: Prevents inlining. Useful for methods that must have distinct stack frames (for profiling, security, or exception handling).
- `MethodImpl(MethodImplOptions.AggressiveOptimization)`: Requests that the JIT spend more time optimizing this method, skipping the initial tier-0 compilation. Used for methods that are known to be hot.
- `SkipLocalsInit`: Suppresses the default zero-initialization of local variables. This improves performance for methods with large local arrays but is unverifiable (locals may contain garbage data). Methods with this attribute must ensure all locals are written before being read.
- `CallerMemberName`, `CallerFilePath`, `CallerLineNumber`: Compile-time substitutions for the caller's member name, file path, and line number. These are injected by the compiler as default argument values — they do not exist as runtime concepts. Relevant if Cobalt wants to implement similar debugging/logging facilities.
- `ModuleInitializerAttribute`: Marks a method as a module initializer, called automatically when the assembly is loaded. Useful for runtime registration of type metadata, serializers, etc.

---

## 6. Implications for Cobalt

### What Cobalt Can Express at the IL Level vs What Must Be Compile-Time-Only

**IL-expressible concepts:**
- Value types vs reference types: directly mapped to CIL structs and classes.
- Ref struct restrictions: `IsByRefLike` provides runtime-enforced stack confinement.
- Readonly/immutability: `readonly` structs, `initonly` fields, `in` parameters.
- Interface implementation and generic constraints: the runtime enforces these.
- Ref fields and byref returns: the runtime tracks these for GC safety.
- Function pointers: available for zero-overhead function calls.

**Compile-time-only concepts (must be erased before IL emission):**
- Ownership annotations (owned, borrowed, shared). No IL representation; must be attributes.
- Move semantics. IL always copies value types and aliases reference types. The compiler must prevent use-after-move statically, then emit ordinary assignment IL.
- Lifetimes. No parametric lifetime system in CIL. The compiler must verify lifetime safety statically and erase lifetime parameters before emission. Interop with other Cobalt assemblies requires encoding lifetime structure in metadata (custom attributes).
- Borrow checking rules. Purely a compiler analysis pass. The emitted IL contains no trace of borrow checks.
- Const generics (if supported). Must be monomorphized or encoded via attributes/separate type definitions.

This split mirrors Rust's own model: the Rust compiler erases lifetimes before generating machine code. The difference is that Rust's compiler is the sole consumer of its own IR, while Cobalt's emitted IL must be consumable by other .NET tools. The custom attribute encoding is the bridge.

### Where the IL Model Helps

**Reified generics.** Unlike Rust (which monomorphizes) or Java (which erases), .NET's generics are reified: the runtime knows the exact type arguments at runtime, and the JIT generates specialized code for value-type instantiations. This means Cobalt generics can be represented directly as .NET generics with runtime enforcement of constraints, runtime type identity, and JIT-specialized performance for value types. A Cobalt `Vec<i32>` and `Vec<String>` would be distinct types with distinct JIT-compiled code, just as `List<int>` and `List<string>` are in C#.

**Verifiable type safety.** The CLR verifier provides a safety baseline. Cobalt code that compiles to verifiable IL is guaranteed to be memory-safe by the runtime, independently of the Cobalt compiler's correctness. This is a significant advantage over native-target languages where the compiler is the sole safety guarantor.

**Ref structs as a partial ownership primitive.** Ref structs provide runtime-enforced stack confinement. Cobalt can use ref structs for types that must not escape to the heap, with the ref struct restrictions serving as a safety net. The scoped-ref system provides a foundation (though not a complete one) for lifetime tracking within methods.

**The constrained prefix for zero-cost abstraction.** Generic interface calls on value types, via `constrained. callvirt`, achieve the same zero-overhead abstraction that Rust gets from monomorphization of trait calls. A Cobalt trait call on a generic value type parameter can compile to a direct (non-virtual) call after JIT specialization.

### Where the IL Model Creates Friction

**No linear types.** The runtime has no mechanism to enforce that a value is used exactly once or at most once. A Cobalt compiler can track this statically, but the emitted IL contains no enforcement. Code produced by a non-Cobalt compiler can violate linearity constraints freely.

**No lifetime encoding.** There is no IL or metadata representation for parametric lifetimes. Lifetime information must be either: (a) erased entirely, losing cross-assembly lifetime checking, or (b) encoded in custom attributes, which non-Cobalt compilers will ignore. Option (b) allows Cobalt-to-Cobalt interop with full lifetime checking but provides no protection against misuse from C#.

**No move enforcement.** After a value is "moved" in Cobalt source, the emitted IL still has the original variable in scope with its old value (for value types) or with a reference to the now-owned-elsewhere object (for reference types). The compiler must prevent reads of moved-from variables, but the IL does not reflect this — the variable is still there. Debugging tools will show the variable as still containing data, which may confuse developers stepping through Cobalt code in a C#-oriented debugger.

**Overlapping reference fields blocked.** The GC's inability to handle overlapping managed references means Rust-style tagged unions with reference-type variants cannot be represented as true unions in IL. Cobalt must choose between:
1. Separate non-overlapping fields (wasting memory proportional to the number of variants).
2. Boxing each variant's data (wasting heap allocations and adding indirection).
3. Combining approaches: use overlapping fields for value-type-only variants, separate fields for reference-type variants.
4. Using a single `object` field and casting (losing static type safety at the IL level, though the Cobalt compiler can enforce it statically).

None of these is as clean as Rust's native enum layout. Option 3 or 4 is likely the best practical compromise.

**Value type copy semantics.** Every assignment of a value type in CIL is a bitwise copy. There is no way to intercept or customize this. In Rust, move semantics mean that assigning a non-`Copy` type invalidates the source. In CIL, both the source and destination remain valid after assignment. Cobalt must enforce move semantics purely through compiler analysis, treating the source as dead after a move even though the IL still holds a valid copy.

### Recommended Encoding Strategies for Cobalt-Specific Features

Based on the analysis above, the following encoding strategies emerge:

**Ownership and borrowing annotations.** Use custom attributes following the `NullableAttribute` pattern. Define `CobaltOwnershipAttribute` with a byte-array encoding for each type position in a signature (e.g., `0` = unowned/default, `1` = owned, `2` = shared-borrow, `3` = mutable-borrow). Apply `CobaltOwnershipContextAttribute` at the type or method level to establish defaults, reducing per-member attribute overhead.

**Lifetime information.** Define `CobaltLifetimeAttribute` with an encoding scheme that maps lifetime parameters to indices and records relationships between them. Each method signature would encode which parameters share lifetime relationships and which lifetimes are returned. The encoding must be compact — lifetime annotations can be numerous in generic code.

**Move semantics.** No metadata encoding needed. The compiler enforces use-after-move as a compile error. The emitted IL uses standard assignment. A moved-from variable can be zero-initialized after the move (for reference types, set to `null`; for value types, use `initobj`) to make debugging less confusing, though this adds a slight runtime cost.

**Discriminated unions (enums with data).** Emit as a struct with:
- A `byte` or `int` tag field indicating the active variant.
- Non-overlapping fields for each variant (or a single `object` field for reference-type variants, with the Cobalt compiler enforcing type safety at compile time).
- Factory methods for each variant that set the tag and the appropriate field.
- A generated `Match` or `Switch` method for pattern matching.
The compiler should generate `readonly` on the struct and `initonly` on the fields to enforce immutability if the enum is immutable.

**Module-level metadata.** Emit `CobaltModuleAttribute` at the module level, encoding the Cobalt compiler version and the semantic rules version (analogous to `RefSafetyRulesAttribute`). This allows consuming Cobalt compilers to know which rules were in effect when the assembly was compiled.

**Interop with C#.** When a Cobalt assembly is consumed from C#, the C# compiler sees:
- Normal .NET types with custom attributes it does not understand (and ignores).
- No ownership enforcement — all types are usable as regular .NET types.
- Ref struct restrictions are still enforced by the runtime.
- Generic constraints (runtime-enforced ones) are still enforced.

This is the "safe default" model: consuming Cobalt types from C# gives you .NET type safety but not Cobalt ownership safety. Consuming them from Cobalt gives you both. This mirrors how C# nullable annotations work: consuming from C# with nullable enabled gives you warnings; consuming from a pre-nullable language gives you nothing.
