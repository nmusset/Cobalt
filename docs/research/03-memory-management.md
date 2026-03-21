# Phase 1.3: Memory Management — Comparative Analysis

This document covers the third research topic from the Cobalt roadmap: how memory is allocated, tracked, and reclaimed in .NET and Rust, and what these mechanics imply for a language that combines both models on the .NET runtime.

This document intentionally does not revisit ownership rules or type system design — those are covered in documents 01 and 02. The focus here is on the runtime machinery of memory management itself.

---

## 1. .NET's Garbage Collector

### Architecture Overview

The .NET garbage collector is a generational, tracing, compacting collector. It is non-deterministic: the runtime decides when to collect, and application code cannot predict or control the exact moment an unreachable object's memory is reclaimed. The GC manages all reference-type allocations on the **managed heap** — a contiguous region of virtual address space that the runtime grows and shrinks as needed.

The GC's core invariant is liveness: an object is live if it is reachable from a **root**, and dead otherwise. Dead objects' memory is reclaimed; live objects may be relocated (compacted) to eliminate fragmentation. Application code never directly frees managed memory.

### Generational Collection

The GC partitions the managed heap into three logical generations, each reflecting object age (measured in surviving collections):

**Generation 0 (Gen 0):** All newly allocated objects (below the large object threshold) start here. Gen 0 is small — typically a few hundred kilobytes to a few megabytes — sized to fit roughly within the CPU's L2 cache. Collections are fast because most objects are short-lived: the GC marks the few survivors, promotes them to Gen 1, and reclaims the rest. Gen 0 collections are the most frequent.

**Generation 1 (Gen 1):** A buffer between short-lived and long-lived objects. Objects that survive a Gen 0 collection are promoted here. Gen 1 collections are less frequent than Gen 0 and also collect Gen 0 simultaneously. Objects surviving a Gen 1 collection are promoted to Gen 2.

**Generation 2 (Gen 2):** Contains long-lived objects — singletons, caches, static data structures. Gen 2 collections are the most expensive: they scan the entire managed heap (a **full collection**). Because Gen 2 can grow large, full collections involve significant pause times if done in a blocking (foreground) manner. Background GC exists specifically to mitigate this.

The generational hypothesis — most objects die young — makes this scheme efficient. The GC avoids scanning long-lived objects on every cycle; it only examines Gen 2 when necessary.

### The Large Object Heap (LOH)

Objects at or above 85,000 bytes are allocated directly on the Large Object Heap, which is logically part of Gen 2 and collected only during full (Gen 2) collections. The LOH differs from the small object heap (SOH) in one critical respect: **it is not compacted by default**. Compacting the LOH would require copying large blocks of memory, which is expensive — and the GC would need twice the memory temporarily to relocate objects. Instead, the LOH uses a free-list allocator: when large objects are collected, their space is added to a free list and reused for future large allocations.

The consequence is that the LOH is susceptible to **fragmentation**. If an application allocates and frees large objects of varying sizes, the free list may contain many small gaps that cannot satisfy a new large allocation, even though total free space is sufficient. This forces the runtime to commit additional virtual memory segments.

Starting with .NET 4.5.1, `GCSettings.LargeObjectHeapCompactionMode` allows applications to request LOH compaction on the next full blocking GC. This is a one-shot setting (it resets after the compaction completes) and should be used sparingly — compacting a large LOH is expensive. Arrays of doubles at or above 1,000 elements (8,000 bytes) are also allocated on the LOH as a performance optimization for alignment, though this detail is an implementation artifact rather than a specification guarantee.

### The Pinned Object Heap (POH)

Introduced in .NET 5, the Pinned Object Heap is a dedicated heap segment for objects that must not be moved by the GC. Before the POH, pinning was done via `GCHandle.Alloc(..., GCHandleType.Pinned)` or the `fixed` statement, which pinned objects in place on the SOH. Pinned objects on the SOH prevent compaction of the surrounding region, creating fragmentation that degrades allocation performance — especially when pins are scattered across generations.

The POH solves this by isolating pinned objects in their own region, where the GC never attempts compaction. This eliminates the fragmentation impact on the SOH entirely. The POH uses a free-list allocator (like the LOH) and allocations are synchronized rather than using per-thread allocation contexts, making POH allocation somewhat slower than SOH allocation. The tradeoff is worthwhile for objects that would otherwise be pinned for extended periods (network I/O buffers, interop buffers).

The GC can skip tracing POH objects during certain collection phases, providing an additional performance benefit. The POH is not intended for short-lived pinned objects — for those, `fixed` on the SOH remains preferable because the object can be reclaimed quickly in a Gen 0 collection.

### GC Root Walking

The GC determines object liveness by tracing from **roots** — known-live references that serve as starting points for the reachability graph. The runtime maintains several categories of roots:

**Stack roots:** Local variables and parameters on each managed thread's stack that hold references to heap objects. The JIT compiler produces **GC info** alongside the machine code for each method — metadata that tells the GC exactly which stack slots and CPU registers contain managed references at each possible suspension point (safe point). The stack walker uses this GC info to enumerate roots precisely. JIT optimizations (register allocation, dead variable elimination) affect which roots are reported: a variable that the JIT has determined is dead (no further reads) may not be reported as a root even if the source code suggests it is still in scope.

