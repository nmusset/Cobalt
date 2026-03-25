# Phase B Milestone 2: Make It Run — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Cobalt compiler produce .NET assemblies that actually execute, with ownership metadata preserved in output assembly custom attributes.

**Architecture:** The work extends the existing three-pass IL emitter (declare types → emit signatures → emit bodies) with six missing emission paths, adds BCL method resolution infrastructure, ownership attribute attachment via Mono.Cecil, and a showcase sample with integration tests. No new projects are created — all changes are in existing `Cobalt.Compiler`, `Cobalt.Compiler.Cli`, and `Cobalt.Compiler.Tests`.

**Tech Stack:** .NET 10, C#, Mono.Cecil 0.11.6, Cobalt.Annotations (Phase A), xUnit

**Spec:** `docs/specs/2026-03-25-phase-b-m2-make-it-run.md`

---

## File Structure

### Modified files

```
src/Cobalt.Compiler/Emit/ILEmitter.cs          — All WS1 emitter fixes + WS2 attribute emission
src/Cobalt.Compiler/Cobalt.Compiler.csproj      — Add Cobalt.Annotations project reference
src/Cobalt.Compiler/Driver/Compilation.cs       — Entry point detection, ModuleKind selection
src/Cobalt.Compiler.Cli/Program.cs              — Copy Cobalt.Annotations.dll alongside output
docs/implementation-roadmap.md                  — Update with M2/M3 sections
```

### New files

```
src/Cobalt.Compiler.Tests/Emit/ILEmitterMatchTests.cs    — Match/switch emission tests
src/Cobalt.Compiler.Tests/Emit/ILEmitterLoopTests.cs     — Foreach, break/continue tests
src/Cobalt.Compiler.Tests/Emit/ILEmitterUsingTests.cs    — Using-var disposal tests
src/Cobalt.Compiler.Tests/Emit/ILEmitterImplTests.cs     — Impl block emission tests
src/Cobalt.Compiler.Tests/Emit/ILEmitterAttrTests.cs     — Ownership attribute emission tests
src/Cobalt.Compiler.Tests/Integration/CompilationTests.cs — End-to-end compile + inspect tests
samples/cobalt-syntax/showcase.co                         — Cobalt-specific feature showcase
```

---

## Task 1: BCL Method Resolution Infrastructure

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs`

Adds helper methods to resolve well-known BCL methods (IDisposable.Dispose, IEnumerator.MoveNext, etc.) that the emitter needs for foreach loops and using-var disposal.

- [ ] **Step 1: Add BCL method resolution helpers to ILEmitter**

Add these private methods after the existing `DefaultObjectCtor()` method (~line 1275):

```csharp
private MethodReference ImportDispose()
{
    var disposableType = new TypeReference("System", "IDisposable", _module, _module.TypeSystem.CoreLibrary);
    var method = new MethodReference("Dispose", _module.TypeSystem.Void, disposableType) { HasThis = true };
    return method;
}

private MethodReference ImportGetEnumerator()
{
    var enumerableType = new TypeReference("System.Collections", "IEnumerable", _module, _module.TypeSystem.CoreLibrary);
    var method = new MethodReference("GetEnumerator",
        new TypeReference("System.Collections", "IEnumerator", _module, _module.TypeSystem.CoreLibrary),
        enumerableType) { HasThis = true };
    return method;
}

private MethodReference ImportMoveNext()
{
    var enumeratorType = new TypeReference("System.Collections", "IEnumerator", _module, _module.TypeSystem.CoreLibrary);
    var method = new MethodReference("MoveNext", _module.TypeSystem.Boolean, enumeratorType) { HasThis = true };
    return method;
}

private MethodReference ImportGetCurrent()
{
    var enumeratorType = new TypeReference("System.Collections", "IEnumerator", _module, _module.TypeSystem.CoreLibrary);
    var method = new MethodReference("get_Current", _module.TypeSystem.Object, enumeratorType) { HasThis = true };
    return method;
}

