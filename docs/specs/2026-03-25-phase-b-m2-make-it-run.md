# Phase B Milestone 2: Make It Run — Design Spec

**Date:** 2026-03-25
**Status:** Approved
**Goal:** Make the Cobalt compiler produce .NET assemblies that actually execute, with ownership metadata preserved in the output. Demonstrate Cobalt's unique value with a showcase sample.

**Prerequisite:** Phase B Milestone 1 (complete) — lexer, parser, type checker, borrow checker, and IL emitter exist with 365 tests passing.

---

## Scope

### In scope (Milestone 2)

Three workstreams:

1. **WS1: IL Emitter Completeness** — fix the six critical gaps that block execution
2. **WS2: Ownership Attribute Emission** — attach Phase A attributes to output assembly metadata
3. **WS3: Showcase Sample + Validation** — new single-file sample exercising all Cobalt-specific features, plus integration tests

### Explicitly deferred (Milestone 3: Production MVP)

- Lifetime validation in borrow checker
- Generic type instantiation (`GenericInstanceType`)
- Cast expression parsing and emission
- Pattern match exhaustiveness checking
- Cross-assembly borrow checking (consuming Cobalt from Cobalt)
- Embedded compiler-synthesized attributes (replace Phase A references)
- Three-file pipeline compilation (`main.co` + `processor.co` + `transforms.co`)
- Async/await support

---

## WS1: IL Emitter Completeness

Six critical gaps block execution. Each is described with the IL strategy.

### 1. Match Statement Emission

**Problem:** `match` statements emit Nop placeholders. Union variant dispatch does not work.

**Strategy:** Union variants compile to nested sealed classes (e.g., `Shape/CircleCase`). Match dispatch uses type-testing:

```
load subject
isinst Shape/CircleCase → if true: cast, extract fields into locals, emit arm body
isinst Shape/RectangleCase → if true: cast, extract fields into locals, emit arm body
... (for each arm)
br end_label
```

For `var` patterns (catch-all): no type test, bind subject to a local directly.

Pattern variable binding (`var r` in `Circle(var r)`): after the cast, load each constructor field from the variant instance and store into fresh locals named by the pattern variables.

### 2. Switch Expression Emission

**Problem:** Switch expressions return Ldnull.

**Strategy:** Same type-test dispatch as match statements, but each arm pushes a value onto the stack and branches to a shared end label. The end label is where the expression's value is consumed.

### 3. Foreach Body Emission

**Problem:** Foreach bodies are discarded. The iterable is evaluated and popped.

**Strategy:** Emit the standard .NET iteration pattern:

```il
eval iterable
callvirt GetEnumerator() → store enumerator_local
br condition_label
loop_body:
    callvirt get_Current() → store loop_variable
    emit body
condition_label:
    callvirt MoveNext() → brtrue loop_body
```

The `ref`/`ref mut` modifiers on the loop variable are compile-time only (borrow checker enforces them at analysis time) — no special IL is needed at runtime.

### 4. Break/Continue

**Problem:** Break and continue emit Nop. Loops execute incorrectly.

**Strategy:** Add a label stack to `BodyContext`:

```csharp
public Stack<(Instruction BreakTarget, Instruction ContinueTarget)> LoopLabels { get; } = new();
```

Each loop (while, for, foreach) pushes labels before emitting its body and pops after. `break` emits `br breakTarget`, `continue` emits `br continueTarget`.

### 5. Using Var Disposal

**Problem:** `using var` declarations create locals but do not emit try/finally or Dispose calls.

**Strategy:** When a `UsingVarDeclaration` is encountered in a block, wrap all subsequent statements in the same block inside a try/finally:

```il
eval initializer → store using_local
.try {
    ... remaining statements in block ...
    leave end_label
}
finally {
    ldloc using_local
    callvirt System.IDisposable::Dispose()
    endfinally
}
end_label:
```

Multiple `using var` declarations in the same block nest: each wraps the remainder (including subsequent using vars).

### 6. Impl Block Emission

**Problem:** Methods declared inside `impl Trait for Type` blocks are registered in the type checker's symbol table but never emitted to IL.

**Strategy:**

- **Pass 1 (DeclareTypes):** When encountering `ImplBlock`, look up the target type's `TypeDefinition` and the trait's `TypeDefinition` (which is an interface). Add the interface to the target type's `Interfaces` collection if not already present.
- **Pass 2 (EmitMemberSignatures):** For each method in the impl block, add a `MethodDefinition` to the target type. Mark it as a virtual/final method implementing the interface method.
- **Pass 3 (EmitBodies):** Queue the method bodies for emission, same as class methods.