**Static fields:** Static fields of loaded types are roots for the lifetime of the containing assembly (or the entire process, for most practical purposes). Each `AssemblyLoadContext` tracks its static roots; unloading a context removes those roots.

**GC handles:** The `GCHandle` API provides explicit control over root strength. `GCHandleType.Normal` creates a strong root (prevents collection). `GCHandleType.Weak` creates a weak reference (does not prevent collection; the handle is cleared when the target is collected). `GCHandleType.WeakTrackResurrection` is similar but survives finalization. `GCHandleType.Pinned` creates a strong root and additionally prevents the GC from moving the object.

**The finalization queue:** Objects with finalizers are registered on the finalization queue at allocation time. This queue acts as a root — even if no other reference exists, the finalization queue keeps the object alive until the finalizer has run.

**Interior pointers and byrefs:** `ref` locals, `ref` returns, and `Span<T>` can point into the interior of a managed object (not just its header). The GC info emitted by the JIT includes these interior pointers so the GC can identify the containing object and update the pointer if the object is relocated.

### The Mark Phase

During the mark phase, the GC starts from all roots and transitively marks every reachable object. It uses a mark bit (or mark array) to track visited objects. The mark phase's cost is proportional to the number of live objects, not the total heap size — a heap with many dead objects and few live ones is cheap to mark.

For generational collections (Gen 0 or Gen 1), the GC does not trace the entire heap. Instead, it uses **card tables** — a data structure where each entry covers a small region of memory (typically 256 bytes). When managed code writes a reference into an older-generation object that points to a younger-generation object, the JIT-emitted **write barrier** marks the corresponding card as dirty. During a young-generation collection, the GC only examines dirty cards to find cross-generational references, avoiding a full scan of Gen 2.

### Compaction

After marking, the GC may compact the surviving objects — sliding them toward the beginning of the generation to eliminate gaps left by dead objects. Compaction updates all references (roots, interior pointers, and inter-object references) to reflect the new addresses.

Compaction has several implications:

- **Object addresses are unstable.** A managed object can move at any GC that compacts its generation. Raw pointers to managed objects are invalid unless the object is pinned. This is why `fixed` and `GCHandle.Pinned` exist — they tell the GC not to move a specific object.
- **Allocation is fast.** Because compaction eliminates fragmentation, the SOH allocator is a simple bump allocator: it maintains a pointer to the next free byte and advances it. Allocation is a pointer increment plus a bounds check — comparable to stack allocation in cost.
- **Compaction cost is proportional to the volume of surviving data** that must be copied. For Gen 0, this is usually very small. For Gen 2, it can be substantial, which is why background GC avoids compacting Gen 2 when possible.

The GC decides whether to compact based on heuristics: fragmentation ratio, survival rate, and available memory. It may choose to **sweep** instead — leaving objects in place and building a free list from the gaps — if compaction would be too expensive relative to the fragmentation it would eliminate.

### Background and Concurrent GC

The .NET GC supports **background collection** (the successor to the older "concurrent GC") for Gen 2 collections. Background GC runs the mark phase of a Gen 2 collection on a dedicated GC thread concurrently with application (mutator) threads. The goal is to minimize pause times for full collections.

During a background Gen 2 collection, foreground Gen 0 and Gen 1 collections can still occur — application threads are only briefly suspended for the initial root scan and the final phase of the background collection. Background GC does not compact Gen 2; it sweeps instead, building a free list. Compaction of Gen 2 requires a blocking (foreground) full GC.

In **workstation GC** mode (the default for client applications), one dedicated background GC thread handles Gen 2 collections. Workstation GC prioritizes low pause times and responsiveness.

In **server GC** mode (intended for server workloads), the runtime creates one GC heap and one dedicated GC thread per logical processor. Collections run in parallel across all GC threads, maximizing throughput. Server GC heaps are larger, collections are less frequent but pause all managed threads for the full duration of foreground collections. Background server GC runs background Gen 2 collection threads per processor as well.

The choice between workstation and server GC is a throughput-vs-latency tradeoff. Server GC achieves higher total throughput by parallelizing collection work but introduces longer individual pauses. Workstation GC keeps pauses shorter at the cost of lower total throughput.

### GC Latency Modes and Tuning

The `GCSettings.LatencyMode` property controls how aggressively the GC collects:

- **`Batch`**: Disables concurrent/background GC entirely. Collections are always blocking. Maximum throughput, maximum pause times. Suitable for batch processing where latency is irrelevant.
- **`Interactive`** (default): Enables background Gen 2 collection. Balances throughput and responsiveness.
- **`LowLatency`**: Suppresses full Gen 2 collections except under memory pressure. For short-duration, latency-critical code paths (e.g., animations, real-time UI updates). Should not be held for extended periods — the heap grows without Gen 2 collection.
- **`SustainedLowLatency`**: Suppresses foreground (blocking) Gen 2 collections while allowing background Gen 2 collections. Allows latency-sensitive applications to run for longer periods without blocking full GCs. The managed heap grows larger in this mode.
- **`NoGCRegion`**: Not set via `LatencyMode` — entered via `GC.TryStartNoGCRegion(size)` and exited via `GC.EndNoGCRegion()`. Pre-allocates the specified amount of memory and suppresses all garbage collections until the region ends or the pre-allocated budget is exhausted. For hard-real-time code paths where any GC pause is unacceptable.

