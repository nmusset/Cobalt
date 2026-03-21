# Cobalt Interop Analysis: C#/.NET and Rust Interoperability Mechanisms

This document provides a detailed analysis of interop mechanisms between C#/.NET and Rust, conducted for the Cobalt project -- a language combining C#/Rust semantics on the .NET runtime.

---

## 1. C#/.NET Interop Mechanisms

### P/Invoke: DllImport vs LibraryImport

**DllImport (Legacy)**

P/Invoke (Platform Invoke) is .NET's primary mechanism for calling unmanaged native code from managed code. Historically, this has been done via `DllImportAttribute`:

```csharp
[DllImport("nativelib", EntryPoint = "to_lower", CharSet = CharSet.Unicode)]
internal static extern string ToLower(string str);
```

At runtime, the .NET runtime generates an **IL stub** -- a stream of IL instructions that is JIT-compiled -- to facilitate the managed-to-unmanaged transition. This stub handles marshalling parameters and return values, calling the unmanaged code, and respecting settings on `DllImportAttribute` (e.g., `SetLastError`, `CallingConvention`). Since the IL stub is generated at runtime, it is unavailable in ahead-of-time (AOT) compilation scenarios, and its generation represents a non-trivial cost in both startup time and steady-state performance.

**LibraryImport (Source-Generated, .NET 7+)**

.NET 7 introduced `LibraryImportAttribute`, backed by a **Roslyn source generator** that emits C# marshalling code at compile time, eliminating runtime IL stub generation entirely:

```csharp
[LibraryImport("nativelib", EntryPoint = "to_lower",
    StringMarshalling = StringMarshalling.Utf16)]
internal static partial string ToLower(string str);
```

Key differences and advantages over `DllImport`:

| Aspect | DllImport | LibraryImport |
|--------|-----------|---------------|
| Marshalling code generation | Runtime (IL stub, JIT-compiled) | Compile-time (source generator) |
| AOT compatibility | Not compatible with full NativeAOT | Fully compatible |
| Debugging | Cannot step through marshalling | Can step through generated C# code |
| Inlining | P/Invoke cannot be inlined | Generated code can be inlined by JIT |
| Calling convention | `CallingConvention` property | `UnmanagedCallConvAttribute` |
| String encoding | `CharSet` (ANSI, Unicode) | `StringMarshalling` (UTF-16, UTF-8; ANSI removed) |

The method must be declared `static partial` (not `extern`), and the project must enable `AllowUnsafeBlocks`.

**Marshalling Rules: Blittable vs Non-Blittable Types**

Types are classified as **blittable** or **non-blittable** based on whether they have the same bit-level representation in managed and unmanaged memory:

