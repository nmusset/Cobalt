# Cobalt Syntax Design Spec

**Date:** 2026-03-23
**Status:** Draft
**Scope:** MVP syntax for the Cobalt language (Phase B, Milestone 1)
**Sample:** `samples/cobalt-syntax/`

---

## Goal

Define a concrete syntax for Cobalt that brings Rust's ownership and borrow-checking guarantees to a C#-familiar language targeting .NET IL. The syntax should feel natural to C# developers while making ownership semantics explicit and compiler-enforced.

## Design Principles

1. **C# first** — Syntax defaults to C# conventions. Deviate only when ownership semantics require it.
2. **Ownership is visible** — Ownership transfer and borrowing are explicit in function signatures and call sites.
3. **No null** — Cobalt-defined types cannot be null. Absence is expressed via `Option<T>`. .NET types crossing the boundary may be null, wrapped automatically into `Option<T>`.
4. **.NET interop is invisible** — BCL types are implicitly trusted. No `unsafe` or `unchecked` blocks needed for standard .NET calls.
5. **Future external annotations** — BCL types can later be annotated via external manifests (JSON/XML) parsed by the compiler, enabling gradual ownership checking of .NET libraries.

## File Extension

`.co` — short for Cobalt, parallels `.cs` (C Sharp), `.fs` (F Sharp).

## Reserved Keywords

The following keywords are reserved for future use but not part of the MVP:

- `fn` — reserved for a potential future function declaration syntax distinct from methods
- `async` / `await` — deferred to Milestone 2
- `send` / `sync` — compile-time thread-safety markers (Milestone 2)

---

## Syntax Reference

### Modules and Imports

```csharp
namespace Cobalt.Samples;

use System;
use System.IO;
use System.Collections.Generic;
```

- `namespace` — identical to C#.
- `use` — replaces C#'s `using` for imports (avoids collision with `using var` disposal pattern).

### Access Modifiers

Cobalt follows C# conventions for access modifiers:

- `public`, `private`, `protected`, `internal` — same semantics as C#.
- Default access: `internal` for top-level types, `private` for members — same as C#.
- Trait members are implicitly `public` (like C# interface members). Explicit `public` is allowed but optional in trait definitions.

### Ownership Modifiers

#### On parameters

| Modifier | Meaning | Rust equivalent |
|----------|---------|-----------------|
| `own` | Ownership transfer — caller gives up access | move |
| `ref` | Shared borrow — read-only, caller retains ownership | `&T` |
| `ref mut` | Exclusive borrow — mutable, no other references allowed | `&mut T` |
| *(none)* | Value types: copy. Reference types: implicit shared borrow | default |

```csharp
void ConsumeStream(own Stream stream) { ... }
int CountLines(ref List<string> lines) { ... }
void AppendLine(ref mut List<string> lines, string text) { ... }
```

#### On call sites

Ownership transfer and mutable borrows are explicit at the call site:

```csharp
var stream = File.OpenRead("input.txt");
ConsumeStream(own stream);         // explicit ownership transfer
// stream is no longer usable

AppendLine(ref mut lines, "text"); // explicit mutable borrow
CountLines(ref lines);             // explicit shared borrow (optional — compiler can infer)
```

Both `own` and `ref mut` are required at call sites to make the intent visible. `ref` for shared borrows is optional — the compiler infers it when the parameter is declared `ref`.

`own` can modify any expression at a call site, not just named variables:

```csharp
processor.AddTransform(own new UpperCaseTransform());  // own applied to a new expression
```

This is equivalent to creating a temporary and immediately moving it. The compiler treats the expression result as owned by the callee.

#### On fields

```csharp
class ResourceHolder : IDisposable
{
    own Stream _input;     // this type owns the stream
    own Stream _output;

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
    }
}
```

#### On return values

```csharp
own FileProcessor Create(own Stream input, own Stream output) { ... }
```

`own` on the return type signals that the caller receives ownership and is responsible for the value's lifetime.

#### In object initializers

`own` can be used in object initializer expressions to transfer ownership from a local into a field:

```csharp
return new FileProcessor
{
    _input = own input,    // transfers ownership from local to field
    _output = own output,
};
```

#### In generic type arguments

`own` inside a generic type argument indicates element ownership:

```csharp
own List<own ITransform> _transforms = new();  // list owns its elements
```

Only `own` is valid in generic type arguments. `ref` and `ref mut` are not allowed in type argument positions — borrowing is a call-site concept, not a type-level one.

#### Using declarations

```csharp
using var output = File.Create("out.txt");  // owned + auto-dispose at scope end
```

`using var` implicitly creates an owned binding with guaranteed disposal at end of scope. It can receive `own` return values directly:

```csharp
using var processor = FileProcessor.Create(own input, own output);  // owns the result, auto-disposes
```

`using own var` is redundant — `using var` already implies ownership. The compiler accepts it but it is not idiomatic.

#### Local bindings

`var` creates an owned binding for reference types and a copy for value types:

```csharp
var stream = File.OpenRead("input.txt");  // stream is owned — can be moved or disposed
var count = 42;                            // count is a copy — value type
```

Owned locals can be moved with `own`, disposed with `using var`, or the compiler reports CB0001 if they are not disposed or transferred by end of scope (for types implementing `IDisposable`).

### Object Construction

Object construction uses `new`, identical to C#:

```csharp
var list = new List<string>();
var transform = new UpperCaseTransform();
```

`new` is always required. There is no shorthand constructor syntax without `new`.

### Free-Standing Functions

Functions can be declared at module scope (outside any class). They use the same syntax as methods:

```csharp
string Summarize(ref ProcessResult result)
{
    return result switch { ... };
}
```

Free-standing functions are compiled to static methods on a synthetic class in IL. They are scoped to the namespace and accessible from other files in the same namespace.

### Traits

```csharp
trait ITransform
{
    string Name { get; }
    void Apply(ref mut List<string> lines);
}
```

- `trait` keyword — distinguishes Cobalt traits from .NET `interface`. Cobalt traits support external `impl` blocks and will eventually support associated types and marker traits.
- Trait members are implicitly `public` (like C# interface members).
- .NET `interface` remains available for pure .NET interop.

#### Inline implementation (C# style)

For types you define:

```csharp
class UpperCaseTransform : ITransform
{
    public string Name => "uppercase";

    public void Apply(ref mut List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = lines[i].ToUpper();
        }
    }
}
```

#### External impl blocks (Rust style)

For implementing traits on types you don't own:

```csharp
impl ITransform for SomeExternalType
{
    public string Name => "external";
    public void Apply(ref mut List<string> lines) { ... }
}
```

### Union Types (Algebraic Data Types)

```csharp
union ProcessResult
{
    Success(int LinesProcessed),
    Error(string Message),
    Skipped(string Reason),
}
```

- `union` keyword — aligns with the C# 15 proposal direction.
- Variants have named fields.
- Compiled to sealed class hierarchies in .NET IL (the only viable encoding for reference-containing variants — see IL constraints research).

#### Constructing union variants

Variants are constructed by name, like function calls. Variant names are scoped to the union type and are imported into scope when the union type is in scope:

```csharp
return Success(42);                    // construct Success variant
return Error("something went wrong");  // construct Error variant
var x = Some("hello");                 // Option<T> variant
var y = None;                          // Option<T> variant (no value)
```

If variant names conflict with other identifiers, qualify with the union type name:

```csharp
return ProcessResult.Success(42);
```

#### Built-in unions

`Option<T>` and `Result<T, E>` are built-in:

```csharp
Option<string> FindLine(ref List<string> lines, string pattern)
{
    foreach (var line in lines)
    {
        if (line.Contains(pattern))
            return Some(line);
    }
    return None;
}
```

### Pattern Matching

Two forms, both with compiler-enforced exhaustiveness:

#### Switch expression (returns a value — C# style)

```csharp
string Summarize(ref ProcessResult result)
{
    return result switch
    {
        Success(var n) => $"Processed {n} lines",
        Error(var msg) => $"Failed: {msg}",
        Skipped(var reason) => $"Skipped: {reason}",
    };
}
```

#### Match statement (side effects — Rust-inspired)

`match` is syntactically an expression-statement: the match expression is followed by a semicolon, like a C# expression-statement (e.g., `method();`).

```csharp
match (result)
{
    Success(var n) => Console.WriteLine($"Done: {n} lines"),
    Error(var msg) => Console.WriteLine($"Error: {msg}"),
    Skipped(var reason) => Console.WriteLine($"Skipped: {reason}"),
};
```

Match arms are single expressions. For multi-statement arms, use a block:

```csharp
match (result)
{
    Success(var n) =>
    {
        Log($"Processed {n} lines");
        Notify(n);
    },
    Error(var msg) => HandleError(msg),
    Skipped(var reason) => { },
};
```

#### Inline pattern matching with `is`

```csharp
while (reader.ReadLine() is Some(var line))
{
    lines.Add(line);
}
```

### Foreach with Ownership Modifiers

```csharp
foreach (ref item in collection)       // shared borrow iteration
foreach (ref mut item in collection)   // mutable borrow iteration
foreach (own item in collection)       // owned iteration (consumes collection)
foreach (var item in collection)       // copies value types, borrows reference types
```

The ownership modifier in `foreach` is a Cobalt-level annotation on the loop variable — it is not passed to the underlying .NET enumerator. The compiler uses it to enforce borrowing rules: `ref` prevents mutation of the element, `ref mut` requires exclusive access, and `own` moves each element out of the collection (making the collection unusable afterward).

### Entry Point

Top-level statements, identical to C# 9+:

```csharp
// main.co — no Main method needed
namespace Cobalt.Samples;

use System;

var input = File.OpenRead("input.txt");
Console.WriteLine("Processing...");
```

Compiled to a synthetic `Program` class with a `Main` method in IL.

### .NET Interop

.NET types are implicitly trusted — no special syntax required. The compiler relaxes ownership checking for .NET types:

```csharp
var reader = new StreamReader(_input);       // .NET type, just works
var lines = new List<string>();              // BCL generic
Console.WriteLine(Summarize(ref result));    // static method call
```

**Ownership modifiers at call sites for .NET methods:**

- **`own` is allowed.** When passing `own` to a .NET method that has no ownership annotation, the compiler accepts it and marks the local as moved. The compiler does not verify that the .NET method actually takes ownership — this is an implicit trust boundary.
  ```csharp
  _transforms.Add(own transform);  // List.Add is unannotated, but the local is marked moved
  ```
- **`ref` and `ref mut` are not used.** Borrow annotations are only meaningful for Cobalt-declared parameters. When calling .NET methods, pass arguments directly without `ref` or `ref mut`.

Future external annotation manifests will enable gradual ownership checking for BCL types.

### Null Handling

- Cobalt-defined types cannot be null.
- .NET types crossing the interop boundary that may be null are automatically wrapped into `Option<T>`.
- `Option<T>` is the canonical way to express absence.

---

## Sample Program

The complete sample is in `samples/cobalt-syntax/`. It implements a file processor pipeline exercising ownership, borrowing, traits, unions, pattern matching, and .NET interop.

See:
- [`main.co`](../../samples/cobalt-syntax/main.co) — entry point with top-level statements
- [`processor.co`](../../samples/cobalt-syntax/processor.co) — `FileProcessor` with owned resources
- [`transforms.co`](../../samples/cobalt-syntax/transforms.co) — `ITransform` trait, implementations, `ProcessResult` union

---

## IL Compilation Notes

These are not part of the syntax spec but inform compiler implementation:

| Cobalt feature | .NET IL encoding |
|---------------|-----------------|
| `own` parameters | Regular parameters + `[Owned]` custom attribute |
| `ref` / `ref mut` | Regular parameters + `[Borrowed]` / `[MutBorrowed]` attribute |
| `trait` | .NET `interface` |
| External `impl` block | Interface implementation on the target type (requires compiler support for cross-type impl) |
| `union` | Sealed abstract class + nested sealed subclasses per variant |
| `Option<T>` | Sealed class hierarchy (or struct for value types) |
| `match` / `switch` | Standard C# pattern matching IL |
| Top-level statements | Synthetic `Program` class with `Main` method |
| Free-standing functions | Static methods on a synthetic class |
| `use` | Resolved at compile time, no IL representation |
| `own` in generic type args | Custom attribute on the type argument (metadata only) |