Additional tuning surfaces include `GCSettings.IsServerGC`, the `DOTNET_gcServer` environment variable, `DOTNET_GCHeapCount` (limiting server GC heaps), `DOTNET_GCConserveMemory` (trading throughput for lower memory footprint), and `DOTNET_GCHeapHardLimit` (capping total heap size, useful in containers).

### Regions-Based GC

Starting in .NET 7 (enabled by default for 64-bit platforms), the GC replaced its internal **segment-based** heap representation with **regions**. Under the old model, the heap was divided into large, fixed-size segments (typically 256 MB on 64-bit), and each segment belonged to a single generation. This was inflexible: memory could not be transferred between generations or between the SOH and LOH without a compacting GC.

Regions are smaller, uniformly-sized chunks of memory (typically 4 MB). Each region is assigned a generation, but this assignment is dynamic — a region can be **demoted** (moved from a higher generation to a lower one) or repurposed entirely. This enables several optimizations:

- **Dynamic Promotion and Demotion (DPAD):** Regions can change generation assignment based on heuristics. If Gen 2 has excess free space and Gen 0 is under pressure, regions can be reassigned. Under segments, demotion was limited to a single contiguous range at the end of the ephemeral segment.
- **Better memory reuse:** Free regions can be returned to the OS or reused by any generation. Under segments, empty segments could only be released whole, and a segment with even one live object could not be freed.
- **Improved server GC load balancing:** In server GC mode, regions can be moved between heaps (one per processor) to balance survivors more evenly, reducing skew that previously caused some heaps to grow while others had excess capacity.

.NET 8 further refined regions-based GC with improved compaction algorithms and better adaptation to varying workload patterns. .NET 9 continued with compaction efficiency improvements that reduce fragmentation with less data movement.

---

## 2. Rust's RAII Model

### Deterministic Destruction via Drop

Rust's memory management is built on RAII (Resource Acquisition Is Initialization): every value has a single owner, and when that owner goes out of scope, the value is destroyed — its `Drop::drop` method (if implemented) is called, followed by recursive drops of all fields. There is no garbage collector, no finalization queue, no background thread. Destruction happens at a deterministic, compiler-determined point in the program.

The `Drop` trait defines a single method: `fn drop(&mut self)`. When the compiler determines that a value's lifetime has ended (typically at the closing brace of its scope, or earlier if the value is explicitly dropped via `std::mem::drop`), it inserts the call to `drop` automatically. The programmer never calls `drop` directly on `&mut self` — they call `std::mem::drop(value)`, which takes ownership and lets the implicit drop run at the end of `drop`'s scope.

After `Drop::drop` executes, the compiler recursively drops each field. The programmer's `Drop` implementation handles the type's own cleanup logic (closing file handles, flushing buffers, releasing locks); the compiler handles the structural teardown.

### Drop Order Guarantees

Rust specifies drop order precisely, and this is a language guarantee (stabilized in RFC 1857):

**Local variables** are dropped in **reverse declaration order** (last declared, first dropped). This mirrors C++ destructor ordering and follows the intuition that later-declared variables may depend on earlier-declared ones.

**Struct fields** are dropped in **declaration order** (first declared, first dropped). This is the opposite of locals and the opposite of C++, where member destructors run in reverse declaration order. The asymmetry is a historical design decision that has been preserved for stability. It means that if field `a` is declared before field `b`, `a` is dropped before `b`.

**Tuple fields** are dropped in order (first element first). **Enum variant fields** are dropped in declaration order, matching structs. **Array and `Vec` elements** are dropped in order (index 0 first).

**Temporaries** created in a statement are dropped at the end of the statement, in reverse order of creation (with some exceptions for temporaries in `let` bindings, which live for the enclosing scope).

These guarantees are important for correctness in Rust: drop order determines the order in which locks are released, files are closed, and resources are freed. Getting the order wrong can cause deadlocks or use-after-close bugs, even though memory safety is still guaranteed by the borrow checker.

### ManuallyDrop and Suppressing Destruction

`std::mem::ManuallyDrop<T>` is a `#[repr(transparent)]` wrapper that inhibits the automatic drop of the wrapped value. The value's `Drop` implementation will not run unless explicitly triggered via `ManuallyDrop::drop(&mut value)` (an unsafe operation — calling it twice is undefined behavior, equivalent to double-free).

`ManuallyDrop` serves several purposes:

- **Preventing double-free in unsafe code.** When transferring ownership across FFI boundaries or into raw pointers, wrapping a value in `ManuallyDrop` prevents the compiler from emitting a drop call for the original binding.
- **Controlling drop order.** By wrapping some fields in `ManuallyDrop` and dropping them explicitly in the containing type's `Drop` implementation, a programmer can override the default field drop order.
- **Union fields.** Rust unions can only contain `Copy` types or `ManuallyDrop<T>` — because the compiler does not know which variant is active, it cannot safely drop the contents.

### std::mem::forget

`std::mem::forget(value)` takes ownership of `value` and prevents its destructor from running, without deallocating the memory. It is implemented as `ManuallyDrop::new(value);` followed by the `ManuallyDrop` going out of scope (which, by definition, does nothing). `forget` is a **safe function** — it cannot cause undefined behavior.