private MethodReference ImportInvalidOperationExceptionCtor()
{
    var exType = new TypeReference("System", "InvalidOperationException", _module, _module.TypeSystem.CoreLibrary);
    var ctor = new MethodReference(".ctor", _module.TypeSystem.Void, exType) { HasThis = true };
    ctor.Parameters.Add(new ParameterDefinition(_module.TypeSystem.String));
    return ctor;
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build src/Cobalt.Compiler/Cobalt.Compiler.csproj --no-restore
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs
git commit -m "Add BCL method resolution helpers (Dispose, GetEnumerator, MoveNext)"
```

---

## Task 2: Break/Continue Loop Label Stack

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (BodyContext class ~line 1304, EmitStatement ~line 523, EmitWhile, EmitFor)

- [ ] **Step 1: Add LoopLabels to BodyContext**

Add to the `BodyContext` class (after the `Module` property at ~line 1311):

```csharp
public Stack<(Instruction BreakTarget, Instruction ContinueTarget)> LoopLabels { get; } = new();
```

- [ ] **Step 2: Add BreakStatement and ContinueStatement cases to EmitStatement**

In the `EmitStatement` switch (~line 457), replace the `default` case that emits Nop. Add before the existing `default`:

```csharp
case BreakStatement:
    if (ctx.LoopLabels.Count > 0)
        il.Emit(OpCodes.Br, ctx.LoopLabels.Peek().BreakTarget);
    else
        il.Emit(OpCodes.Nop);
    return;
case ContinueStatement:
    if (ctx.LoopLabels.Count > 0)
        il.Emit(OpCodes.Br, ctx.LoopLabels.Peek().ContinueTarget);
    else
        il.Emit(OpCodes.Nop);
    return;
```

- [ ] **Step 3: Update EmitWhile to push/pop loop labels**

In `EmitWhile` method, wrap the body emission with label push/pop:

```csharp
// Before emitting body:
ctx.LoopLabels.Push((endLabel, conditionLabel));
// Emit body...
// After body:
ctx.LoopLabels.Pop();
```

Where `endLabel` is the instruction after the loop and `conditionLabel` is the loop condition check.

- [ ] **Step 4: Update EmitFor to push/pop loop labels**

Add an `incrementLabel` instruction before the increment section and push it as the continue target. The restructured `EmitFor` should be:

```csharp
private void EmitFor(ForStatement forStmt, BodyContext ctx)
{
    var il = ctx.IL;
    if (forStmt.Initializer != null)
        EmitStatement(forStmt.Initializer, ctx);

    var condLabel = il.Create(OpCodes.Nop);
    var endLabel = il.Create(OpCodes.Nop);
    var incrementLabel = il.Create(OpCodes.Nop); // continue jumps here

    il.Append(condLabel);
    if (forStmt.Condition != null)
    {
        EmitExpression(forStmt.Condition, ctx);
        il.Emit(OpCodes.Brfalse, endLabel);
    }

    ctx.LoopLabels.Push((endLabel, incrementLabel));
    EmitStatement(forStmt.Body, ctx);
    ctx.LoopLabels.Pop();

    il.Append(incrementLabel); // continue target: run increment then re-check condition
    if (forStmt.Increment != null)
    {
        var t = EmitExpression(forStmt.Increment, ctx);
        if (t != null && t != _module.TypeSystem.Void) il.Emit(OpCodes.Pop);
    }

    il.Emit(OpCodes.Br, condLabel);
    il.Append(endLabel);
}
```

- [ ] **Step 5: Write tests**

Create `src/Cobalt.Compiler.Tests/Emit/ILEmitterLoopTests.cs`:

```csharp
[Fact]
public void Emit_BreakInWhile_EmitsBranch()
{
    var asm = Emit("""
        public class BreakTest
        {
            public void Run(bool flag)
            {
                while (flag)
                {
                    break;
                }
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "BreakTest"), "Run");
    Assert.True(HasOpCode(method, OpCodes.Br));
}

[Fact]
public void Emit_ContinueInFor_EmitsBranch()
{
    var asm = Emit("""
        public class ContinueTest
        {
            public void Run()
            {
                for (var i = 0; i < 10; i++)
                {
                    continue;
                }
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "ContinueTest"), "Run");
    // Should have at least 2 Br instructions (continue + loop back)
    var brCount = Instructions(method).Count(i => i.OpCode == OpCodes.Br);
    Assert.True(brCount >= 2);
}
```

Use the same helpers (`Emit`, `GetType`, `GetMethod`, `HasOpCode`, `Instructions`) from `ILEmitterTests.cs`. The new test file needs the same `using` statements and helper method copies or a shared base class.

- [ ] **Step 6: Run tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~ILEmitterLoopTests"
```

Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler.Tests/Emit/ILEmitterLoopTests.cs
git commit -m "Add break/continue emission with loop label stack"
```

---

## Task 3: Foreach Body Emission

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (EmitForEach method ~line 594)
- Modify: `src/Cobalt.Compiler.Tests/Emit/ILEmitterLoopTests.cs`

- [ ] **Step 1: Rewrite EmitForEach**

Replace the existing `EmitForEach` method (which discards the body) with the full IEnumerator pattern:

```csharp
private void EmitForEach(ForEachStatement forEach, BodyContext ctx)
{
    var il = ctx.IL;

    // Emit iterable and call GetEnumerator()
    EmitExpression(forEach.Iterable, ctx);
    il.Emit(OpCodes.Callvirt, ImportGetEnumerator());

    // Store enumerator in a local
    var enumeratorType = new TypeReference("System.Collections", "IEnumerator", _module, _module.TypeSystem.CoreLibrary);
    var enumeratorLocal = new VariableDefinition(enumeratorType);
    ctx.Method.Body.Variables.Add(enumeratorLocal);
    il.Emit(OpCodes.Stloc, enumeratorLocal);

    // Labels
    var conditionLabel = il.Create(OpCodes.Nop);
    var endLabel = il.Create(OpCodes.Nop);

    // Jump to condition first
    il.Emit(OpCodes.Br, conditionLabel);

    // Loop body start
    var bodyStart = il.Create(OpCodes.Nop);
    il.Append(bodyStart);

    // Load current element
    il.Emit(OpCodes.Ldloc, enumeratorLocal);
    il.Emit(OpCodes.Callvirt, ImportGetCurrent());

    // Store in loop variable
    var elementLocal = new VariableDefinition(_module.TypeSystem.Object);
    ctx.Method.Body.Variables.Add(elementLocal);
    ctx.Locals[forEach.VariableName] = elementLocal;
    il.Emit(OpCodes.Stloc, elementLocal);

    // Push loop labels for break/continue
    ctx.LoopLabels.Push((endLabel, conditionLabel));
    EmitStatement(forEach.Body, ctx);
    ctx.LoopLabels.Pop();

    // Condition: call MoveNext()
    il.Append(conditionLabel);
    il.Emit(OpCodes.Ldloc, enumeratorLocal);
    il.Emit(OpCodes.Callvirt, ImportMoveNext());
    il.Emit(OpCodes.Brtrue, bodyStart);

    il.Append(endLabel);
}
```

- [ ] **Step 2: Write test**

Add to `ILEmitterLoopTests.cs`:

```csharp
[Fact]
public void Emit_ForEach_EmitsMoveNextLoop()
{
    var asm = Emit("""
        public class ForEachTest
        {
            public void Run(List items)
            {
                foreach (var item in items)
                {
                    var x = item;
                }
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "ForEachTest"), "Run");
    Assert.True(HasOpCode(method, OpCodes.Callvirt));
    Assert.True(HasOpCode(method, OpCodes.Brtrue));
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~ILEmitterLoopTests"
```

Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler.Tests/Emit/ILEmitterLoopTests.cs
git commit -m "Implement foreach body emission with IEnumerator pattern"
```

---

## Task 4: Match Statement Emission

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (EmitStatement, new EmitMatch method)
- Create: `src/Cobalt.Compiler.Tests/Emit/ILEmitterMatchTests.cs`

- [ ] **Step 1: Add EmitMatch method to ILEmitter**

Add a new method that handles `MatchStatement` dispatch:

```csharp
private void EmitMatch(MatchStatement match, BodyContext ctx)
{
    var il = ctx.IL;
    var endLabel = il.Create(OpCodes.Nop);

    // Evaluate subject and store in temp
    var subjectType = EmitExpression(match.Subject, ctx);
    var tempLocal = new VariableDefinition(subjectType ?? _module.TypeSystem.Object);
    ctx.Method.Body.Variables.Add(tempLocal);
    il.Emit(OpCodes.Stloc, tempLocal);

    foreach (var arm in match.Arms)
    {
        switch (arm.Pattern)
        {
            case VariantPattern variant:
            {
                // Type-test: isinst TargetType
                var nextArm = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Ldloc, tempLocal);
                var variantKey = FindVariantType(variant.VariantName);
                if (variantKey != null)
                {
                    il.Emit(OpCodes.Isinst, variantKey);
                    il.Emit(OpCodes.Brfalse, nextArm);

                    // Cast and extract fields into locals
                    il.Emit(OpCodes.Ldloc, tempLocal);
                    il.Emit(OpCodes.Castclass, variantKey);

                    // Bind pattern variables from variant fields
                    if (variant.SubPatterns.Count > 0)
                    {
                        var castLocal = new VariableDefinition(variantKey);
                        ctx.Method.Body.Variables.Add(castLocal);
                        il.Emit(OpCodes.Stloc, castLocal);

                        for (int i = 0; i < variant.SubPatterns.Count && i < variantKey.Fields.Count; i++)
                        {
                            if (variant.SubPatterns[i] is VarPattern vp)
                            {
                                var fieldLocal = new VariableDefinition(variantKey.Fields[i].FieldType);
                                ctx.Method.Body.Variables.Add(fieldLocal);
                                ctx.Locals[vp.VariableName] = fieldLocal;
                                il.Emit(OpCodes.Ldloc, castLocal);
                                il.Emit(OpCodes.Ldfld, variantKey.Fields[i]);
                                il.Emit(OpCodes.Stloc, fieldLocal);
                            }
                        }
                    }
                    else
                    {
                        il.Emit(OpCodes.Pop); // discard cast result if no bindings
                    }
                }
                else
                {
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Br, nextArm);
                }

                // Emit arm body
                EmitArmBody(arm.Body, ctx);
                il.Emit(OpCodes.Br, endLabel);
                il.Append(nextArm);
                break;
            }
            case VarPattern varPat:
            {
                // Catch-all: bind subject to local
                var catchLocal = new VariableDefinition(subjectType ?? _module.TypeSystem.Object);
                ctx.Method.Body.Variables.Add(catchLocal);
                ctx.Locals[varPat.VariableName] = catchLocal;
                il.Emit(OpCodes.Ldloc, tempLocal);
                il.Emit(OpCodes.Stloc, catchLocal);
                EmitArmBody(arm.Body, ctx);
                il.Emit(OpCodes.Br, endLabel);
                break;
            }
            case DiscardPattern:
            {
                // Wildcard: emit body unconditionally
                EmitArmBody(arm.Body, ctx);
                il.Emit(OpCodes.Br, endLabel);
                break;
            }
            default:
            {
                il.Emit(OpCodes.Nop);
                break;
            }
        }
    }

    // Safety net: throw if no arm matched
    il.Emit(OpCodes.Ldstr, "No matching pattern");
    il.Emit(OpCodes.Newobj, ImportInvalidOperationExceptionCtor());
    il.Emit(OpCodes.Throw);

    il.Append(endLabel);
}

private void EmitArmBody(SyntaxNode body, BodyContext ctx)
{
    if (body is StatementNode stmt)
        EmitStatement(stmt, ctx);
    else if (body is ExpressionNode expr)
    {
        var t = EmitExpression(expr, ctx);
        if (t != null && t.FullName != "System.Void")
            ctx.IL.Emit(OpCodes.Pop);
    }
}

private TypeDefinition? FindVariantType(string variantName)
{
    // Search nested types in union type definitions
    foreach (var (key, typeDef) in _typeDefs)
    {
        if (key.EndsWith("." + variantName))
            return typeDef;
        var nested = typeDef.NestedTypes.FirstOrDefault(n => n.Name == variantName);
        if (nested != null) return nested;
    }
    return null;
}
```

- [ ] **Step 2: Wire EmitMatch into EmitStatement**

Add before the `default` case in `EmitStatement`:

```csharp
case MatchStatement matchStmt:
    EmitMatch(matchStmt, ctx);
    return;
```

- [ ] **Step 3: Write tests**

Create `src/Cobalt.Compiler.Tests/Emit/ILEmitterMatchTests.cs` with same helper infrastructure as `ILEmitterTests.cs`:

```csharp
[Fact]
public void Emit_MatchOnUnion_EmitsIsinst()
{
    var asm = Emit("""
        union Shape
        {
            Circle(int Radius),
            Rectangle(int Width, int Height),
        }
        public class Matcher
        {
            public int Area(Shape s)
            {
                match (s)
                {
                    Circle(var r) => return r,
                    Rectangle(var w, var h) => return w,
                };
                return 0;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "Matcher"), "Area");
    Assert.True(HasOpCode(method, OpCodes.Isinst));
    Assert.True(HasOpCode(method, OpCodes.Castclass));
    // Safety-net throw for unmatched patterns
    Assert.True(HasOpCode(method, OpCodes.Throw));
}

[Fact]
public void Emit_MatchVarCatchAll_BindsLocal()
{
    var asm = Emit("""
        union Result
        {
            Ok(int Value),
            Err(string Message),
        }
        public class Handler
        {
            public void Handle(Result r)
            {
                match (r)
                {
                    var x => return,
                };
            }
        }
        """);
    var method = GetMethod(GetType(asm, "Handler"), "Handle");
    // Var pattern should NOT emit Isinst (it's a catch-all)
    Assert.False(HasOpCode(method, OpCodes.Isinst));
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~ILEmitterMatchTests"
```

Expected: All tests pass.

- [ ] **Step 5: Run full test suite to check for regressions**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore
```

Expected: All 365+ tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler.Tests/Emit/ILEmitterMatchTests.cs
git commit -m "Implement match statement emission with union variant dispatch"
```

---

## Task 5: Switch Expression Emission

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (EmitExpression, new EmitSwitch method)
- Modify: `src/Cobalt.Compiler.Tests/Emit/ILEmitterMatchTests.cs`

- [ ] **Step 1: Add EmitSwitch method**

Same type-test dispatch as EmitMatch, but each arm pushes a value and branches to end:

```csharp
private TypeReference? EmitSwitch(SwitchExpression switchExpr, BodyContext ctx)
{
    var il = ctx.IL;
    var endLabel = il.Create(OpCodes.Nop);
    TypeReference? resultType = null;

    // Evaluate subject and store in temp
    var subjectType = EmitExpression(switchExpr.Subject, ctx);
    var tempLocal = new VariableDefinition(subjectType ?? _module.TypeSystem.Object);
    ctx.Method.Body.Variables.Add(tempLocal);
    il.Emit(OpCodes.Stloc, tempLocal);

    foreach (var arm in switchExpr.Arms)
    {
        switch (arm.Pattern)
        {
            case VariantPattern variant:
            {
                var nextArm = il.Create(OpCodes.Nop);
                il.Emit(OpCodes.Ldloc, tempLocal);
                var variantKey = FindVariantType(variant.VariantName);
                if (variantKey != null)
                {
                    il.Emit(OpCodes.Isinst, variantKey);
                    il.Emit(OpCodes.Brfalse, nextArm);
                    il.Emit(OpCodes.Ldloc, tempLocal);
                    il.Emit(OpCodes.Castclass, variantKey);

                    if (variant.SubPatterns.Count > 0)
                    {
                        var castLocal = new VariableDefinition(variantKey);
                        ctx.Method.Body.Variables.Add(castLocal);
                        il.Emit(OpCodes.Stloc, castLocal);
                        for (int i = 0; i < variant.SubPatterns.Count && i < variantKey.Fields.Count; i++)
                        {
                            if (variant.SubPatterns[i] is VarPattern vp)
                            {
                                var fieldLocal = new VariableDefinition(variantKey.Fields[i].FieldType);
                                ctx.Method.Body.Variables.Add(fieldLocal);
                                ctx.Locals[vp.VariableName] = fieldLocal;
                                il.Emit(OpCodes.Ldloc, castLocal);
                                il.Emit(OpCodes.Ldfld, variantKey.Fields[i]);
                                il.Emit(OpCodes.Stloc, fieldLocal);
                            }
                        }
                    }
                    else
                    {
                        il.Emit(OpCodes.Pop);
                    }
                }
                else
                {
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Br, nextArm);
                }

                resultType = EmitExpression(arm.Expression, ctx);
                il.Emit(OpCodes.Br, endLabel);
                il.Append(nextArm);
                break;
            }
            case VarPattern varPat:
            {
                var catchLocal = new VariableDefinition(subjectType ?? _module.TypeSystem.Object);
                ctx.Method.Body.Variables.Add(catchLocal);
                ctx.Locals[varPat.VariableName] = catchLocal;
                il.Emit(OpCodes.Ldloc, tempLocal);
                il.Emit(OpCodes.Stloc, catchLocal);
                resultType = EmitExpression(arm.Expression, ctx);
                il.Emit(OpCodes.Br, endLabel);
                break;
            }
            default:
            {
                resultType = EmitExpression(arm.Expression, ctx);
                il.Emit(OpCodes.Br, endLabel);
                break;
            }
        }
    }

    // Safety net
    il.Emit(OpCodes.Ldstr, "No matching pattern");
    il.Emit(OpCodes.Newobj, ImportInvalidOperationExceptionCtor());
    il.Emit(OpCodes.Throw);

    il.Append(endLabel);
    return resultType;
}
```

- [ ] **Step 2: Wire EmitSwitch into EmitExpression**

Replace the `SwitchExpression` placeholder case (~line 649) with:

```csharp
case SwitchExpression switchExpr:
    return EmitSwitch(switchExpr, ctx);
```

- [ ] **Step 3: Write test**

Add to `ILEmitterMatchTests.cs`:

```csharp
[Fact]
public void Emit_SwitchExpression_EmitsIsinstAndReturnsValue()
{
    var asm = Emit("""
        union Color
        {
            Red(),
            Blue(),
        }
        public class Describer
        {
            public string Describe(Color c)
            {
                var name = switch (c)
                {
                    Red() => "red",
                    Blue() => "blue",
                };
                return name;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "Describer"), "Describe");
    Assert.True(HasOpCode(method, OpCodes.Isinst));
    Assert.True(HasOpCode(method, OpCodes.Ldstr));
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~ILEmitterMatchTests"
```

Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler.Tests/Emit/ILEmitterMatchTests.cs
git commit -m "Implement switch expression emission with union variant dispatch"
```

---

## Task 6: Using Var Disposal (try/finally)

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (EmitBlock, UsingVarDeclaration handling)
- Create: `src/Cobalt.Compiler.Tests/Emit/ILEmitterUsingTests.cs`

- [ ] **Step 1: Modify EmitBlock to handle using-var try/finally**

The key insight: when a `UsingVarDeclaration` is encountered in a block, all subsequent statements in that block must be wrapped in try/finally. Modify `EmitBlock` to detect this and emit the exception handler:

```csharp
private void EmitBlock(BlockStatement block, BodyContext ctx)
{
    var statements = block.Statements;
    for (int i = 0; i < statements.Count; i++)
    {
        if (statements[i] is UsingVarDeclaration usingDecl)
        {
            EmitUsingVarWithFinally(usingDecl, statements, i + 1, ctx);
            return; // remaining statements are inside the try block
        }
        EmitStatement(statements[i], ctx);
    }
}
```

Add the helper:

```csharp
private void EmitUsingVarWithFinally(UsingVarDeclaration usingDecl,
    IReadOnlyList<StatementNode> remainingStatements, int startIdx, BodyContext ctx)
{
    var il = ctx.IL;

    // Emit initializer and store in local
    var localType = usingDecl.Initializer != null
        ? (EmitExpression(usingDecl.Initializer, ctx) ?? _module.TypeSystem.Object)
        : _module.TypeSystem.Object;
    var usingLocal = new VariableDefinition(localType);
    ctx.Method.Body.Variables.Add(usingLocal);
    ctx.Locals[usingDecl.Name] = usingLocal;
    il.Emit(OpCodes.Stloc, usingLocal);

    // try block start
    var tryStart = il.Create(OpCodes.Nop);
    il.Append(tryStart);

    // Emit remaining statements
    for (int i = startIdx; i < remainingStatements.Count; i++)
    {
        if (remainingStatements[i] is UsingVarDeclaration nestedUsing)
        {
            EmitUsingVarWithFinally(nestedUsing, remainingStatements, i + 1, ctx);
            break; // nested using handles the rest
        }
        EmitStatement(remainingStatements[i], ctx);
    }

    var leaveTarget = il.Create(OpCodes.Nop);
    il.Emit(OpCodes.Leave, leaveTarget);

    // finally block
    var handlerStart = il.Create(OpCodes.Nop);
    il.Append(handlerStart);
    il.Emit(OpCodes.Ldloc, usingLocal);
    var endFinally = il.Create(OpCodes.Nop);
    il.Emit(OpCodes.Brfalse, endFinally);  // null check
    il.Emit(OpCodes.Ldloc, usingLocal);
    il.Emit(OpCodes.Callvirt, ImportDispose());
    il.Append(endFinally);
    il.Emit(OpCodes.Endfinally);

    var handlerEnd = il.Create(OpCodes.Nop);
    il.Append(handlerEnd);

    // Register exception handler
    var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
    {
        TryStart = tryStart,
        TryEnd = handlerStart,
        HandlerStart = handlerStart,
        HandlerEnd = handlerEnd,
    };
    ctx.Method.Body.ExceptionHandlers.Add(handler);

    il.Append(leaveTarget);
}
```

- [ ] **Step 2: Remove the old UsingVarDeclaration case from EmitStatement**

The old case (~line 479) that created a local without try/finally should be removed since `EmitBlock` now handles it.

- [ ] **Step 3: Write tests**

Create `src/Cobalt.Compiler.Tests/Emit/ILEmitterUsingTests.cs`:

```csharp
[Fact]
public void Emit_UsingVar_EmitsTryFinally()
{
    var asm = Emit("""
        public class UsingTest
        {
            public void Run(Stream s)
            {
                using var resource = s;
                var x = 1;
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "UsingTest"), "Run");
    Assert.True(method.Body.ExceptionHandlers.Count > 0);
    Assert.Equal(ExceptionHandlerType.Finally, method.Body.ExceptionHandlers[0].HandlerType);
}

[Fact]
public void Emit_UsingVar_CallsDispose()
{
    var asm = Emit("""
        public class DisposeTest
        {
            public void Run(Stream s)
            {
                using var resource = s;
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "DisposeTest"), "Run");
    // Should have a Callvirt to Dispose in the finally block
    Assert.True(Instructions(method).Any(i =>
        i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr && mr.Name == "Dispose"));
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~ILEmitterUsingTests"
```

Expected: All tests pass.

- [ ] **Step 5: Run full test suite**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore
```

Expected: All existing tests still pass (the UsingVarDeclaration behavior change should be backward-compatible since the old code was a placeholder anyway).

- [ ] **Step 6: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler.Tests/Emit/ILEmitterUsingTests.cs
git commit -m "Implement using-var disposal with try/finally and null-check"
```

---

## Task 7: Impl Block Emission

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (DeclareTopLevelMember, EmitMemberSignatures)
- Create: `src/Cobalt.Compiler.Tests/Emit/ILEmitterImplTests.cs`

- [ ] **Step 1: Add ImplBlock to Pass 1 (DeclareTopLevelMember)**

Add a case to the switch in `DeclareTopLevelMember` (~line 70):

```csharp
case ImplBlock impl:
    // Add interface to target type
    if (_typeDefs.TryGetValue(impl.TargetTypeName, out var targetType)
        && _typeDefs.TryGetValue(impl.TraitName, out var traitType))
    {
        if (!targetType.Interfaces.Any(i => i.InterfaceType.Name == traitType.Name))
            targetType.Interfaces.Add(new InterfaceImplementation(traitType));
    }
    break;
```

- [ ] **Step 2: Add ImplBlock to Pass 2 (EmitMemberSignatures)**

Add a case to the switch in `EmitMemberSignatures` (~line 160):

```csharp
case ImplBlock impl:
    if (_typeDefs.TryGetValue(impl.TargetTypeName, out var implTarget))
    {
        foreach (var member in impl.Members)
        {
            if (member is MethodDeclaration method)
            {
                EmitMethodSignature(method, implTarget);
            }
        }
    }
    break;
```

After calling `EmitMethodSignature`, override the method's attributes to ensure correct interface implementation metadata:

```csharp
// Force correct attributes for interface implementation
var methodDef = implTarget.Methods.Last(); // the method just added
methodDef.Attributes = MethodAttributes.Public | MethodAttributes.Virtual
    | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot;
```

- [ ] **Step 3: Write tests**

Create `src/Cobalt.Compiler.Tests/Emit/ILEmitterImplTests.cs`:

```csharp
[Fact]
public void Emit_ImplBlock_AddsInterface()
{
    var asm = Emit("""
        trait Greetable
        {
            public string Greet();
        }
        class Person
        {
            public string name;
        }
        impl Greetable for Person
        {
            public string Greet()
            {
                return "hello";
            }
        }
        """);
    var person = GetType(asm, "Person");
    Assert.Contains(person.Interfaces, i => i.InterfaceType.Name == "Greetable");
    var greet = GetMethod(person, "Greet");
    Assert.NotNull(greet);
}

[Fact]
public void Emit_ImplBlock_MethodHasBody()
{
    var asm = Emit("""
        trait Countable
        {
            public int Count();
        }
        class Bag
        {
            public int size;
        }
        impl Countable for Bag
        {
            public int Count()
            {
                return 42;
            }
        }
        """);
    var bag = GetType(asm, "Bag");
    var count = GetMethod(bag, "Count");
    Assert.True(HasOpCode(count, OpCodes.Ldc_I4));
    Assert.True(HasOpCode(count, OpCodes.Ret));
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~ILEmitterImplTests"
```

Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler.Tests/Emit/ILEmitterImplTests.cs
git commit -m "Implement impl block emission (interface addition + method bodies)"
```

---

## Task 8: Union Variant Constructor Fix

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (DeclareUnion method ~line 113)

The spec's Risk 2: variant constructors call `Object..ctor()` instead of the union base class constructor. Fix by making the base constructor `protected` (Family) and chaining variant constructors to it.

- [ ] **Step 1: Fix union base class constructor visibility**

In `DeclareUnion`, change the base class constructor from `Private` to `Family` (protected):

```csharp
// Change: MethodAttributes.Private → MethodAttributes.Family
```

- [ ] **Step 2: Chain variant constructors to base class constructor**

In the variant constructor emission, replace the call to `DefaultObjectCtor()` with a call to the base class's `.ctor`:

```csharp
// Replace: il.Emit(OpCodes.Call, DefaultObjectCtor());
// With: il.Emit(OpCodes.Call, baseCtor);
// Where baseCtor is a reference to the union base class's .ctor
```

- [ ] **Step 3: Run full test suite**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore
```

Expected: All tests pass (existing union tests should still work).

- [ ] **Step 4: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs
git commit -m "Fix union variant constructors to chain to base class ctor"
```

---

## Task 9: Entry Point Generation

**Files:**
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs` (constructor, Emit method)
- Modify: `src/Cobalt.Compiler/Driver/Compilation.cs`

- [ ] **Step 1: Detect top-level statements in Compilation.cs**

Add a helper to detect whether the merged `CompilationUnit` contains top-level statements (statements that are not inside a class/trait/union/impl):

```csharp
private static bool HasTopLevelStatements(CompilationUnit unit)
{
    return unit.Members.Any(m => m is StatementNode or VariableDeclaration or UsingVarDeclaration);
}
```

Pass this flag to the emitter.

- [ ] **Step 2: Add ModuleKind parameter to ILEmitter**

Modify the `ILEmitter` constructor to accept a `ModuleKind` parameter (default `Dll`). When `Console`, use `ModuleKind.Console` in `AssemblyDefinition.CreateAssembly`.

- [ ] **Step 3: Handle top-level statements in Emit method**

In the `Emit` method, after the three passes, if top-level statements exist:
- Create a synthetic `Program` class
- Add a `static void Main(string[] args)` method
- Emit top-level statements into the Main body
- Set `_module.EntryPoint` to the Main method

- [ ] **Step 4: Run full test suite**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore
```

Expected: All tests pass. Existing tests use class-based sources (no top-level statements), so the new code path is not exercised yet.

- [ ] **Step 5: Commit**

```bash
git add src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler/Driver/Compilation.cs
git commit -m "Add entry point generation for top-level statements"
```

---

## Task 10: Ownership Attribute Emission

**Files:**
- Modify: `src/Cobalt.Compiler/Cobalt.Compiler.csproj`
- Modify: `src/Cobalt.Compiler/Emit/ILEmitter.cs`
- Create: `src/Cobalt.Compiler.Tests/Emit/ILEmitterAttrTests.cs`

- [ ] **Step 1: Add Cobalt.Annotations project reference**

Add to `src/Cobalt.Compiler/Cobalt.Compiler.csproj`:

```xml
<ProjectReference Include="..\Cobalt.Annotations\Cobalt.Annotations.csproj" />
```

- [ ] **Step 2: Import attribute types in ILEmitter constructor**

Add fields and import the attribute constructor references:

```csharp
private readonly MethodReference? _ownedAttrCtor;
private readonly MethodReference? _borrowedAttrCtor;
private readonly MethodReference? _mutBorrowedAttrCtor;
```

In the constructor, after creating the module:

```csharp
_ownedAttrCtor = _module.ImportReference(typeof(Cobalt.Annotations.OwnedAttribute).GetConstructor(Type.EmptyTypes));
_borrowedAttrCtor = _module.ImportReference(typeof(Cobalt.Annotations.BorrowedAttribute).GetConstructor(Type.EmptyTypes));
_mutBorrowedAttrCtor = _module.ImportReference(typeof(Cobalt.Annotations.MutBorrowedAttribute).GetConstructor(Type.EmptyTypes));
```

- [ ] **Step 3: Attach attributes to parameters during EmitMethodSignature**

After creating each `ParameterDefinition`, check the ownership modifier from the AST and attach the appropriate attribute:

```csharp
if (param.Ownership == OwnershipModifier.Own && _ownedAttrCtor != null)
    paramDef.CustomAttributes.Add(new CustomAttribute(_ownedAttrCtor));
else if (param.Ownership == OwnershipModifier.Ref && _borrowedAttrCtor != null)
    paramDef.CustomAttributes.Add(new CustomAttribute(_borrowedAttrCtor));
else if (param.Ownership == OwnershipModifier.RefMut && _mutBorrowedAttrCtor != null)
    paramDef.CustomAttributes.Add(new CustomAttribute(_mutBorrowedAttrCtor));
```

- [ ] **Step 4: Attach attributes to return types**

When emitting method signatures, check `MethodDeclaration.ReturnOwnership`:

```csharp
if (method.ReturnOwnership == OwnershipModifier.Own && _ownedAttrCtor != null)
    methodDef.MethodReturnType.CustomAttributes.Add(new CustomAttribute(_ownedAttrCtor));
```

- [ ] **Step 5: Attach attributes to fields**

When emitting field definitions, check the AST's ownership modifier:

```csharp
if (field.Ownership == OwnershipModifier.Own && _ownedAttrCtor != null)
    fieldDef.CustomAttributes.Add(new CustomAttribute(_ownedAttrCtor));
```

- [ ] **Step 6: Write tests**

Create `src/Cobalt.Compiler.Tests/Emit/ILEmitterAttrTests.cs`:

```csharp
[Fact]
public void Emit_OwnParameter_HasOwnedAttribute()
{
    var asm = Emit("""
        public class Transfer
        {
            public void Take(Stream own s)
            {
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "Transfer"), "Take");
    var param = method.Parameters[0];
    Assert.Contains(param.CustomAttributes, a => a.AttributeType.Name == "OwnedAttribute");
}

[Fact]
public void Emit_RefParameter_HasBorrowedAttribute()
{
    var asm = Emit("""
        public class Borrow
        {
            public void Read(Stream ref s)
            {
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "Borrow"), "Read");
    var param = method.Parameters[0];
    Assert.Contains(param.CustomAttributes, a => a.AttributeType.Name == "BorrowedAttribute");
}

[Fact]
public void Emit_RefMutParameter_HasMutBorrowedAttribute()
{
    var asm = Emit("""
        public class Mutate
        {
            public void Write(Stream ref mut s)
            {
                return;
            }
        }
        """);
    var method = GetMethod(GetType(asm, "Mutate"), "Write");
    var param = method.Parameters[0];
    Assert.Contains(param.CustomAttributes, a => a.AttributeType.Name == "MutBorrowedAttribute");
}

[Fact]
public void Emit_OwnField_HasOwnedAttribute()
{
    var asm = Emit("""
        public class Owner
        {
            own Stream resource;
        }
        """);
    var type = GetType(asm, "Owner");
    var field = type.Fields.First(f => f.Name == "resource");
    Assert.Contains(field.CustomAttributes, a => a.AttributeType.Name == "OwnedAttribute");
}
```

- [ ] **Step 7: Run tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~ILEmitterAttrTests"
```

Expected: All tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Cobalt.Compiler/Cobalt.Compiler.csproj src/Cobalt.Compiler/Emit/ILEmitter.cs src/Cobalt.Compiler.Tests/Emit/ILEmitterAttrTests.cs
git commit -m "Emit ownership attributes ([Owned], [Borrowed], [MutBorrowed]) on parameters and fields"
```

---

## Task 11: Showcase Sample

**Files:**
- Create: `samples/cobalt-syntax/showcase.co`

- [ ] **Step 1: Write the showcase sample**

Create `samples/cobalt-syntax/showcase.co` — a resource pool demonstrating all Cobalt-specific features:

```cobalt
// Cobalt Showcase — demonstrates ownership, borrowing, pattern matching,
// traits, unions, and using-var disposal in a single file.

namespace Cobalt.Showcase;

use System;

union AcquireResult
{
    Acquired(string Name),
    Exhausted(string Reason),
}

trait Disposable
{
    public void Dispose();
}

public class ResourcePool : Disposable
{
    own string name;
    int capacity;
    int used;

    public ResourcePool(own string name, int capacity)
    {
        this.name = name;
        this.capacity = capacity;
        this.used = 0;
    }

    public own AcquireResult Acquire()
    {
        if (used >= capacity)
        {
            return Exhausted($"Pool {name} is full");
        }
        used = used + 1;
        return Acquired($"resource-{used}");
    }

    public void Report(ref string label)
    {
        Console.WriteLine($"{label}: {used}/{capacity} used");
        return;
    }

    public string DescribeResult(own AcquireResult result)
    {
        match (result)
        {
            Acquired(var res) => return $"Got: {res}",
            Exhausted(var reason) => return $"Failed: {reason}",
        };
        return "unknown";
    }

    public void Dispose()
    {
        Console.WriteLine($"Disposing pool {name}");
        return;
    }
}
```

- [ ] **Step 2: Verify it compiles**

```bash
dotnet run --project src/Cobalt.Compiler.Cli --no-restore -- --dump-ast samples/cobalt-syntax/showcase.co
```

Expected: AST dump prints with no errors.

```bash
dotnet run --project src/Cobalt.Compiler.Cli --no-restore -- samples/cobalt-syntax/showcase.co -o /tmp/cobalt-test/showcase.dll
```

Expected: "Compiled successfully" with at most warnings (no errors).

- [ ] **Step 3: Commit**

```bash
git add samples/cobalt-syntax/showcase.co
git commit -m "Add showcase.co demonstrating Cobalt-specific features"
```

---

## Task 12: Integration Tests

**Files:**
- Create: `src/Cobalt.Compiler.Tests/Integration/CompilationTests.cs`

- [ ] **Step 1: Write integration tests**

```csharp
using Cobalt.Compiler.Driver;
using Mono.Cecil;

namespace Cobalt.Compiler.Tests.Integration;

public class CompilationTests
{
    private static string FindSample(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var path = Path.Combine(dir, "samples", "cobalt-syntax", name);
            if (File.Exists(path)) return path;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException($"Sample not found: {name}");
    }

    [Fact]
    public void Compile_HelloCo_ProducesAssemblyWithExpectedTypes()
    {
        var output = Path.GetTempFileName() + ".dll";
        try
        {
            var compilation = new Compilation();
            var success = compilation.Compile(new[] { FindSample("hello.co") }, output);
            Assert.True(success, "Compilation failed: " + compilation.FormatDiagnostics());

            var asm = AssemblyDefinition.ReadAssembly(output);
            var typeNames = asm.MainModule.Types.Select(t => t.Name).ToList();
            Assert.Contains("Greeter", typeNames);
            Assert.Contains("MathHelper", typeNames);
            Assert.Contains("Printable", typeNames);
            Assert.Contains("Shape", typeNames);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void Compile_ShowcaseCo_ProducesAssemblyWithUnionVariants()
    {
        var output = Path.GetTempFileName() + ".dll";
        try
        {
            var compilation = new Compilation();
            var success = compilation.Compile(new[] { FindSample("showcase.co") }, output);
            Assert.True(success, "Compilation failed: " + compilation.FormatDiagnostics());

            var asm = AssemblyDefinition.ReadAssembly(output);
            var acquireResult = asm.MainModule.Types.First(t => t.Name == "AcquireResult");
            var nestedNames = acquireResult.NestedTypes.Select(n => n.Name).ToList();
            Assert.Contains("Acquired", nestedNames);
            Assert.Contains("Exhausted", nestedNames);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void Compile_ShowcaseCo_HasOwnershipAttributes()
    {
        var output = Path.GetTempFileName() + ".dll";
        try
        {
            var compilation = new Compilation();
            var success = compilation.Compile(new[] { FindSample("showcase.co") }, output);
            Assert.True(success, "Compilation failed: " + compilation.FormatDiagnostics());

            var asm = AssemblyDefinition.ReadAssembly(output);
            var pool = asm.MainModule.Types.First(t => t.Name == "ResourcePool");

            // own string name field should have [Owned]
            var nameField = pool.Fields.FirstOrDefault(f => f.Name == "name");
            Assert.NotNull(nameField);
            Assert.Contains(nameField.CustomAttributes, a => a.AttributeType.Name == "OwnedAttribute");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void Compile_UseAfterMove_ReportsError()
    {
        var compilation = new Compilation();
        compilation.Analyze("""
            class Foo
            {
                public void Take(own Stream s) { }
                public void Bad(own Stream input)
                {
                    Take(own input);
                    Take(own input);
                }
            }
            """, "test.co");
        Assert.True(compilation.Diagnostics.HasErrors);
        Assert.Contains(compilation.Diagnostics.All, d => d.Id == "CB3002");
    }
}
```

- [ ] **Step 2: Run integration tests**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore --filter "FullyQualifiedName~CompilationTests"
```

Expected: All tests pass.

- [ ] **Step 3: Run full test suite**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore
```

Expected: All tests pass (365+ existing + new tests).

- [ ] **Step 4: Commit**

```bash
git add src/Cobalt.Compiler.Tests/Integration/CompilationTests.cs
git commit -m "Add integration tests for end-to-end compilation and ownership attributes"
```

---

## Task 13: Update Roadmap and CLI

**Files:**
- Modify: `docs/implementation-roadmap.md`
- Modify: `src/Cobalt.Compiler.Cli/Program.cs`

- [ ] **Step 1: Update roadmap with M2 completion and M3 backlog**

Update `docs/implementation-roadmap.md`:
- Mark B.7 as complete
- Add Phase B M2 section as complete
- Add Phase B M3 backlog section

- [ ] **Step 2: Update CLI to copy Cobalt.Annotations.dll alongside output**

In `Program.cs`, after successful compilation, copy the annotations assembly:

```csharp
if (success)
{
    // Copy Cobalt.Annotations.dll alongside output for runtime attribute access
    var annotationsPath = Path.Combine(AppContext.BaseDirectory, "Cobalt.Annotations.dll");
    if (File.Exists(annotationsPath))
    {
        var targetDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            var targetPath = Path.Combine(targetDir, "Cobalt.Annotations.dll");
            if (!File.Exists(targetPath))
                File.Copy(annotationsPath, targetPath);
        }
    }
    Console.WriteLine($"Compiled successfully: {outputPath}");
    return 0;
}
```

- [ ] **Step 3: Fix emitter header comment (three-pass, not two-pass)**

Update the comment at the top of `ILEmitter.cs` to say "Three-pass design" instead of "Two-pass design".

- [ ] **Step 4: Final full test suite run**

```bash
dotnet test src/Cobalt.Compiler.Tests --no-restore
```

Expected: ALL tests pass.

- [ ] **Step 5: Commit**

```bash
git add docs/implementation-roadmap.md src/Cobalt.Compiler.Cli/Program.cs src/Cobalt.Compiler/Emit/ILEmitter.cs
git commit -m "Complete Phase B M2: update roadmap, CLI annotations copy, fix emitter comments"
```