---

## WS2: Ownership Attribute Emission

### Goal

When Cobalt compiles code with ownership modifiers (`own`, `ref`, `ref mut`), the output assembly preserves this information as custom attributes from `Cobalt.Annotations`. This enables:
- Cobalt-to-Cobalt interop (future: compiler reads attributes from referenced assemblies)
- Cobalt-to-C# interop (Phase A analyzer reads attributes for advisory warnings)

### Approach

Reference the existing `Cobalt.Annotations` assembly and attach its attributes.

**At ILEmitter construction time:**
- Import `OwnedAttribute`, `BorrowedAttribute`, `MutBorrowedAttribute` types via `ModuleDefinition.ImportReference()`
- Store the imported method references for each attribute's constructor

**During Pass 2 (EmitMemberSignatures):**
- For each parameter with an ownership modifier, create a `CustomAttribute` and add it to the parameter's `CustomAttributes` collection
- For return types with `own`, add `[Owned]` to the method's `MethodReturnType.CustomAttributes`
- For fields with `own`, add `[Owned]` to the field's `CustomAttributes`

### Attribute mapping

| Cobalt syntax | Attribute | Target |
|---|---|---|
| `own` on parameter | `[Owned]` | `ParameterDefinition.CustomAttributes` |
| `ref` on parameter | `[Borrowed]` | `ParameterDefinition.CustomAttributes` |
| `ref mut` on parameter | `[MutBorrowed]` | `ParameterDefinition.CustomAttributes` |
| `own` on return type | `[Owned]` | `MethodDefinition.MethodReturnType.CustomAttributes` |
| `own` on field | `[Owned]` | `FieldDefinition.CustomAttributes` |

### Assembly dependency

The output assembly will reference `Cobalt.Annotations.dll`. The CLI copies it alongside the output. In Milestone 3, embedded compiler-synthesized attributes will remove this external dependency.

### Project reference

Add `Cobalt.Annotations` as a project reference to `Cobalt.Compiler.csproj` (it's already in the solution).

---

## WS3: Showcase Sample + Validation

### New sample: `samples/cobalt-syntax/showcase.co`

A single-file program exercising all Cobalt-specific features in a realistic mini-scenario — a resource pool that owns resources, lends them via borrows, and uses unions for results.

**Features demonstrated:**
- Union types with multiple variants
- Trait definition and inline implementation
- Owned fields (`own` on class fields)
- Ownership transfer at call sites (`own` keyword)
- Borrowed parameters (`ref`, `ref mut`)
- `match` statement on union variants with pattern variable binding
- `using var` for automatic disposal
- Interpolated strings with value-type insertions
- The borrow checker preventing misuse (ownership-aware diagnostics)

### `hello.co`

Kept as-is — simple intro showing classes, traits, unions.

### Integration tests: `src/Cobalt.Compiler.Tests/Integration/`

Currently an empty directory. Add:

1. **Compile and inspect `hello.co`** — assert assembly exists, verify expected types/methods via Cecil
2. **Compile and inspect `showcase.co`** — assert assembly exists, verify union variant types, verify ownership attributes present on parameters/fields
3. **Negative test** — compile source with use-after-move, assert borrow checker error is reported

---

## Roadmap Structure

### Main roadmap: `docs/implementation-roadmap.md`

Updated to reflect:
- Phase B M1: fully complete with known limitations
- Phase B M2: "Make it run" — this spec's scope
- Phase B M3: "Production MVP" — deferred items as a backlog

### Detailed plans: `docs/plans/`

- Archive `2026-03-24-phase-b-m1-compiler.md` by moving to `docs/plans/archive/`
- New implementation plan for M2 generated via writing-plans skill

### Milestone 3 backlog

Listed as bullet points in the main roadmap. No detailed plan until M2 is complete.

---

## Success Criteria

Milestone 2 is complete when:

1. `cobaltc showcase.co -o showcase.dll` produces an assembly that passes PEVerify (or equivalent)
2. `cobaltc hello.co -o hello.dll` continues to work
3. Ownership attributes (`[Owned]`, `[Borrowed]`, `[MutBorrowed]`) are present in the output assembly metadata
4. All existing tests (365+) continue to pass
5. New integration tests verify end-to-end compilation
6. The showcase sample exercises: own transfers, ref/ref mut borrows, match on unions, using var disposal, trait implementation