### Leaking Is Safe, Not Desirable

A foundational Rust design decision (RFC 1066) is that **leaking memory (failing to run destructors) is safe**. "Safe" in Rust means specifically "cannot cause undefined behavior" — it does not mean "correct" or "desirable." Leaking is often a logic bug: a leaked `MutexGuard` means the lock is never released, a leaked `File` means the file descriptor is never closed, a leaked `Vec` means its heap allocation is never freed.

The decision to make `forget` safe was driven by the observation that leaking is already possible through entirely safe code paths: `Rc` reference cycles, `std::process::exit`, infinite loops, and panics that unwind past a value. If the type system could not prevent leaking in these cases, making `forget` unsafe would provide a false sense of security without actually guaranteeing that destructors run.

This has a concrete design consequence: **safe Rust APIs must not rely on destructors for memory safety.** A type whose safety invariant depends on its `Drop` running (e.g., a scoped thread guard that joins on drop) must either use lifetime constraints to prevent leaking or accept that leaking is a possible (if buggy) outcome. The `std::thread::scope` API handles this by using lifetime-bounded closures that the compiler prevents from being leaked.

---

## 3. Deterministic Destruction in C#

### IDisposable and the using Pattern

C# addresses deterministic resource cleanup through the `IDisposable` interface and the `using` statement. `IDisposable` defines a single method: `void Dispose()`. The `using` statement (or `using` declaration in C# 8+) ensures `Dispose()` is called when control leaves the scope, whether normally or via exception.

The `using` statement compiles to a `try`/`finally` block: the object is allocated before the `try`, and `Dispose()` is called in the `finally`. This guarantees cleanup even if an exception is thrown — but only if the object is used within a `using`. Nothing in the language or runtime forces callers to use `using`; `IDisposable` is a convention, not an enforcement mechanism.

Key differences from Rust's `Drop`:

- **Not automatic.** The caller must explicitly use `using` or call `Dispose()`. Forgetting to do so is a common source of resource leaks. Static analysis tools (IDE warnings, Roslyn analyzers) flag missing `Dispose` calls, but this is advisory, not a compiler error.
- **Not tied to ownership.** Multiple references to the same `IDisposable` object can exist. Disposing through one reference does not invalidate the others — calling methods on a disposed object is a runtime error (`ObjectDisposedException`), not a compile-time one.
- **Not recursive.** `Dispose()` does not automatically dispose fields. The programmer must implement `Dispose()` to call `Dispose()` on each disposable field. The standard dispose pattern (virtual `Dispose(bool disposing)`, called from both `Dispose()` and the finalizer) is a convention, not compiler-generated.
- **Idempotent by convention.** The guidelines state that `Dispose()` should be safe to call multiple times without throwing. This is not enforced.

### Finalizers

A C# finalizer (declared as `~ClassName()`) is a method invoked by the GC when it determines that an object is unreachable — but not immediately. Finalizers exist as a safety net for unmanaged resources that were not properly disposed.

**The finalization lifecycle:**

1. **Registration.** When an object with a finalizer is allocated, the runtime registers it on the **finalization queue**. This registration is implicit and automatic. The finalization queue is a GC root — objects on it are kept alive even if no other reference exists.

2. **First collection: promotion to f-reachable.** When the GC determines that a finalizable object is otherwise unreachable, it does not reclaim the object. Instead, it moves the object from the finalization queue to the **f-reachable queue** (sometimes called the "freachable" queue). The object is now considered live again — it has been **resurrected** — because the f-reachable queue is a root.

3. **Finalizer execution.** A dedicated **finalizer thread** (a single thread, running at `THREAD_PRIORITY_HIGHEST`) dequeues objects from the f-reachable queue and calls their finalizers. The finalizer thread runs asynchronously — there is no guarantee about when it will execute relative to other threads, and no guarantee about ordering between finalizers of different objects.

4. **Second collection: actual reclamation.** After the finalizer runs and the object is removed from the f-reachable queue, the object is again rootless. The next GC cycle that reaches the object's generation can reclaim it. This means **finalizable objects survive at least two GC cycles** — they are promoted to a higher generation than non-finalizable objects of the same age, increasing GC pressure.

**Object resurrection.** If a finalizer stores a reference to `this` (or any of its reachable objects) in a static field or other root, the object becomes reachable again and will not be collected. `GC.ReRegisterForFinalize(this)` can even re-register the object for finalization, causing the cycle to repeat. Resurrection is rarely useful and widely considered dangerous.

**`GC.SuppressFinalize`.** The standard dispose pattern calls `GC.SuppressFinalize(this)` inside `Dispose()`. This removes the object from the finalization queue, so the GC can reclaim it in a single cycle instead of two. It is the primary mechanism for avoiding the finalization performance penalty when `Dispose` has already cleaned up.

### IAsyncDisposable and await using

.NET Core 3.0 / C# 8 introduced `IAsyncDisposable`, which defines `ValueTask DisposeAsync()`. The `await using` statement calls `DisposeAsync()` in the generated `finally` block, enabling async cleanup (e.g., flushing an async stream, sending a close frame on a WebSocket).

