# Phase 1: Knowledge Base

Comparative research across C# and Rust, covering the foundational topics needed before designing Cobalt.

| # | Topic | Description |
|---|-------|-------------|
| 01 | [Type Systems](01-type-systems.md) | Value/reference types, generics, traits vs interfaces, enums/ADTs, nullability |
| 02 | [Ownership & Borrowing](02-ownership-and-borrowing.md) | Move semantics, borrow checker rules, lifetimes |
| 03 | [Memory Management](03-memory-management.md) | GC vs RAII/Drop, deterministic destruction, pinning, interior mutability |
| 04 | [Concurrency](04-concurrency.md) | Async/await models, Send/Sync, data-race prevention, concurrency patterns |
| 05 | [.NET IL Constraints](05-il-constraints.md) | CIL type system, metadata encoding, ref structs, value type layout |
| 06 | [Interop](06-interop.md) | P/Invoke, Rust FFI, .NET-Rust binding generators, marshalling |
| 07 | [Prior Art](07-prior-art.md) | Midori, Vale, Austral, Mojo, rustc_codegen_clr, Roslyn analyzers |
| 08 | [Tooling Landscape](08-tooling-landscape.md) | Roslyn pipeline, compiler frontends, IL emission, LSP/IDE integration |