- **Blittable types**: `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `nint`, `nuint`, `IntPtr`, `UIntPtr`, and structs containing only blittable fields. These are **pinned, not copied**, during marshalling -- the runtime simply pins the managed memory and passes a pointer to unmanaged code. This is extremely fast.
- **Non-blittable types**: `bool`, `char`, `string`, arrays of non-blittable types, classes with non-blittable members. These require **conversion and copying**, sometimes twice (once on the way in, once on the way out). In benchmarks, non-blittable marshalling can be **10x slower** than blittable marshalling.

The performance implication is clear: **design FFI boundaries around blittable types whenever possible.**

**String Marshalling**

String marshalling is particularly expensive because it involves encoding conversion and memory allocation:

- **ANSI** (`CharSet.Ansi` / `UnmanagedType.LPStr`): Converts .NET UTF-16 strings to the system's ANSI codepage. Lossy for non-ASCII characters. Removed as a first-class option in `LibraryImport`.
- **Unicode** (`CharSet.Unicode` / `UnmanagedType.LPWStr` / `StringMarshalling.Utf16`): Uses UTF-16, matching .NET's internal string representation. Most efficient for Windows APIs.
- **UTF-8** (`StringMarshalling.Utf8`): New first-class option in `LibraryImport`. Converts .NET UTF-16 strings to UTF-8. Particularly relevant for Rust interop since Rust strings are UTF-8 natively.

**SafeHandle**

`SafeHandle` is a wrapper class for operating system handles (file handles, socket handles, etc.) that provides:

- **Type safety**: Different handle types (e.g., `SafeFileHandle`, `SafeWaitHandle`) cannot be accidentally mixed.
- **Guaranteed cleanup**: Finalizers run and handles are closed even during unexpected shutdowns (e.g., `ThreadAbortException`).
- **P/Invoke integration**: The runtime marshals `SafeHandle` subclasses automatically in P/Invoke calls, providing reference-counted, release-on-finalize semantics.
- **Race condition prevention**: Prevents handle recycling attacks where a handle is closed on one thread while another thread is still using it.

To create a custom `SafeHandle`, you inherit from it and override `IsInvalid` (to identify invalid handle values) and `ReleaseHandle()` (to perform the actual native resource release).

### COM Interop

**Runtime Callable Wrappers (RCW) and COM Callable Wrappers (CCW)**

.NET provides bidirectional COM interop:

- **RCW (Runtime Callable Wrapper)**: When managed code calls a COM object, the runtime creates an RCW that acts as a proxy, managing the COM object's reference count and translating between .NET's garbage-collected world and COM's `AddRef`/`Release` model.
- **CCW (COM Callable Wrapper)**: When COM clients call .NET objects, the runtime creates a CCW that exposes the .NET object's interfaces as COM-compatible vtables, handling reference counting, QueryInterface, and IDispatch.

**Source-Generated COM Interop (.NET 8+)**

Starting in .NET 8, a **COM source generator** can automatically implement the `ComWrappers` API for `IUnknown`-based interfaces using `[GeneratedComInterface]`. This:
- Generates marshalling code at compile time (like `LibraryImport`).
- Is compatible with NativeAOT (unlike the legacy built-in COM interop).
- In .NET 9+, supports cross-assembly interface inheritance.

**Relevance to modern development**: COM interop remains relevant on Windows for interacting with Windows APIs (DirectX, Shell, WMI, Office automation), but is less important on cross-platform .NET. For Cobalt, COM is unlikely to be a primary interop path with Rust, but the source-generated `ComWrappers` pattern demonstrates how .NET is moving interop logic to compile time.

### C++/CLI and Mixed-Mode Assemblies

C++/CLI is a Microsoft-specific language extension that allows C++ code to directly reference and use .NET types, and vice versa. The compiler produces **mixed-mode assemblies** containing both native machine code and .NET IL in the same binary.

**IJW (It Just Works) Interop** is the mechanism that makes this seamless:
- The compiler generates a `.vtfixup` table in the assembly metadata.
- When loaded, the CLR creates native-callable stubs for each managed method referenced from native code.
- Native code calls managed methods indirectly through these stubs; the transition is automatic.
- No explicit marshalling declarations are needed -- the compiler infers everything.

**Limitations**: C++/CLI is Windows-only, requires the MSVC compiler with `/clr`, and is not supported on .NET's cross-platform targets. It is not a viable interop path for Cobalt if cross-platform support is a goal.

### NativeAOT and Interop: Exporting .NET as Native Code

.NET's NativeAOT compiler compiles .NET code ahead-of-time into native binaries, and can export managed methods as **C-compatible entry points** using `UnmanagedCallersOnlyAttribute`:

```csharp
public static class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "add_numbers")]
    public static int AddNumbers(int a, int b) => a + b;
}
```

When published with NativeAOT, this produces a native shared library (.dll/.so/.dylib) with `add_numbers` as a standard C export, callable from any native language including Rust.

**Constraints**:
- Only static methods can be exported.
- Parameters and return types must be **unmanaged types** (no `string`, no reference types, no managed arrays). You must use `IntPtr`, pointers, or blittable structs.
- Only methods in the **published assembly itself** are exported; methods in referenced projects or NuGet packages are not.
- The exported library includes the entire .NET runtime (GC, type system, etc.), so the binary is large (typically 5-15 MB minimum).

**Significance for Cobalt**: NativeAOT exports are the primary mechanism by which .NET code can be called from Rust. This is the reverse direction of the more common P/Invoke pattern.

### Source Generators for Interop

The trend across .NET interop is clear: **move marshalling from runtime to compile time** via Roslyn source generators. This pattern appears in:

- `LibraryImportAttribute` (P/Invoke source generation)
- `GeneratedComInterfaceAttribute` (COM source generation)
- Custom marshaller source generation via `[CustomMarshaller]`

Benefits: AOT compatibility, debuggability, smaller binary size (no runtime marshalling infrastructure), better performance (generated code can be inlined), and the ability to inspect exactly what marshalling code is produced.

---

## 2. Rust FFI Mechanisms

### extern "C" and #[no_mangle]

Rust's FFI is built on the C ABI as a lingua franca. To expose a Rust function to foreign code:

```rust
#[no_mangle]
pub extern "C" fn add_numbers(a: i32, b: i32) -> i32 {
    a + b
}
```

- `extern "C"` forces the function to use the C calling convention (argument passing, return value, stack cleanup).
- `#[no_mangle]` prevents Rust's name mangling, ensuring the symbol name in the compiled library matches the function name exactly.
- As of Rust 2024 edition, these must be written `#[unsafe(no_mangle)]` and `unsafe extern "C"` to make the unsafety explicit.