The pattern for non-sealed classes is to provide a `protected virtual ValueTask DisposeAsyncCore()` method, called from `DisposeAsync()`. `DisposeAsync()` should call `GC.SuppressFinalize(this)` if the type has a finalizer.

`IAsyncDisposable` does not replace `IDisposable` — Microsoft recommends implementing both when async cleanup is needed, because some callers may only have synchronous disposal paths. Finalizers cannot be async (the finalizer thread is synchronous), so `IAsyncDisposable` does not interact with finalization.

### The Fundamental Tension

The .NET model creates a tension between two conflicting goals:

- **GC wants non-determinism.** The GC's efficiency depends on batching work and deferring reclamation. Forcing immediate cleanup (via `Dispose`) works against this by requiring the programmer to manually track resource lifetimes — exactly the burden the GC was designed to eliminate.
- **Resources need determinism.** File handles, database connections, network sockets, GPU resources, and locks must be released promptly. Waiting for the GC (or the finalizer thread) to reclaim them risks exhausting limited resources long before memory pressure triggers collection.

The result is a bifurcated model: memory is managed by the GC; everything else must be managed manually through `IDisposable`. Programmers must distinguish between "things the GC handles" and "things I handle" — and must remember to write `using` for the latter. This dual model is a persistent source of bugs and a significant departure from Rust's unified RAII approach where all resources (memory and non-memory) are reclaimed through the same deterministic mechanism.

---

## 4. Pinning in Rust

### Why Pinning Exists

Rust's ownership model permits moving values freely — a move is a bitwise copy to a new location, and the old location is invalidated. For most types, this is correct: their behavior does not depend on their memory address. But some types are **address-sensitive** — they contain internal pointers (pointers from one part of the struct to another) that become invalid if the struct is moved.

The primary motivating case is **async/await**. When the Rust compiler transforms an `async fn` into a state machine, the generated `Future` type may hold both a local variable and a reference to that variable across an `.await` point. This creates a self-referential struct: the struct contains a field that points to another field within the same struct. If the `Future` is moved, the internal pointer becomes dangling.

### Pin<T>

`Pin<P>` is a wrapper around a pointer type `P` (typically `Pin<&mut T>` or `Pin<Box<T>>`) that **restricts access** to the pointee. Specifically, `Pin<P>` prevents obtaining a `&mut T` from the pinned pointer — because `&mut T` would allow calling `std::mem::swap` or `std::mem::replace`, both of which move the value. By preventing `&mut T` access, `Pin` ensures the value cannot be moved out of its current location.

`Pin` does not change where the value is allocated. A `Pin<Box<T>>` still uses heap allocation; a `Pin<&mut T>` can point to a stack location. What `Pin` guarantees is that once pinned, the value will not be moved until it is dropped.

### The Unpin Trait

`Unpin` is an auto trait — almost all types implement it automatically. A type that implements `Unpin` is declaring that it is safe to move even after being pinned. For `Unpin` types, `Pin<&mut T>` degrades to `&mut T` — you can freely get a mutable reference and move the value.

Types that are `!Unpin` (do not implement `Unpin`) are the ones that actually benefit from pinning. The compiler-generated `Future` types from `async` blocks are `!Unpin` because they may be self-referential. To create a `!Unpin` type manually, a type includes a field of type `PhantomPinned`.

### Pin Projections

When working with a pinned struct, accessing fields requires **pin projections** — methods that convert a `Pin<&mut Struct>` into pinned (or unpinned) references to individual fields. Whether a field's projection should be pinned (returning `Pin<&mut Field>`) or unpinned (returning `&mut Field`) depends on whether the field's address is structurally significant:

- A field that is part of the self-referential structure (e.g., pointed to by another field) must have a **structural pin projection** — the projection returns `Pin<&mut Field>`, maintaining the pin guarantee.
- A field that is independent of the pinning invariant can have a **non-structural projection** — the projection returns `&mut Field`, allowing the field to be moved independently.

Pin projections are manually written (or generated by libraries like `pin-project` and `pin-project-lite`). Writing them correctly is subtle: the rules for structural pinning include that the struct must not implement `Unpin` if any structurally-pinned field is `!Unpin`, must not offer any API that moves structurally-pinned fields out, and the struct's `Drop` implementation must not move structurally-pinned fields.

### Relationship to Memory Management

Pin is orthogonal to allocation. It does not affect where memory is allocated or how it is freed. It is a **type-level constraint** that prevents a specific class of operation (moving) to preserve pointer validity within a value. It interacts with memory management only indirectly: a pinned value on the heap (via `Pin<Box<T>>`) will be freed when the `Box` is dropped, just like any other `Box`. The pin prevents movement, not deallocation.

---

## 5. Pinning in .NET

### Why Pinning Is Needed

The .NET GC's compaction phase moves objects to eliminate fragmentation. This is transparent to managed code — the GC updates all managed references to reflect new addresses. But native code (C/C++ DLLs, OS APIs, hardware DMA buffers) does not use managed references. When managed data is passed to native code via P/Invoke or COM interop, the native code holds a raw pointer. If the GC moves the object while native code is using the pointer, the result is memory corruption.

Pinning tells the GC: do not move this object. The object remains at its current address for the duration of the pin, and native code can safely use the raw pointer.

### Pinning Mechanisms

**The `fixed` statement.** Used in `unsafe` code to pin a managed array or string and obtain a raw pointer. The object is pinned for the duration of the `fixed` block. The JIT reports the pinned reference to the GC via GC info, so the GC knows not to move it. `fixed` is the lightest-weight pinning mechanism — it has no allocation overhead and is scoped. The pinning duration should be as short as possible.

**`GCHandle.Alloc(..., GCHandleType.Pinned)`.** Creates a GC handle that pins the target object. The handle must be explicitly freed via `GCHandle.Free()`. This is necessary when the pin must outlive a single statement — for example, when a buffer is passed to an async native operation that completes later. Only objects with blittable types (no managed references in their fields) can be pinned via `GCHandle`. The handle itself is a root, so the object will not be collected while the handle exists.

**The Pinned Object Heap (POH).** Allocating directly on the POH via `GC.AllocateArray<T>(length, pinned: true)` (or `GC.AllocateUninitializedArray<T>(length, pinned: true)`) creates an array that is permanently pinned without needing a `GCHandle`. The object lives on the POH for its entire lifetime. This is the recommended approach for long-lived pinned buffers (e.g., I/O buffers pooled by `System.Net.Sockets` or ASP.NET Core's memory pool).

### Performance Implications of Pinning

Pinning on the SOH has two costs:

1. **Fragmentation.** A pinned object cannot be moved during compaction. The GC must compact around it, leaving gaps. If many objects are pinned at scattered locations, compaction becomes ineffective and the heap fragments. This is the primary motivation for the POH — moving long-lived pins off the SOH.

2. **Promotion.** Pinned objects survive collections (they are roots) and get promoted to older generations. If many short-lived objects are pinned, they pollute Gen 1 and Gen 2 with objects that would otherwise have been collected in Gen 0.

The POH avoids both problems by isolating pins, but at the cost of slower allocation (free-list rather than bump allocator) and the loss of generational collection benefits. POH objects are effectively Gen 2 for collection purposes.

### Contrast with Rust's Pin

The two "pin" concepts solve different problems:

- **.NET pinning** prevents the GC from **relocating** an object. It exists because the GC moves objects by default (compaction). It is about **physical address stability** for native interop.
- **Rust's `Pin<T>`** prevents application code from **moving** a value via safe APIs. It exists because Rust's ownership model moves values freely by default. It is about **logical address stability** for self-referential types.

A Cobalt language on .NET would need to deal with both concepts: .NET-style pinning for interop (preventing GC relocation), and potentially Rust-style pinning if it supports self-referential types or Rust-compatible async state machines.

---

## 6. Interior Mutability and Memory

### Rust: Cell and RefCell

Rust's default rule is that shared references (`&T`) are immutable and exclusive references (`&mut T`) are required for mutation. Interior mutability types relax this, allowing mutation through `&T`.

**`Cell<T>`** (for `Copy` types): Provides `get()` and `set()` methods that copy values in and out. `Cell` has **zero runtime overhead** for the mutation itself — no reference counting, no dynamic borrow checks. The mutation is achieved through `UnsafeCell<T>` internally, which is the language's primitive for interior mutability that tells the compiler not to assume immutability through shared references. `Cell<T>` is the same size as `T` (it is `#[repr(transparent)]` over `UnsafeCell<T>`, which is `#[repr(transparent)]` over `T`). The cost is purely in what the compiler cannot optimize: it cannot cache values read through `&Cell<T>` across operations that might call `set()`.

**`RefCell<T>`** (for non-`Copy` types): Provides `borrow()` and `borrow_mut()` methods that return smart-pointer guards (`Ref<T>` and `RefMut<T>`). These enforce Rust's borrowing rules at runtime: multiple `Ref<T>` guards can coexist, but `RefMut<T>` is exclusive — attempting to `borrow_mut()` while any borrow is active panics. The runtime cost is an `isize`-sized counter (8 bytes on 64-bit) stored alongside the data, incremented/decremented on every borrow/release. `RefCell<T>` is therefore `size_of::<T>() + size_of::<isize>()` (plus alignment padding). The runtime check is a comparison and branch on each borrow — cheap individually but not zero-cost.

**`Mutex<T>`, `RwLock<T>`**: Thread-safe interior mutability. These use OS synchronization primitives and have more substantial runtime overhead (system calls for contended locks, memory for the mutex state).

Interior mutability in Rust affects memory layout by adding storage for the runtime tracking mechanism (borrow count, lock state). It does not interact with memory management directly — these types are allocated and freed through the same ownership/drop mechanism as any other type.

### .NET: Mutable by Default

C# objects are mutable by default. Any code holding a reference to an object can mutate it (subject to access modifiers on individual members). There is no equivalent of Rust's `&T`/`&mut T` distinction at the reference level — all references are mutable references, and immutability is opt-in via `readonly` fields, `init`-only properties, and record types.

This means .NET has no need for `Cell`/`RefCell`-style patterns — the problem they solve (mutation through shared references) does not arise because all references permit mutation. The tradeoff is that .NET provides no compile-time guarantee against data races or aliased mutation. Thread safety is the programmer's responsibility, enforced through `lock`, `Interlocked`, `volatile`, or concurrent collection types.

From a memory management perspective, .NET's mutable-by-default model has one relevant implication: **write barriers**. Every reference-type field assignment requires a write barrier — a small piece of code emitted by the JIT that updates the GC's card table. This cost exists regardless of whether the mutation is "interior" or not; it is a consequence of the GC needing to track cross-generational references. Rust has no equivalent cost because it has no GC.

### Memory Layout Consequences

In Rust, choosing `Cell<T>`, `RefCell<T>`, or `Mutex<T>` directly affects the memory layout and size of the containing type. This is a consequence of Rust's explicit memory model — the programmer controls layout.

In .NET, all reference-type objects have a fixed overhead (object header + method table pointer, typically 16 bytes on 64-bit) regardless of their mutability characteristics. The GC does not treat mutable and immutable objects differently. A `readonly` field generates the same IL as a regular field — it is enforced at the language level, not the runtime level (though the JIT can exploit `readonly` for optimization).

---

## 7. Implications for Cobalt

### The Central Question

Cobalt proposes combining Rust-style ownership semantics with a .NET runtime that is fundamentally built around garbage collection. This creates a tension that requires deliberate design choices rather than naive composition.

### Could Ownership Reduce GC Pressure?

Yes, in several concrete ways:

**Stack allocation of owned values.** If the compiler can prove that a value has a single owner and does not escape the current method, it can allocate the value on the stack (as a value type in CLR terms) even if it would normally be a reference type. This avoids a heap allocation entirely — no GC tracking, no object header overhead, no collection cost. The .NET JIT already performs **escape analysis** in limited cases (since .NET 6, with `[StackAlloc]` and object stack allocation in some scenarios), but a language with ownership tracking has far more information available: if a value is owned and not borrowed beyond the current scope, it is provably non-escaping.

**Deterministic freeing reduces live-set size.** In .NET, objects remain on the heap until the GC collects them — which may be long after they become unreachable. With ownership semantics, the compiler knows exactly when a value's lifetime ends and can emit cleanup immediately (for value types, this is free; for heap-allocated objects, the compiler could null out the reference at the ownership boundary, making the object eligible for collection sooner). This reduces the live-set size at any given point, making GC cycles faster (less to mark) and less frequent (less memory pressure).

**Fewer defensive copies.** C#'s value type semantics require defensive copies in many scenarios (passing structs to methods, accessing members of readonly struct fields). Ownership and borrowing semantics can eliminate these copies because the compiler can prove that the borrowed reference will not be used to mutate the value when `&T` is used.

**Reduced need for finalizers.** If deterministic destruction is the default, fewer objects need finalizers as a safety net. This avoids the two-pass collection penalty and the finalization thread overhead.

### Could Deterministic Drop Be Layered on .NET?

This is the most technically interesting question. Several approaches are possible, each with tradeoffs:

**Approach 1: Compiler-inserted Dispose calls (IDisposable integration).** The Cobalt compiler could treat owned values like C# `using` declarations — automatically inserting `Dispose()` calls when ownership ends. This requires all owned types to implement `IDisposable`. Drop order would be deterministic and compiler-controlled. This approach works within the existing .NET model and interoperates cleanly with C# code.

Limitations: `Dispose()` handles resource cleanup but not memory reclamation — the GC still frees the object. The value remains on the heap after `Dispose()` until collected. There is no guarantee that the object is inaccessible after `Dispose()` — other references may exist and call methods on the disposed object. This is a weaker guarantee than Rust's `Drop`, where the value is destroyed and its memory is invalidated.

**Approach 2: Null-on-drop with compiler enforcement.** The compiler nulls out all references to an owned value when its lifetime ends, immediately after calling `Dispose()`. Combined with ownership tracking, the compiler guarantees that no other references exist (because ownership is exclusive). The object becomes unreachable immediately, eligible for collection in the next Gen 0 sweep.

This gets closer to Rust semantics but still defers memory reclamation to the GC. The benefit is reducing the window between "value is dead" and "memory is reclaimed" — in practice, Gen 0 collections are frequent enough that the delay is usually short.

**Approach 3: Arena/region-based allocation.** Owned values could be allocated in arenas (memory pools) that are freed in bulk when a scope exits. The CLR does not natively support arenas for managed objects, but `NativeMemory.Alloc` and `Span<T>` can be used for unmanaged arenas. Managed arenas could be simulated using `ArrayPool<T>` or custom allocators that reuse memory. This approach can dramatically reduce GC pressure for allocation-heavy workloads but adds complexity to the runtime model.

**Approach 4: Value types for owned data.** Cobalt could default to value-type (struct) representation for owned data, using the heap only when necessary (boxing, interface dispatch, or explicit `Box<T>` equivalent). Value types are stack-allocated (or inline-allocated in containing objects) and reclaimed deterministically at scope exit. This aligns closely with Rust's model and avoids GC overhead entirely for owned data.

Limitations: CLR structs cannot participate in inheritance, have fixed sizes, and are copied on assignment (though Cobalt's move semantics would prevent unintentional copies). Large structs are expensive to copy and may need to be boxed for certain CLR interop scenarios.

### Fighting vs. Embracing the GC

The design space has two poles:

**Fighting the GC (Rust-maximal approach):** Treat the GC as an implementation detail to be avoided. Use value types, arenas, and deterministic destruction for all owned data. Use the GC only for interop with existing .NET libraries. This maximizes control and predictability but fights against the runtime's design — much of the .NET ecosystem assumes GC-managed objects, and patterns like `IDisposable` were designed as exceptions rather than the default.

**Embracing the GC (C#-maximal approach):** Accept that memory is GC-managed and layer ownership as a compile-time correctness tool. Ownership prevents use-after-free and data races at compile time; the GC handles actual memory reclamation. Deterministic `Drop`/`Dispose` runs for resource cleanup, but memory lifetime is decoupled from ownership lifetime. This is simpler to implement and interoperates seamlessly with .NET but gives up the performance benefits of true deterministic memory management.

**The pragmatic middle ground:** Use ownership to inform but not override the GC. The compiler uses ownership information to:

- Prefer stack allocation for non-escaping owned values.
- Insert `Dispose()` calls at ownership boundaries for resource cleanup.
- Null out references at ownership boundaries to reduce the live-set.
- Suppress finalization for properly disposed objects.
- Avoid write barriers where ownership proves no cross-generational reference exists.

The GC remains the memory reclamation mechanism, but ownership analysis makes it more efficient by reducing allocation rates, shrinking the live set, and minimizing finalization overhead.

This middle ground accepts a key asymmetry: in Rust, memory lifetime and value lifetime are identical (the value is destroyed and its memory is freed at the same point). On .NET, value lifetime (when `Drop`/`Dispose` runs) and memory lifetime (when the GC reclaims the allocation) are decoupled. Cobalt would guarantee deterministic value destruction (resource cleanup, invariant teardown) while delegating memory reclamation to the GC. This is a weaker guarantee than Rust provides, but it is the strongest guarantee achievable on the CLR without reimplementing the runtime.

### Finalization Strategy

Cobalt should strongly consider making finalizers unnecessary for Cobalt-authored types. If the compiler guarantees that `Drop`/`Dispose` runs at ownership boundaries, there is no need for a finalizer safety net. This avoids the two-pass collection penalty, the finalization thread, and the resurrection hazard. For interop with C# types that have finalizers, Cobalt would treat them as opaque — their finalization is handled by the .NET runtime as usual.

### Pinning Strategy

Cobalt needs a pinning story for two scenarios:

1. **Interop pinning** (preventing GC relocation): Use POH allocation for long-lived buffers, `fixed` blocks for short-lived pins. The compiler could insert appropriate pinning automatically when ownership analysis detects that a value's address is shared with native code.

2. **Structural pinning** (preventing logical moves of self-referential types): If Cobalt supports Rust-style async (which generates self-referential state machines), it needs a `Pin`-like mechanism. On .NET, this is somewhat simpler than in Rust: if the async state machine is heap-allocated (as `Task<T>` is), it will not be moved by user code (C#/.NET has no move semantics), and GC relocation is handled by updating all managed references. Self-referential managed objects are safe as long as all internal pointers are managed references (which the GC updates on compaction). If internal pointers are raw (unmanaged) pointers, the object must be pinned against GC relocation. The need for a Rust-style `Pin` type depends on whether Cobalt permits raw internal pointers in its type system.

---

*Sources consulted:*

- [Fundamentals of garbage collection - .NET (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals)
- [.NET runtime GC design documentation (dotnet/runtime)](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/garbage-collection.md)
- [Workstation vs. server garbage collection (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc)
- [Background garbage collection (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/background-gc)
- [Latency Modes (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency)
- [Large object heap (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap)
- [Internals of the POH (.NET Blog)](https://devblogs.microsoft.com/dotnet/internals-of-the-poh/)
- [Pinned Object Heap design (dotnet/runtime)](https://github.com/dotnet/runtime/blob/main/docs/design/features/PinnedHeap.md)
- [Put a DPAD on that GC! (.NET Blog)](https://devblogs.microsoft.com/dotnet/put-a-dpad-on-that-gc/)
- [GC Regions Support (dotnet/runtime)](https://github.com/dotnet/runtime/issues/43844)
- [Finalization implementation details (.NET Blog)](https://devblogs.microsoft.com/dotnet/finalization-implementation-details/)
- [Implement a DisposeAsync method (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync)
- [Destructors - The Rust Reference](https://doc.rust-lang.org/reference/destructors.html)
- [Drop trait (Rust std docs)](https://doc.rust-lang.org/std/ops/trait.Drop.html)
- [ManuallyDrop (Rust std docs)](https://doc.rust-lang.org/std/mem/struct.ManuallyDrop.html)
- [RFC 1857: Stabilize drop order](https://github.com/rust-lang/rfcs/blob/master/text/1857-stabilize-drop-order.md)
- [RFC 1066: Safe mem::forget](https://rust-lang.github.io/rfcs/1066-safe-mem-forget.html)
- [std::pin module documentation](https://doc.rust-lang.org/std/pin/index.html)
- [RFC 2349: Pin](https://rust-lang.github.io/rfcs/2349-pin.html)
- [std::cell module documentation](https://doc.rust-lang.org/std/cell/)
- [Drop order in Rust: It's tricky](https://vojtechkral.github.io/blag/rust-drop-order/)