**Stability guarantee**: Rust makes **no guarantees** about its native ABI. Only `repr(C)` struct layouts and `extern "C"` function calling conventions are stable and suitable for FFI. Rust's default struct layout is unspecified and can change between compiler versions.

### #[repr(C)] and #[repr(transparent)]

**`#[repr(C)]`** forces a struct or enum to use C-compatible memory layout -- fields are laid out in declaration order with C-standard padding and alignment rules:

```rust
#[repr(C)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}
```

Any type intended to cross an FFI boundary should use `#[repr(C)]`.

**`#[repr(transparent)]`** is for newtype wrappers: it guarantees the wrapper type has the exact same ABI as the single inner field. This is crucial for FFI when you want to create type-safe wrappers around primitive types without changing the ABI:

```rust
#[repr(transparent)]
pub struct Handle(u64);  // Same ABI as u64
```

### cbindgen

[cbindgen](https://github.com/mozilla/cbindgen) is a tool that **generates C/C++ header files** from Rust source code. It scans Rust files for `pub extern "C"` functions and `#[repr(C)]` types, then produces a `.h` file that C/C++ code can `#include` to call the Rust library. This is the Rust-to-C direction of binding generation.

### bindgen

[bindgen](https://github.com/rust-lang/rust-bindgen) goes the other direction: it **generates Rust FFI bindings from C/C++ headers**. Given a `.h` file, it produces Rust `extern "C"` blocks and `#[repr(C)]` struct definitions. It uses libclang to parse the headers, handling most C constructs and many (but not all) C++ constructs. Limitations include incomplete support for C++ templates, constructors/destructors, and some operator overloads.

### The cxx Crate: Safe C++/Rust Interop

[cxx](https://cxx.rs/) takes a fundamentally different approach from cbindgen/bindgen. Instead of generating unsafe C-style bindings, it uses a **shared bridge definition** (an IDL embedded in Rust code via the `cxx::bridge` macro) to generate safe bindings on both sides:

```rust
#[cxx::bridge]
mod ffi {
    extern "Rust" {
        fn greet(name: &str) -> String;
    }
    extern "C++" {
        include!("mylib.h");
        fn do_work(input: &CxxString) -> UniquePtr<Output>;
    }
}
```

Key properties of cxx:

- **Safety**: Both sides' invariants are verified at compile time via static analysis of the bridge definition. No `unsafe` code needed for most calls.
- **Zero-cost**: No copying, serialization, memory allocation, or runtime checks in the generated bridge code. Data is shared directly.
- **Ownership bridging**: `Box<T>` (Rust) maps to unique ownership on C++ side; `UniquePtr<T>` (C++) maps to unique ownership on Rust side. Shared references (`&T`) and exclusive references (`&mut T`) map to `const T&` and `T&` respectively.
- **Lifetime awareness**: The bridge tracks lifetimes to prevent use-after-free across the boundary.
- **Rich type support**: Built-in bindings for `String`/`CxxString`, `Vec<T>`/`CxxVector<T>`, `Box<T>`, `UniquePtr<T>`, `SharedPtr<T>`, `Result<T>`.

cxx is arguably the gold standard for safe cross-language interop and is a model Cobalt could aspire to for its Rust bridge.

### UniFFI: Mozilla's Multi-Language Binding Generator

[UniFFI](https://github.com/mozilla/uniffi-rs) is Mozilla's tool for generating bindings from Rust to multiple foreign languages. You define your API in either UDL (UniFFI Definition Language) files or via procedural macros in Rust, and UniFFI generates:

1. **Rust scaffolding**: The FFI layer that serializes/deserializes Rust types across the C ABI boundary.
2. **Foreign language bindings**: Idiomatic bindings for the target language.

Officially supported targets: **Kotlin, Swift, Python, Ruby**. Third-party plugins exist for **C#** ([uniffi-bindgen-cs](https://github.com/NordSecurity/uniffi-bindgen-cs) by NordSecurity) and **Go**.

UniFFI handles complex types (records, enums, optional values, errors, callbacks, async functions) by serializing them through a binary format over the C ABI. This is higher-overhead than cxx's zero-copy approach but far more portable across languages.

---

## 3. .NET-to-Rust and Rust-to-.NET Interop

### Calling Rust from C# via P/Invoke (Current State of the Art)

The standard pattern for calling Rust from C# is:

1. **Rust side**: Write `extern "C"` functions with `#[no_mangle]`, using only `#[repr(C)]` types and C-compatible primitives. Compile as a `cdylib` (C dynamic library).
2. **C# side**: Declare matching P/Invoke signatures using `DllImport` or `LibraryImport`, pointing to the Rust-produced .dll/.so/.dylib.

```rust
// Rust
#[no_mangle]
pub extern "C" fn calculate(x: f64, y: f64) -> f64 {
    x * y + x
}
```

```csharp
// C#
[LibraryImport("myrust_lib")]
internal static partial double calculate(double x, double y);
```

This works well for simple function calls with blittable types but quickly becomes complex for strings, collections, callbacks, and error handling.

### Calling C# from Rust

This is less common but possible through two mechanisms:

1. **NativeAOT exports**: Compile the C# library with NativeAOT, exporting functions via `[UnmanagedCallersOnly]`. Rust then links against the resulting native library. This works but bundles the entire .NET runtime into the native library.

2. **COM**: On Windows, Rust can call .NET objects exposed through COM using crates like `windows-rs`. This is platform-specific and heavyweight.

3. **rustc_codegen_clr** (experimental): A Rust compiler backend that compiles Rust directly to .NET CIL. Currently passes ~93.9% of the Rust core test suite but is highly experimental, Linux-only, and requires nightly Rust. If matured, this could enable direct Rust-to-.NET compilation without any FFI boundary -- a transformative possibility for Cobalt.

### Existing Projects

**csbindgen (Cysharp)**

[csbindgen](https://github.com/Cysharp/csbindgen) generates C# `DllImport`/`LibraryImport` declarations from Rust `extern "C"` functions. It runs during Rust's build process (`build.rs`), producing C# source files.

- Supports primitives, structs, unions, enums, function pointers, raw pointers.
- Generates `delegate*` (modern .NET) or `MonoPInvokeCallback` delegates (Unity).
- Can chain with rust-bindgen to bring C libraries through Rust into C#.
- Limitations: No support for C variadic functions; no automatic memory lifecycle management; developers must handle ownership manually.

**uniffi-bindgen-cs (NordSecurity)**

[uniffi-bindgen-cs](https://github.com/NordSecurity/uniffi-bindgen-cs) is a third-party UniFFI backend for C#. It generates C# bindings from UniFFI's UDL interface definitions.

- Targets .NET 8+ (or .NET Framework 4.6.1 with configuration).
- Handles complex types (records, enums, optionals, errors, callbacks) through UniFFI's serialization layer.
- Requires Rust 1.88+.
- Still maturing (major version 0.x), with stability of generated bindings between versions not fully guaranteed.

**Interoptopus**

[Interoptopus](https://github.com/ralfbiedert/interoptopus) is a polyglot bindings generator. You annotate Rust code with `#[ffi]` macros, register exports in an inventory, and the tool generates idiomatic bindings for target languages.

- C# is the only Tier 1 (production-ready) target. C and Python backends are suspended.
- Supports primitives, structs, enums, opaque types, callbacks/delegates, ASCII and UTF-8 strings.
- Provides Tokio-based async interop (~361ns round-trip overhead).
- Current stable version is v0.16 (full rewrite). Requires Rust 1.94+.
- Performance: ~20-52ns FFI overhead for simple calls in C#; ~21ns for retained delegates.

### Marshalling Challenges

**String Types**

The mismatch is fundamental:
- Rust `String` / `&str`: UTF-8 encoded, length-prefixed, heap-allocated (for `String`).
- .NET `System.String`: UTF-16 encoded, immutable, garbage-collected.
- C strings (`*const c_char`): null-terminated byte arrays, no encoding guarantee.

Every string crossing the FFI boundary requires encoding conversion (UTF-8 to/from UTF-16) and memory allocation. With `LibraryImport`'s `StringMarshalling.Utf8`, the C# side can handle UTF-8 directly, reducing one conversion step, but a copy is still required because Rust strings are not null-terminated.

**Collections**

Rust `Vec<T>` and .NET `List<T>` or arrays have completely different memory layouts. At the FFI boundary, options include:
- Passing a pointer + length (the "slice" approach): efficient for blittable element types but requires careful lifetime management.
- Serialization: copy elements one by one into a shared buffer.
- Opaque handles: pass a handle to the collection, with accessor functions to read elements.

**Error Handling**

Rust `Result<T, E>` and .NET exceptions are fundamentally incompatible:
- Rust expects errors to be returned as values.
- .NET exceptions are thrown and unwind the stack.
- Unwinding across FFI boundaries is **undefined behavior** in both directions.

The standard pattern (as demonstrated by Datalust's Seq project) is:
1. On the Rust side, wrap all `extern "C"` functions with `catch_unwind` to prevent panics from crossing the boundary.
2. Return error codes or status enums from Rust functions.
3. Store detailed error messages in thread-local storage, retrievable by a separate function call.
4. On the C# side, check the return code and throw a .NET exception if it indicates failure.

**Option<T> Mapping**

Rust `Option<T>` has no direct C ABI representation. Common approaches:
- For pointer types: map `None` to null.
- For value types: use a separate boolean flag or a `#[repr(C)]` struct with a discriminant.
- UniFFI handles this automatically through its serialization layer.

**Callback/Delegate Interop**

C# delegates and Rust closures cannot cross the FFI boundary directly. The standard approach:
- Pass C function pointers (`extern "C" fn(...)` on Rust side, `delegate* unmanaged<...>` on C# side).
- For closures with captured state, pass a context pointer alongside the function pointer.
- C# delegates must be pinned (to prevent GC relocation) and, on Mono/Unity, require `MonoPInvokeCallback`.

### Memory Management at the Boundary

This is the most critical and error-prone aspect of Rust/.NET interop:

- **Ownership rule**: Every allocated object must have exactly one owner responsible for freeing it. Each side must free its own allocations -- Rust memory via Rust's allocator, .NET memory via the GC.
- **Preventing double-free**: Never free memory on the wrong side. If Rust allocates a buffer and passes it to C#, C# must call back into Rust to free it. The `SafeHandle` pattern on the C# side ensures this happens even during exceptions.
- **Preventing use-after-free**: The GC can move or collect .NET objects at any time. Pointers into .NET memory must be **pinned** (`fixed` statement or `GCHandle.Alloc` with `Pinned`) before being passed to Rust. Conversely, Rust data passed to C# must remain valid for the duration of C#'s use.
- **Handle pattern** (used by Datalust/Seq): Rust allocates objects with `Box::new`, converts to raw pointer via `Box::into_raw`, and passes the pointer to C# as an `IntPtr`. C# wraps it in a `SafeHandle` subclass. When the `SafeHandle` is disposed/finalized, it calls back into Rust, which reconstructs the `Box` via `Box::from_raw` and drops it.

---

## 4. Deeper Integration Possibilities

### Beyond Raw FFI

Raw FFI (C ABI + P/Invoke) is the established approach but has fundamental limitations:

- **No shared type definitions**: Types must be defined separately in Rust and C# and kept in sync manually, or generated by tools like csbindgen/Interoptopus.
- **No zero-copy for complex types**: Strings, collections, and nested structures always require copying or serialization across the boundary.
- **No shared error model**: Error handling is bolted on via conventions (return codes, thread-local storage).

Cobalt could provide tighter integration by:

1. **Shared type definitions**: A single type definition in Cobalt that generates both .NET IL representations and Rust `#[repr(C)]` structures. This eliminates sync issues and is analogous to what cxx does for C++/Rust.

2. **Automatic binding generation**: Since Cobalt controls the compiler, it could emit both .NET P/Invoke declarations and Rust `extern "C"` functions from annotated Cobalt types and functions.

3. **Zero-copy data sharing for blittable types**: If Cobalt enforces `#[repr(C)]`-compatible layouts for certain types, the same memory could be accessed from both .NET and Rust without copying. This requires pinning on the .NET side and careful lifetime management.

4. **Integrated ownership tracking**: Cobalt's borrow checker could track ownership across the interop boundary, statically preventing double-free and use-after-free at the language level rather than relying on runtime patterns like `SafeHandle`.

### WebAssembly as an Interop Layer

Both Rust and .NET compile to WebAssembly, and the **WASM Component Model** defines a language-neutral interface mechanism:

- **WIT (WebAssembly Interface Types)** defines high-level data types (strings, lists, records, variants, enums) in a language-neutral IDL. Components that import/export the same WIT interface can interoperate regardless of source language.
- **Current status**: The Component Model specification is in draft (W3C Phase 2/3). Wasmtime is the first runtime with full support. WASI 0.2 is tightly coupled to the Component Model.
- **.NET support**: The [componentize-dotnet](https://github.com/bytecodealliance/componentize-dotnet) project (Bytecode Alliance) bundles the necessary tooling into a NuGet package. It requires .NET 10+ preview and is currently Windows-only, though macOS/Linux support is expected.
- **Rust support**: Rust's Component Model tooling (via `wit-bindgen` and `cargo-component`) is roughly a year ahead of other languages in maturity.

**Viability for Cobalt**: The WASM Component Model is promising as a **future** interop path, particularly for sandboxed or edge deployment scenarios. However, it has significant current limitations:
- Not production-ready for general use (specification still in draft).
- Single-threaded execution model (no threading support yet).
- Performance gap: WASI file I/O benchmarked at 10x slower than native.
- Immature tooling across all languages except Rust.
- Not supported in browsers yet.

For Cobalt's primary use case (native desktop/server applications combining .NET and Rust), direct FFI will remain more practical than WASM interop for the foreseeable future. The Component Model is worth monitoring as a long-term option.

### gRPC / Protocol Buffers

gRPC provides language-neutral RPC with protocol buffers for serialization:

- Both Rust (via `tonic`/`prost`) and .NET (via `Grpc.Net.Client` / `Grpc.AspNetCore`) have mature gRPC support.
- Protobuf schema files provide a single source of truth for message types.
- Works across process boundaries and networks, not just in-process.

**Trade-offs for Cobalt**:
- gRPC is appropriate for microservice architectures where Rust and .NET components run as separate processes.
- It is **not** suitable for tight in-process integration: serialization/deserialization overhead, network stack overhead, and the inability to share memory make it orders of magnitude slower than FFI for fine-grained calls.
- It does not help with ownership transfer or shared-memory scenarios.

### Shared Memory Approaches

For high-performance interop between separate processes (one Rust, one .NET):

- **Memory-mapped files**: Both Rust (`memmap2` crate) and .NET (`MemoryMappedFile`) can map the same file into their address space. With agreed-upon `#[repr(C)]` layouts, data can be shared without serialization.
- **Shared buffers with synchronization**: Ring buffers or lock-free queues in shared memory can enable high-throughput, low-latency communication.

These approaches bypass the FFI marshalling problem entirely for data exchange but introduce their own complexity (synchronization, cache coherence, platform-specific APIs). They are best suited for specific high-performance scenarios rather than general-purpose interop.

---

## 5. Implications for Cobalt

### .NET IL Targeting Gives .NET Interop for Free

If Cobalt compiles to .NET IL (CIL), it automatically gains:
- Full access to the .NET ecosystem: all NuGet packages, all .NET APIs.
- Seamless interop with C#, F#, VB.NET -- all languages on the .NET runtime interoperate at the IL level.
- Garbage collection, JIT compilation, and the full .NET type system.

The challenge is **Rust interop**, which requires explicit mechanisms since Rust does not target the .NET runtime (outside the experimental `rustc_codegen_clr`).

### Dual Code Generation: .NET IL + C ABI Exports

A compelling possibility for Cobalt: **generate both .NET IL and Rust-compatible C ABI exports from the same source**.

Consider a Cobalt type and function:

```cobalt
// Hypothetical Cobalt syntax
#[interop(rust)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}

#[interop(rust)]
pub fn distance(a: &Point, b: &Point) -> f64 { ... }
```

The Cobalt compiler could emit:
1. **.NET IL**: A `Point` struct and `distance` method usable from any .NET language.
2. **C ABI export**: An `extern "C"` function and `#[repr(C)]`-compatible struct, callable from Rust via standard FFI.
3. **Rust binding file**: A generated `.rs` file with `extern "C"` declarations and `#[repr(C)]` struct definitions, so Rust code can call Cobalt functions type-safely.

This dual-target approach would make Cobalt a natural bridge language. The NativeAOT compilation path already enables .NET-to-native exports via `[UnmanagedCallersOnly]`; Cobalt could automate this pattern.

### Ownership Semantics at the Interop Boundary

This is the hardest problem and the most valuable one Cobalt could solve. Current Rust/.NET interop has a fundamental gap: **ownership and lifetime information is lost at the FFI boundary**. Both sides must rely on conventions, documentation, and runtime checks rather than compiler enforcement.

Cobalt's borrow checker could bridge this gap:

1. **Owned transfers**: When Cobalt transfers ownership of a value to Rust (or vice versa), the borrow checker ensures the sender no longer accesses it. This is currently done manually with `Box::into_raw` / `Box::from_raw` patterns.

2. **Borrowed references**: When Cobalt lends a reference to Rust, the borrow checker ensures the reference does not outlive the borrowed data. Currently impossible to enforce statically across FFI.

3. **GC interaction**: Values on the .NET GC heap that are passed to Rust must be pinned. Cobalt could automatically insert pinning when a GC-heap value is passed to a Rust function, and statically verify that the Rust side does not store the pointer beyond the pinning scope.

4. **Drop/Dispose unification**: Cobalt could unify Rust's `Drop` trait and .NET's `IDisposable` pattern, ensuring that resources are cleaned up regardless of which runtime "owns" them.

The cxx crate's approach is a strong model: define a bridge interface with ownership annotations, and generate safe wrapper code on both sides. Cobalt could provide this as a language-level feature rather than a library.

### The Marshalling Cost Question: FFI vs Deeper Integration

**Is FFI-based interop sufficient?**

For coarse-grained interop (calling a Rust library for a CPU-intensive task, then returning the result to .NET), FFI overhead is negligible. The per-call cost of a P/Invoke is roughly 20-50 nanoseconds for blittable types, which is acceptable if each call does meaningful work.

For fine-grained interop (calling across the boundary thousands or millions of times per second, passing complex data structures), FFI becomes a bottleneck due to:
- Marshalling overhead for non-blittable types.
- String encoding conversions (UTF-8 to UTF-16 and back).
- Collection copying.
- Inability to share object graphs.

**Does Cobalt need deeper integration?**

Yes, if Cobalt aims to be more than "C# with a borrow checker." The value proposition of combining C# and Rust semantics implies tight integration, not just a better FFI generator. Specific scenarios requiring deeper integration:

- **Iterating over a Rust collection from Cobalt/.NET**: FFI requires either copying the entire collection or making one P/Invoke call per element. Deeper integration could enable zero-copy iteration.
- **Sharing object graphs**: Rust and .NET have incompatible memory models (manual ownership vs GC). Cobalt could define a third memory region with rules compatible with both.
- **Async interop**: Bridging Rust futures and .NET Tasks through FFI is possible but awkward (requires polling loops or callback chains). Cobalt could provide unified async semantics.
- **Generic type interop**: Neither FFI nor existing tools handle generic/parameterized types across the boundary. Cobalt could monomorphize generics at the interop boundary.

The `rustc_codegen_clr` project hints at the deepest possible integration: compiling Rust directly to .NET IL, eliminating the FFI boundary entirely. While that project is experimental, it demonstrates that the approach is technically feasible. Cobalt could explore a similar path -- not by compiling Rust to IL, but by designing a language whose semantics are compatible with both .NET IL execution and Rust-style ownership, minimizing or eliminating the need for marshalling.

---

## Sources

- [P/Invoke source generation - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)
- [Native code interop with Native AOT - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)
- [Native interoperability best practices - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices)
- [Blittable and Non-Blittable Types - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/framework/interop/blittable-and-non-blittable-types)
- [ComWrappers source generation - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation)
- [COM Interop in .NET - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/cominterop)
- [SafeHandle Class - .NET | Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.safehandle)
- [FFI - The Rustonomicon](https://doc.rust-lang.org/nomicon/ffi.html)
- [Other reprs - The Rustonomicon](https://doc.rust-lang.org/nomicon/other-reprs.html)
- [CXX -- safe interop between Rust and C++](https://cxx.rs/)
- [csbindgen - Cysharp/csbindgen on GitHub](https://github.com/Cysharp/csbindgen)
- [uniffi-bindgen-cs - NordSecurity on GitHub](https://github.com/NordSecurity/uniffi-bindgen-cs)
- [Interoptopus on GitHub](https://github.com/ralfbiedert/interoptopus)
- [UniFFI - mozilla/uniffi-rs on GitHub](https://github.com/mozilla/uniffi-rs)
- [rust-bindgen on GitHub](https://github.com/rust-lang/rust-bindgen)
- [cbindgen on GitHub](https://github.com/mozilla/cbindgen)
- [componentize-dotnet - Bytecode Alliance on GitHub](https://github.com/bytecodealliance/componentize-dotnet)
- [rustc_codegen_clr on GitHub](https://github.com/FractalFir/rustc_codegen_clr)
- [WASI and the WebAssembly Component Model: Current Status](https://eunomia.dev/blog/2025/02/16/wasi-and-the-webassembly-component-model-current-status/)
- [How we integrate Rust with C# - Datalust](https://datalust.co/blog/rust-at-datalust-how-we-integrate-rust-with-csharp)
- [C++/CLI and mixed mode programming | Microsoft Learn](https://learn.microsoft.com/en-us/archive/blogs/abhinaba/ccli-and-mixed-mode-programming)
