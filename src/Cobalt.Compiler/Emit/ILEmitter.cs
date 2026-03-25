namespace Cobalt.Compiler.Emit;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Cobalt.Compiler.Semantics;
using Cobalt.Compiler.Syntax;

// ──────────────────────────────────────────────
// IL Emitter — Cobalt AST → .NET IL (via Mono.Cecil)
//
// Two-pass design:
//   Pass 1 (DeclareTypes)  — create empty TypeDefinitions for every Cobalt type
//   Pass 2 (EmitMembers)   — add fields, methods and constructors with signatures
//   Pass 3 (EmitBodies)    — emit IL into method/constructor bodies
//
// Ownership metadata is encoded as custom attributes on parameters/return values,
// following the same [Owned]/[Borrowed]/[MutBorrowed] attribute set from Phase A.
// ──────────────────────────────────────────────

public sealed class ILEmitter
{
    private readonly ModuleDefinition _module;
    private readonly Scope _globalScope;
    private string _namespace = "";

    // User-defined type declarations keyed by simple name
    private readonly Dictionary<string, TypeDefinition> _typeDefs = new();

    // Synthetic class for top-level (free-standing) functions
    private TypeDefinition? _topLevelClass;

    // Deferred method bodies: (syntax, Cecil definition, parameter name list)
    private readonly List<PendingBody> _pendingBodies = [];

    // ──────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────

    public ILEmitter(string assemblyName, Version version, Scope globalScope)
    {
        _globalScope = globalScope;
        var asmName = new AssemblyNameDefinition(assemblyName, version);
        var assembly = AssemblyDefinition.CreateAssembly(asmName, assemblyName + ".dll", ModuleKind.Dll);
        _module = assembly.MainModule;
    }

    public AssemblyDefinition Emit(CompilationUnit unit)
    {
        _namespace = unit.Namespace?.Name ?? "";

        // Pass 1 — declare all types
        foreach (var member in unit.Members)
            DeclareTopLevelMember(member);

        // Pass 2 — emit member signatures
        foreach (var member in unit.Members)
            EmitMemberSignatures(member);

        // Pass 3 — emit method bodies
        foreach (var pending in _pendingBodies)
            EmitBody(pending);

        return _module.Assembly;
    }

    // ──────────────────────────────────────────────
    // Pass 1 — type declarations
    // ──────────────────────────────────────────────

    private void DeclareTopLevelMember(SyntaxNode member)
    {
        switch (member)
        {
            case ClassDeclaration cls:
                DeclareClass(cls);
                break;
            case TraitDeclaration trait:
                DeclareTrait(trait);
                break;
            case UnionDeclaration union:
                DeclareUnion(union);
                break;
            case ImplBlock impl:
                if (_typeDefs.TryGetValue(impl.TargetTypeName, out var targetType)
                    && _typeDefs.TryGetValue(impl.TraitName, out var traitType))
                {
                    if (!targetType.Interfaces.Any(i => i.InterfaceType.Name == traitType.Name))
                        targetType.Interfaces.Add(new InterfaceImplementation(traitType));
                }
                break;
            case MethodDeclaration:
                EnsureTopLevelClass();
                break;
        }
    }

    private TypeDefinition DeclareClass(ClassDeclaration cls)
    {
        var attrs = TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
        if (cls.Access == AccessModifier.Public) attrs |= TypeAttributes.Public;
        if (cls.IsSealed) attrs |= TypeAttributes.Sealed;
        if (cls.IsAbstract) attrs |= TypeAttributes.Abstract;

        var typeDef = new TypeDefinition(_namespace, cls.Name, attrs, _module.TypeSystem.Object);
        _module.Types.Add(typeDef);
        _typeDefs[cls.Name] = typeDef;
        return typeDef;
    }

    private TypeDefinition DeclareTrait(TraitDeclaration trait)
    {
        // Traits → .NET interfaces
        var attrs = TypeAttributes.Interface | TypeAttributes.Abstract
            | TypeAttributes.BeforeFieldInit | TypeAttributes.Public;
        var typeDef = new TypeDefinition(_namespace, trait.Name, attrs);
        _module.Types.Add(typeDef);
        _typeDefs[trait.Name] = typeDef;
        return typeDef;
    }

    private void DeclareUnion(UnionDeclaration union)
    {
        // Union → abstract sealed base class + one nested sealed class per variant
        var baseAttrs = TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit;
        if (union.Access == AccessModifier.Public) baseAttrs |= TypeAttributes.Public;

        var baseDef = new TypeDefinition(_namespace, union.Name, baseAttrs, _module.TypeSystem.Object);
        // Private constructor prevents external subclassing
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Family | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            _module.TypeSystem.Void);
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, DefaultObjectCtor());
        il.Emit(OpCodes.Ret);
        baseDef.Methods.Add(ctor);

        _module.Types.Add(baseDef);
        _typeDefs[union.Name] = baseDef;

        foreach (var variant in union.Variants)
        {
            var variantAttrs = TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.NestedPublic
                | TypeAttributes.BeforeFieldInit;
            var variantDef = new TypeDefinition("", variant.Name, variantAttrs, baseDef);
            baseDef.NestedTypes.Add(variantDef);

            // Register so field/param resolution can find them
            _typeDefs[$"{union.Name}.{variant.Name}"] = variantDef;
        }
    }

    private void EnsureTopLevelClass()
    {
        if (_topLevelClass != null) return;

        var ns = _namespace.Length > 0 ? _namespace : "Cobalt";
        _topLevelClass = new TypeDefinition(ns, "<TopLevel>",
            TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.NotPublic,
            _module.TypeSystem.Object);
        _module.Types.Add(_topLevelClass);
    }

    // ──────────────────────────────────────────────
    // Pass 2 — member signatures
    // ──────────────────────────────────────────────

    private void EmitMemberSignatures(SyntaxNode member)
    {
        switch (member)
        {
            case ClassDeclaration cls when _typeDefs.TryGetValue(cls.Name, out var typeDef):
                EmitClassSignatures(cls, typeDef);
                break;
            case TraitDeclaration trait when _typeDefs.TryGetValue(trait.Name, out var typeDef):
                EmitTraitSignatures(trait, typeDef);
                break;
            case UnionDeclaration union when _typeDefs.TryGetValue(union.Name, out var typeDef):
                EmitUnionSignatures(union, typeDef);
                break;
            case ImplBlock impl:
                if (_typeDefs.TryGetValue(impl.TargetTypeName, out var implTarget))
                {
                    foreach (var m in impl.Members)
                    {
                        if (m is MethodDeclaration method2)
                        {
                            EmitMethodSignature(method2, implTarget);
                            var methodDef = implTarget.Methods.Last();
                            methodDef.Attributes = MethodAttributes.Public | MethodAttributes.Virtual
                                | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot;
                        }
                    }
                }
                break;
            case MethodDeclaration method:
                EnsureTopLevelClass();
                EmitMethodSignature(method, _topLevelClass!);
                break;
        }
    }

    private void EmitClassSignatures(ClassDeclaration cls, TypeDefinition typeDef)
    {
        // Base types / interfaces
        foreach (var baseType in cls.BaseTypes)
        {
            var resolved = ResolveTypeSyntax(baseType);
            if (resolved is TypeDefinition baseDef && (baseDef.Attributes & TypeAttributes.Interface) != 0)
                typeDef.Interfaces.Add(new InterfaceImplementation(baseDef));
            else if (resolved != null)
                typeDef.BaseType = resolved;
        }

        // Ensure there's always a base type
        if (typeDef.BaseType == null)
            typeDef.BaseType = _module.TypeSystem.Object;

        // Add default constructor if none declared
        var hasCtor = cls.Members.OfType<ConstructorDeclaration>().Any();
        if (!hasCtor)
            AddDefaultConstructor(typeDef);

        foreach (var m in cls.Members)
            EmitClassMember(m, typeDef);
    }

    private void EmitClassMember(SyntaxNode member, TypeDefinition typeDef)
    {
        switch (member)
        {
            case FieldDeclaration field:
                EmitField(field, typeDef);
                break;
            case PropertyDeclaration prop:
                EmitProperty(prop, typeDef);
                break;
            case MethodDeclaration method:
                EmitMethodSignature(method, typeDef);
                break;
            case ConstructorDeclaration ctor:
                EmitConstructorSignature(ctor, typeDef);
                break;
        }
    }

    private void EmitTraitSignatures(TraitDeclaration trait, TypeDefinition typeDef)
    {
        foreach (var m in trait.Members)
        {
            if (m is MethodDeclaration method)
                EmitTraitMethodSignature(method, typeDef);
            else if (m is PropertyDeclaration prop)
                EmitTraitPropertySignature(prop, typeDef);
        }
    }

    private void EmitUnionSignatures(UnionDeclaration union, TypeDefinition baseDef)
    {
        foreach (var variant in union.Variants)
        {
            var key = $"{union.Name}.{variant.Name}";
            if (!_typeDefs.TryGetValue(key, out var variantDef)) continue;

            variantDef.BaseType = baseDef;

            // Add a constructor with fields as parameters
            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                _module.TypeSystem.Void);

            foreach (var field in variant.Fields)
            {
                var fieldDef = EmitField(field, variantDef);
                ctor.Parameters.Add(new ParameterDefinition(field.Name, ParameterAttributes.None, fieldDef.FieldType));
            }

            // Body: call base class .ctor, store each param into field, ret
            var baseCtor = new MethodReference(".ctor", _module.TypeSystem.Void, baseDef)
            {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default
            };
            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtor);
            for (int i = 0; i < variant.Fields.Count; i++)
            {
                var fieldName = variant.Fields[i].Name;
                if (variantDef.Fields.FirstOrDefault(f => f.Name == fieldName) is { } fieldDef2)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    il.Emit(OpCodes.Stfld, fieldDef2);
                }
            }
            il.Emit(OpCodes.Ret);
            ctor.Body.InitLocals = true;
            variantDef.Methods.Add(ctor);
        }
    }

    // ──────────────────────────────────────────────
    // Field, property, method signature helpers
    // ──────────────────────────────────────────────

    private FieldDefinition EmitField(FieldDeclaration field, TypeDefinition typeDef)
    {
        var attrs = FieldAttributes.Private;
        if (field.Access == AccessModifier.Public) attrs = FieldAttributes.Public;
        var fieldType = ResolveTypeSyntax(field.Type) ?? _module.TypeSystem.Object;
        var fieldDef = new FieldDefinition(field.Name, attrs, fieldType);
        typeDef.Fields.Add(fieldDef);
        return fieldDef;
    }

    private void EmitProperty(PropertyDeclaration prop, TypeDefinition typeDef)
    {
        var propType = ResolveTypeSyntax(prop.Type) ?? _module.TypeSystem.Object;
        var propDef = new PropertyDefinition(prop.Name, PropertyAttributes.None, propType);
        typeDef.Properties.Add(propDef);

        if (prop.HasGetter)
        {
            var getter = new MethodDefinition($"get_{prop.Name}",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                propType);
            if (prop.ExpressionBody != null)
                _pendingBodies.Add(new PendingBody(prop.ExpressionBody, getter, typeDef, []));
            else
                EmitNotImplementedBody(getter);
            typeDef.Methods.Add(getter);
            propDef.GetMethod = getter;
        }

        if (prop.HasSetter)
        {
            var setter = new MethodDefinition($"set_{prop.Name}",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                _module.TypeSystem.Void);
            setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, propType));
            EmitNotImplementedBody(setter);
            typeDef.Methods.Add(setter);
            propDef.SetMethod = setter;
        }
    }

    private void EmitTraitPropertySignature(PropertyDeclaration prop, TypeDefinition typeDef)
    {
        var propType = ResolveTypeSyntax(prop.Type) ?? _module.TypeSystem.Object;
        var propDef = new PropertyDefinition(prop.Name, PropertyAttributes.None, propType);
        typeDef.Properties.Add(propDef);

        if (prop.HasGetter)
        {
            var getter = new MethodDefinition($"get_{prop.Name}",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot,
                propType);
            typeDef.Methods.Add(getter);
            propDef.GetMethod = getter;
        }
    }

    private MethodDefinition EmitMethodSignature(MethodDeclaration method, TypeDefinition typeDef)
    {
        var attrs = BuildMethodAttributes(method.Access, method.IsStatic, method.IsAbstract, method.IsVirtual, method.IsOverride);
        var returnType = ResolveTypeSyntax(method.ReturnType) ?? _module.TypeSystem.Void;
        var methodDef = new MethodDefinition(method.Name, attrs, returnType);

        foreach (var param in method.Parameters)
        {
            var paramType = ResolveTypeSyntax(param.Type) ?? _module.TypeSystem.Object;
            methodDef.Parameters.Add(new ParameterDefinition(param.Name, ParameterAttributes.None, paramType));
        }

        if (method.Body != null)
        {
            var paramNames = method.Parameters.Select(p => p.Name).ToList();
            _pendingBodies.Add(new PendingBody(method.Body, methodDef, typeDef, paramNames));
        }
        else if (!method.IsAbstract)
        {
            EmitNotImplementedBody(methodDef);
        }

        typeDef.Methods.Add(methodDef);
        return methodDef;
    }

    private void EmitTraitMethodSignature(MethodDeclaration method, TypeDefinition typeDef)
    {
        var attrs = MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual
            | MethodAttributes.HideBySig | MethodAttributes.NewSlot;
        var returnType = ResolveTypeSyntax(method.ReturnType) ?? _module.TypeSystem.Void;
        var methodDef = new MethodDefinition(method.Name, attrs, returnType);

        foreach (var param in method.Parameters)
        {
            var paramType = ResolveTypeSyntax(param.Type) ?? _module.TypeSystem.Object;
            methodDef.Parameters.Add(new ParameterDefinition(param.Name, ParameterAttributes.None, paramType));
        }

        typeDef.Methods.Add(methodDef);
    }

    private void EmitConstructorSignature(ConstructorDeclaration ctor, TypeDefinition typeDef)
    {
        var attrs = MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
        if (ctor.Access == AccessModifier.Public) attrs |= MethodAttributes.Public;

        var ctorDef = new MethodDefinition(".ctor", attrs, _module.TypeSystem.Void);

        foreach (var param in ctor.Parameters)
        {
            var paramType = ResolveTypeSyntax(param.Type) ?? _module.TypeSystem.Object;
            ctorDef.Parameters.Add(new ParameterDefinition(param.Name, ParameterAttributes.None, paramType));
        }

        if (ctor.Body != null)
        {
            // Insert call to base constructor before the user body
            var paramNames = ctor.Parameters.Select(p => p.Name).ToList();
            _pendingBodies.Add(new PendingBody(ctor.Body, ctorDef, typeDef, paramNames, isConstructor: true));
        }
        else
        {
            AddDefaultConstructor(typeDef);
            return;
        }

        typeDef.Methods.Add(ctorDef);
    }

    // ──────────────────────────────────────────────
    // Pass 3 — method body emission
    // ──────────────────────────────────────────────

    private void EmitBody(PendingBody pending)
    {
        var il = pending.Method.Body.GetILProcessor();
        pending.Method.Body.InitLocals = true;
        var ctx = new BodyContext(pending.Method, pending.ContainingType, pending.ParamNames, il, _module);

        if (pending.IsConstructor)
        {
            // Call base .ctor first
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, DefaultObjectCtor());
        }

        if (pending.Node is BlockStatement block)
        {
            EmitBlock(block, ctx);
        }
        else if (pending.Node is ExpressionNode expr)
        {
            // Expression body (property getter) — emit expr and return
            EmitExpression(expr, ctx);
            il.Emit(OpCodes.Ret);
            return;
        }

        // Ensure method ends with ret (may already have one from an early return)
        if (!EndsWithRet(pending.Method))
        {
            // For non-void methods, push a default value so the stack isn't empty
            var retType = pending.Method.ReturnType;
            if (retType.FullName != "System.Void")
            {
                if (retType.IsValueType)
                    EmitDefaultValue(il, retType);
                else
                    il.Emit(OpCodes.Ldnull);
            }
            il.Emit(OpCodes.Ret);
        }
    }

    private void EmitBlock(BlockStatement block, BodyContext ctx)
    {
        EmitStatementRange(block.Statements, 0, ctx);
    }

    private void EmitStatementRange(IReadOnlyList<StatementNode> statements, int startIdx, BodyContext ctx)
    {
        for (int i = startIdx; i < statements.Count; i++)
        {
            if (statements[i] is UsingVarDeclaration usingDecl)
            {
                EmitUsingVarWithFinally(usingDecl, statements, i + 1, ctx);
                return; // remaining statements are inside the try block
            }
            EmitStatement(statements[i], ctx);
        }
    }

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

        // Try block start
        var tryStart = il.Create(OpCodes.Nop);
        il.Append(tryStart);

        // Emit remaining statements (recursive for nested using vars)
        EmitStatementRange(remainingStatements, startIdx, ctx);

        var leaveTarget = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Leave, leaveTarget);

        // Finally block
        var handlerStart = il.Create(OpCodes.Nop);
        il.Append(handlerStart);

        // Null check before calling Dispose
        il.Emit(OpCodes.Ldloc, usingLocal);
        var endFinally = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Brfalse, endFinally);
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

    private void EmitStatement(StatementNode stmt, BodyContext ctx)
    {
        var il = ctx.IL;
        switch (stmt)
        {
            case BlockStatement block:
                EmitBlock(block, ctx);
                break;

            case VariableDeclaration varDecl:
                TypeReference varType = _module.TypeSystem.Object;
                if (varDecl.Initializer != null)
                {
                    varType = EmitExpression(varDecl.Initializer, ctx) ?? _module.TypeSystem.Object;
                }
                var local = new VariableDefinition(varType);
                ctx.Method.Body.Variables.Add(local);
                ctx.Locals[varDecl.Name] = local;
                if (varDecl.Initializer != null)
                    il.Emit(OpCodes.Stloc, local);
                break;

            case UsingVarDeclaration usingDecl:
                // Handled by EmitStatementRange/EmitUsingVarWithFinally when inside a block.
                // Fallback: emit as simple local (no try/finally) if reached directly.
                EmitUsingVarWithFinally(usingDecl, Array.Empty<StatementNode>(), 0, ctx);
                break;

            case ReturnStatement ret:
                if (ret.Expression != null)
                    EmitExpression(ret.Expression, ctx);
                il.Emit(OpCodes.Ret);
                break;

            case ExpressionStatement exprStmt:
                var resultType = EmitExpression(exprStmt.Expression, ctx);
                // Pop non-void results of expression statements
                if (resultType != null && resultType != _module.TypeSystem.Void)
                    il.Emit(OpCodes.Pop);
                break;

            case IfStatement ifStmt:
                EmitIf(ifStmt, ctx);
                break;

            case WhileStatement whileStmt:
                EmitWhile(whileStmt, ctx);
                break;

            case ForStatement forStmt:
                EmitFor(forStmt, ctx);
                break;

            case ForEachStatement forEach:
                EmitForEach(forEach, ctx);
                break;

            case MatchStatement matchStmt:
                EmitMatch(matchStmt, ctx);
                break;

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

            default:
                // Unsupported statement — emit nop as placeholder
                il.Emit(OpCodes.Nop);
                break;
        }
    }

    private void EmitIf(IfStatement ifStmt, BodyContext ctx)
    {
        var il = ctx.IL;
        var elseLabel = il.Create(OpCodes.Nop);
        var endLabel = il.Create(OpCodes.Nop);

        EmitExpression(ifStmt.Condition, ctx);
        il.Emit(OpCodes.Brfalse, elseLabel);

        EmitStatement(ifStmt.ThenBody, ctx);

        if (ifStmt.ElseBody != null)
        {
            il.Emit(OpCodes.Br, endLabel);
            il.Append(elseLabel);
            EmitStatement(ifStmt.ElseBody, ctx);
            il.Append(endLabel);
        }
        else
        {
            il.Append(elseLabel);
        }
    }

    private void EmitWhile(WhileStatement whileStmt, BodyContext ctx)
    {
        var il = ctx.IL;
        var condLabel = il.Create(OpCodes.Nop);
        var endLabel = il.Create(OpCodes.Nop);

        il.Append(condLabel);
        EmitExpression(whileStmt.Condition, ctx);
        il.Emit(OpCodes.Brfalse, endLabel);

        ctx.LoopLabels.Push((endLabel, condLabel));
        EmitStatement(whileStmt.Body, ctx);
        ctx.LoopLabels.Pop();

        il.Emit(OpCodes.Br, condLabel);
        il.Append(endLabel);
    }

    private void EmitFor(ForStatement forStmt, BodyContext ctx)
    {
        var il = ctx.IL;
        if (forStmt.Initializer != null)
            EmitStatement(forStmt.Initializer, ctx);

        var condLabel = il.Create(OpCodes.Nop);
        var endLabel = il.Create(OpCodes.Nop);
        var incrementLabel = il.Create(OpCodes.Nop);

        il.Append(condLabel);
        if (forStmt.Condition != null)
        {
            EmitExpression(forStmt.Condition, ctx);
            il.Emit(OpCodes.Brfalse, endLabel);
        }

        ctx.LoopLabels.Push((endLabel, incrementLabel));
        EmitStatement(forStmt.Body, ctx);
        ctx.LoopLabels.Pop();

        il.Append(incrementLabel);
        if (forStmt.Increment != null)
        {
            var t = EmitExpression(forStmt.Increment, ctx);
            if (t != null && t != _module.TypeSystem.Void) il.Emit(OpCodes.Pop);
        }

        il.Emit(OpCodes.Br, condLabel);
        il.Append(endLabel);
    }

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

    // ──────────────────────────────────────────────
    // Match statement emission
    // ──────────────────────────────────────────────

    private void EmitMatch(MatchStatement matchStmt, BodyContext ctx)
    {
        var il = ctx.IL;
        var endLabel = il.Create(OpCodes.Nop);

        // Evaluate subject and store in a temp local
        var subjectType = EmitExpression(matchStmt.Subject, ctx) ?? _module.TypeSystem.Object;
        var subjectLocal = new VariableDefinition(subjectType);
        ctx.Method.Body.Variables.Add(subjectLocal);
        il.Emit(OpCodes.Stloc, subjectLocal);

        foreach (var arm in matchStmt.Arms)
        {
            switch (arm.Pattern)
            {
                case VariantPattern variant:
                {
                    var variantType = FindVariantType(variant.VariantName);
                    if (variantType == null)
                    {
                        il.Emit(OpCodes.Nop);
                        break;
                    }

                    var nextArmLabel = il.Create(OpCodes.Nop);

                    // Type test: isinst + brfalse
                    il.Emit(OpCodes.Ldloc, subjectLocal);
                    il.Emit(OpCodes.Isinst, variantType);
                    il.Emit(OpCodes.Brfalse, nextArmLabel);

                    // Cast and extract fields for sub-patterns
                    il.Emit(OpCodes.Ldloc, subjectLocal);
                    il.Emit(OpCodes.Castclass, variantType);

                    var castedLocal = new VariableDefinition(variantType);
                    ctx.Method.Body.Variables.Add(castedLocal);
                    il.Emit(OpCodes.Stloc, castedLocal);

                    // Bind sub-pattern variables to fields
                    var fields = variantType.Fields;
                    for (int i = 0; i < variant.SubPatterns.Count && i < fields.Count; i++)
                    {
                        if (variant.SubPatterns[i] is VarPattern vp)
                        {
                            il.Emit(OpCodes.Ldloc, castedLocal);
                            il.Emit(OpCodes.Ldfld, fields[i]);
                            var fieldLocal = new VariableDefinition(fields[i].FieldType);
                            ctx.Method.Body.Variables.Add(fieldLocal);
                            ctx.Locals[vp.VariableName] = fieldLocal;
                            il.Emit(OpCodes.Stloc, fieldLocal);
                        }
                        // DiscardPattern — no binding needed
                    }

                    EmitArmBody(arm.Body, ctx);
                    il.Emit(OpCodes.Br, endLabel);
                    il.Append(nextArmLabel);
                    break;
                }

                case VarPattern varPat:
                {
                    // Catch-all: bind subject to local, no type test
                    var catchAllLocal = new VariableDefinition(subjectType);
                    ctx.Method.Body.Variables.Add(catchAllLocal);
                    ctx.Locals[varPat.VariableName] = catchAllLocal;
                    il.Emit(OpCodes.Ldloc, subjectLocal);
                    il.Emit(OpCodes.Stloc, catchAllLocal);

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
            }
        }

        // Safety-net: throw new InvalidOperationException("No matching pattern")
        il.Emit(OpCodes.Ldstr, "No matching pattern");
        il.Emit(OpCodes.Newobj, ImportInvalidOperationExceptionCtor());
        il.Emit(OpCodes.Throw);

        il.Append(endLabel);
    }

    private TypeReference? EmitSwitch(SwitchExpression switchExpr, BodyContext ctx)
    {
        var il = ctx.IL;
        var endLabel = il.Create(OpCodes.Nop);

        // Evaluate subject and store in a temp local
        var subjectType = EmitExpression(switchExpr.Subject, ctx) ?? _module.TypeSystem.Object;
        var subjectLocal = new VariableDefinition(subjectType);
        ctx.Method.Body.Variables.Add(subjectLocal);
        il.Emit(OpCodes.Stloc, subjectLocal);

        TypeReference? resultType = null;

        foreach (var arm in switchExpr.Arms)
        {
            switch (arm.Pattern)
            {
                case VariantPattern variant:
                {
                    var variantType = FindVariantType(variant.VariantName);
                    if (variantType == null)
                    {
                        il.Emit(OpCodes.Nop);
                        break;
                    }

                    var nextArmLabel = il.Create(OpCodes.Nop);

                    // Type test: isinst + brfalse
                    il.Emit(OpCodes.Ldloc, subjectLocal);
                    il.Emit(OpCodes.Isinst, variantType);
                    il.Emit(OpCodes.Brfalse, nextArmLabel);

                    // Cast and extract fields for sub-patterns
                    il.Emit(OpCodes.Ldloc, subjectLocal);
                    il.Emit(OpCodes.Castclass, variantType);

                    var castedLocal = new VariableDefinition(variantType);
                    ctx.Method.Body.Variables.Add(castedLocal);
                    il.Emit(OpCodes.Stloc, castedLocal);

                    // Bind sub-pattern variables to fields
                    var fields = variantType.Fields;
                    for (int i = 0; i < variant.SubPatterns.Count && i < fields.Count; i++)
                    {
                        if (variant.SubPatterns[i] is VarPattern vp)
                        {
                            il.Emit(OpCodes.Ldloc, castedLocal);
                            il.Emit(OpCodes.Ldfld, fields[i]);
                            var fieldLocal = new VariableDefinition(fields[i].FieldType);
                            ctx.Method.Body.Variables.Add(fieldLocal);
                            ctx.Locals[vp.VariableName] = fieldLocal;
                            il.Emit(OpCodes.Stloc, fieldLocal);
                        }
                        // DiscardPattern — no binding needed
                    }

                    resultType = EmitExpression(arm.Expression, ctx);
                    il.Emit(OpCodes.Br, endLabel);
                    il.Append(nextArmLabel);
                    break;
                }

                case VarPattern varPat:
                {
                    // Catch-all: bind subject to local, no type test
                    var catchAllLocal = new VariableDefinition(subjectType);
                    ctx.Method.Body.Variables.Add(catchAllLocal);
                    ctx.Locals[varPat.VariableName] = catchAllLocal;
                    il.Emit(OpCodes.Ldloc, subjectLocal);
                    il.Emit(OpCodes.Stloc, catchAllLocal);

                    resultType = EmitExpression(arm.Expression, ctx);
                    il.Emit(OpCodes.Br, endLabel);
                    break;
                }

                case DiscardPattern:
                {
                    // Wildcard: emit expression unconditionally
                    resultType = EmitExpression(arm.Expression, ctx);
                    il.Emit(OpCodes.Br, endLabel);
                    break;
                }
            }
        }

        // Safety-net: throw new InvalidOperationException("No matching pattern")
        il.Emit(OpCodes.Ldstr, "No matching pattern");
        il.Emit(OpCodes.Newobj, ImportInvalidOperationExceptionCtor());
        il.Emit(OpCodes.Throw);

        il.Append(endLabel);

        return resultType;
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
        // Check composite keys like "Shape.Circle"
        foreach (var kvp in _typeDefs)
        {
            if (kvp.Key.EndsWith("." + variantName))
                return kvp.Value;
        }

        // Check nested types of all type definitions
        foreach (var kvp in _typeDefs)
        {
            foreach (var nested in kvp.Value.NestedTypes)
            {
                if (nested.Name == variantName)
                    return nested;
            }
        }

        return null;
    }

    // ──────────────────────────────────────────────
    // Expression emission — returns the .NET type pushed onto the stack
    // ──────────────────────────────────────────────

    private TypeReference? EmitExpression(ExpressionNode expr, BodyContext ctx)
    {
        var il = ctx.IL;
        switch (expr)
        {
            case LiteralExpression lit:
                return EmitLiteral(lit, il);

            case IdentifierExpression ident:
                return EmitIdentifier(ident, ctx);

            case OwnExpression own:
                // 'own' is a compile-time concept — just emit the inner expression
                return EmitExpression(own.Inner, ctx);

            case RefMutExpression refMut:
                // 'ref mut' is a compile-time concept — just emit the inner expression
                return EmitExpression(refMut.Inner, ctx);

            case BinaryExpression bin:
                return EmitBinary(bin, ctx);

            case UnaryExpression unary:
                return EmitUnary(unary, ctx);

            case MemberAccessExpression memberAccess:
                return EmitMemberAccess(memberAccess, ctx);

            case InvocationExpression invocation:
                return EmitInvocation(invocation, ctx);

            case ObjectCreationExpression objCreate:
                return EmitObjectCreation(objCreate, ctx);

            case AssignmentExpression assign:
                return EmitAssignment(assign, ctx);

            case IsPatternExpression:
                // Pattern matching expression — emit true as placeholder
                il.Emit(OpCodes.Ldc_I4_1);
                return _module.TypeSystem.Boolean;

            case SwitchExpression switchExpr:
                return EmitSwitch(switchExpr, ctx);

            case InterpolatedStringExpression interp:
                return EmitInterpolatedString(interp, ctx);

            case IndexExpression idx:
                return EmitIndex(idx, ctx);

            case ThisExpression:
                il.Emit(OpCodes.Ldarg_0);
                return ctx.ContainingType;

            case CastExpression cast:
                var innerType = EmitExpression(cast.Expression, ctx);
                var targetType = ResolveTypeSyntax(cast.Type);
                if (targetType != null && targetType != innerType)
                    il.Emit(OpCodes.Castclass, targetType);
                return targetType;

            default:
                il.Emit(OpCodes.Ldnull);
                return _module.TypeSystem.Object;
        }
    }

    private TypeReference EmitLiteral(LiteralExpression lit, ILProcessor il)
    {
        switch (lit.Kind)
        {
            case LiteralKind.Int:
                il.Emit(OpCodes.Ldc_I4, lit.Value is int i ? i : Convert.ToInt32(lit.Value));
                return _module.TypeSystem.Int32;
            case LiteralKind.Float:
                il.Emit(OpCodes.Ldc_R8, lit.Value is double d ? d : Convert.ToDouble(lit.Value));
                return _module.TypeSystem.Double;
            case LiteralKind.String:
                il.Emit(OpCodes.Ldstr, lit.Value?.ToString() ?? "");
                return _module.TypeSystem.String;
            case LiteralKind.Bool:
                il.Emit(lit.Value is true ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                return _module.TypeSystem.Boolean;
            case LiteralKind.Null:
                il.Emit(OpCodes.Ldnull);
                return _module.TypeSystem.Object;
            case LiteralKind.Char:
                var ch = lit.Value is char c ? c : (char)0;
                il.Emit(OpCodes.Ldc_I4, (int)ch);
                return _module.TypeSystem.Char;
            default:
                il.Emit(OpCodes.Ldnull);
                return _module.TypeSystem.Object;
        }
    }

    private TypeReference? EmitIdentifier(IdentifierExpression ident, BodyContext ctx)
    {
        var il = ctx.IL;
        var name = ident.Name;

        // Local variable?
        if (ctx.Locals.TryGetValue(name, out var local))
        {
            il.Emit(OpCodes.Ldloc, local);
            return local.VariableType;
        }

        // Parameter? (arg 0 = this for instance methods)
        var paramOffset = ctx.Method.IsStatic ? 0 : 1;
        for (int i = 0; i < ctx.ParamNames.Count; i++)
        {
            if (ctx.ParamNames[i] == name)
            {
                il.Emit(OpCodes.Ldarg, i + paramOffset);
                return ctx.Method.Parameters[i].ParameterType;
            }
        }

        // Field on containing type?
        if (ctx.ContainingType != null)
        {
            var field = ctx.ContainingType.Fields.FirstOrDefault(f => f.Name == name);
            if (field != null)
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, field);
                return field.FieldType;
            }
        }

        // Unknown — push null
        il.Emit(OpCodes.Ldnull);
        return _module.TypeSystem.Object;
    }

    private TypeReference? EmitBinary(BinaryExpression bin, BodyContext ctx)
    {
        var il = ctx.IL;

        // Short-circuit operators must not eagerly evaluate right side
        if (bin.Operator == TokenKind.AmpersandAmpersand)
        {
            var falseLabel = il.Create(OpCodes.Nop);
            var endLabel = il.Create(OpCodes.Nop);
            EmitExpression(bin.Left, ctx);
            il.Emit(OpCodes.Brfalse, falseLabel);
            EmitExpression(bin.Right, ctx);
            il.Emit(OpCodes.Br, endLabel);
            il.Append(falseLabel);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Append(endLabel);
            return _module.TypeSystem.Boolean;
        }
        if (bin.Operator == TokenKind.PipePipe)
        {
            var trueLabel = il.Create(OpCodes.Nop);
            var endLabel = il.Create(OpCodes.Nop);
            EmitExpression(bin.Left, ctx);
            il.Emit(OpCodes.Brtrue, trueLabel);
            EmitExpression(bin.Right, ctx);
            il.Emit(OpCodes.Br, endLabel);
            il.Append(trueLabel);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Append(endLabel);
            return _module.TypeSystem.Boolean;
        }

        EmitExpression(bin.Left, ctx);
        EmitExpression(bin.Right, ctx);
        switch (bin.Operator)
        {
            case TokenKind.Plus: il.Emit(OpCodes.Add); return _module.TypeSystem.Int32;
            case TokenKind.Minus: il.Emit(OpCodes.Sub); return _module.TypeSystem.Int32;
            case TokenKind.Star: il.Emit(OpCodes.Mul); return _module.TypeSystem.Int32;
            case TokenKind.Slash: il.Emit(OpCodes.Div); return _module.TypeSystem.Int32;
            case TokenKind.Percent: il.Emit(OpCodes.Rem); return _module.TypeSystem.Int32;
            case TokenKind.EqualsEquals: il.Emit(OpCodes.Ceq); return _module.TypeSystem.Boolean;
            case TokenKind.BangEquals: il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return _module.TypeSystem.Boolean;
            case TokenKind.Less: il.Emit(OpCodes.Clt); return _module.TypeSystem.Boolean;
            case TokenKind.Greater: il.Emit(OpCodes.Cgt); return _module.TypeSystem.Boolean;
            case TokenKind.LessEquals: il.Emit(OpCodes.Cgt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return _module.TypeSystem.Boolean;
            case TokenKind.GreaterEquals: il.Emit(OpCodes.Clt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return _module.TypeSystem.Boolean;
            default:
                il.Emit(OpCodes.Pop); il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldnull);
                return _module.TypeSystem.Object;
        }
    }

    private TypeReference? EmitUnary(UnaryExpression unary, BodyContext ctx)
    {
        var il = ctx.IL;
        EmitExpression(unary.Operand, ctx);
        switch (unary.Operator)
        {
            case TokenKind.Minus: il.Emit(OpCodes.Neg); return _module.TypeSystem.Int32;
            case TokenKind.Bang: il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return _module.TypeSystem.Boolean;
            default: return _module.TypeSystem.Object;
        }
    }

    private TypeReference? EmitMemberAccess(MemberAccessExpression memberAccess, BodyContext ctx)
    {
        var il = ctx.IL;
        var targetType = EmitExpression(memberAccess.Target, ctx);

        // Try to find a field on the target type
        if (targetType is TypeDefinition td)
        {
            var field = td.Fields.FirstOrDefault(f => f.Name == memberAccess.MemberName);
            if (field != null)
            {
                il.Emit(OpCodes.Ldfld, field);
                return field.FieldType;
            }
        }

        // Try in type map
        var typeName = memberAccess.Target is IdentifierExpression id ? id.Name : null;
        if (typeName != null && _typeDefs.TryGetValue(typeName, out var td2))
        {
            var field = td2.Fields.FirstOrDefault(f => f.Name == memberAccess.MemberName);
            if (field != null)
            {
                // Pop the pushed target (we already have it on the stack)
                il.Emit(OpCodes.Ldfld, field);
                return field.FieldType;
            }
        }

        // Unknown member — pop target, push null
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldnull);
        return _module.TypeSystem.Object;
    }

    private TypeReference? EmitInvocation(InvocationExpression invocation, BodyContext ctx)
    {
        var il = ctx.IL;

        // Determine the method being called
        MethodReference? methodRef = null;
        TypeReference? returnType = null;

        if (invocation.Target is MemberAccessExpression memberAccess)
        {
            // instance.Method(...) or Type.Method(...)
            var targetName = memberAccess.Target is IdentifierExpression id2 ? id2.Name : null;

            // Check if it's a static call on a known type
            if (targetName != null && _typeDefs.TryGetValue(targetName, out var staticType))
            {
                methodRef = staticType.Methods.FirstOrDefault(m => m.Name == memberAccess.MemberName);
            }

            if (methodRef == null)
            {
                // Emit target (push instance)
                EmitExpression(memberAccess.Target, ctx);
            }

            // Emit arguments
            foreach (var arg in invocation.Arguments)
                EmitExpression(arg.Expression, ctx);

            if (methodRef != null)
            {
                il.Emit(OpCodes.Call, methodRef);
                returnType = methodRef.ReturnType;
            }
            else
            {
                // Unknown method — pop all and push null
                for (int i = 0; i < invocation.Arguments.Count + 1; i++)
                    il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldnull);
                returnType = _module.TypeSystem.Object;
            }
        }
        else if (invocation.Target is IdentifierExpression identExpr)
        {
            // Direct function call (top-level or method in same class)
            MethodDefinition? found = null;
            if (_topLevelClass != null)
                found = _topLevelClass.Methods.FirstOrDefault(m => m.Name == identExpr.Name);
            if (found == null && ctx.ContainingType != null)
                found = ctx.ContainingType.Methods.FirstOrDefault(m => m.Name == identExpr.Name);

            if (!found?.IsStatic ?? true)
            {
                // Instance call — push this
                il.Emit(OpCodes.Ldarg_0);
            }

            foreach (var arg in invocation.Arguments)
                EmitExpression(arg.Expression, ctx);

            if (found != null)
            {
                il.Emit(found.IsStatic ? OpCodes.Call : OpCodes.Callvirt, found);
                returnType = found.ReturnType;
            }
            else
            {
                for (int i = 0; i < invocation.Arguments.Count + 1; i++)
                    il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldnull);
                returnType = _module.TypeSystem.Object;
            }
        }
        else
        {
            // Complex target — push null
            il.Emit(OpCodes.Ldnull);
            returnType = _module.TypeSystem.Object;
        }

        return returnType;
    }

    private TypeReference? EmitObjectCreation(ObjectCreationExpression objCreate, BodyContext ctx)
    {
        var il = ctx.IL;
        if (_typeDefs.TryGetValue(objCreate.Type.Name, out var typeDef))
        {
            // Find the matching constructor
            var ctor = typeDef.Methods
                .Where(m => m.Name == ".ctor" && m.Parameters.Count == objCreate.Arguments.Count)
                .FirstOrDefault();

            // Fall back to default constructor
            ctor ??= typeDef.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0);

            if (ctor != null)
            {
                if (ctor.Parameters.Count == objCreate.Arguments.Count)
                {
                    foreach (var arg in objCreate.Arguments)
                        EmitExpression(arg.Expression, ctx);
                }
                else
                {
                    // Arg count mismatch — evaluate and discard
                    foreach (var arg in objCreate.Arguments)
                    {
                        EmitExpression(arg.Expression, ctx);
                        il.Emit(OpCodes.Pop);
                    }
                }

                il.Emit(OpCodes.Newobj, ctor);

                // Handle object initializers
                if (objCreate.InitializerClauses is { Count: > 0 })
                {
                    var tmpLocal = new VariableDefinition(typeDef);
                    ctx.Method.Body.Variables.Add(tmpLocal);
                    il.Emit(OpCodes.Stloc, tmpLocal);

                    foreach (var clause in objCreate.InitializerClauses)
                    {
                        var field = typeDef.Fields.FirstOrDefault(f => f.Name == clause.FieldName);
                        if (field != null)
                        {
                            il.Emit(OpCodes.Ldloc, tmpLocal);
                            EmitExpression(clause.Value, ctx);
                            il.Emit(OpCodes.Stfld, field);
                        }
                    }
                    il.Emit(OpCodes.Ldloc, tmpLocal);
                }

                return typeDef;
            }
        }

        il.Emit(OpCodes.Ldnull);
        return _module.TypeSystem.Object;
    }

    private TypeReference? EmitAssignment(AssignmentExpression assign, BodyContext ctx)
    {
        var il = ctx.IL;

        if (assign.Target is IdentifierExpression ident)
        {
            EmitExpression(assign.Value, ctx);

            if (ctx.Locals.TryGetValue(ident.Name, out var local))
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Stloc, local);
                return local.VariableType;
            }

            var paramOffset = ctx.Method.IsStatic ? 0 : 1;
            for (int i = 0; i < ctx.ParamNames.Count; i++)
            {
                if (ctx.ParamNames[i] == ident.Name)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Starg, i + paramOffset);
                    return ctx.Method.Parameters[i].ParameterType;
                }
            }

            if (ctx.ContainingType != null)
            {
                var field = ctx.ContainingType.Fields.FirstOrDefault(f => f.Name == ident.Name);
                if (field != null)
                {
                    var tmp = new VariableDefinition(field.FieldType);
                    ctx.Method.Body.Variables.Add(tmp);
                    il.Emit(OpCodes.Stloc, tmp);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, tmp);
                    il.Emit(OpCodes.Stfld, field);
                    il.Emit(OpCodes.Ldloc, tmp);
                    return field.FieldType;
                }
            }
        }
        else if (assign.Target is MemberAccessExpression memberAccess)
        {
            // obj.field = value → emit obj, emit value, stfld
            var targetType = EmitExpression(memberAccess.Target, ctx) as TypeDefinition;
            EmitExpression(assign.Value, ctx);

            // Try the emitted type, then look up identifier name in type defs
            targetType ??= memberAccess.Target is IdentifierExpression id
                && _typeDefs.TryGetValue(id.Name, out var td) ? td : null;

            // Try resolving parameter type → look up in _typeDefs
            if (targetType == null && memberAccess.Target is IdentifierExpression id2)
            {
                var paramOffset = ctx.Method.IsStatic ? 0 : 1;
                for (int i = 0; i < ctx.ParamNames.Count; i++)
                {
                    if (ctx.ParamNames[i] == id2.Name)
                    {
                        var paramTypeName = ctx.Method.Parameters[i].ParameterType.Name;
                        _typeDefs.TryGetValue(paramTypeName, out targetType);
                        break;
                    }
                }
            }

            if (targetType != null)
            {
                var field = targetType.Fields.FirstOrDefault(f => f.Name == memberAccess.MemberName);
                if (field != null)
                {
                    il.Emit(OpCodes.Stfld, field);
                    return field.FieldType;
                }
            }

            // Unknown member — pop both target and value
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Pop);
            return _module.TypeSystem.Object;
        }
        else
        {
            EmitExpression(assign.Value, ctx);
        }

        return _module.TypeSystem.Object;
    }

    private TypeReference EmitInterpolatedString(InterpolatedStringExpression interp, BodyContext ctx)
    {
        var il = ctx.IL;
        // Build the string by concatenating parts via String.Concat
        if (interp.Parts.Count == 0)
        {
            il.Emit(OpCodes.Ldstr, "");
            return _module.TypeSystem.String;
        }

        // Emit each part as a string on the stack, then concat
        var concatArgs = new List<TypeReference>();
        foreach (var part in interp.Parts)
        {
            if (part is InterpolatedStringText text)
            {
                il.Emit(OpCodes.Ldstr, text.Text);
                concatArgs.Add(_module.TypeSystem.String);
            }
            else if (part is InterpolatedStringInsertion ins)
            {
                var t = EmitExpression(ins.Expression, ctx);
                if (t != null && t != _module.TypeSystem.String)
                {
                    // Box value types before calling ToString
                    if (t.IsValueType)
                        il.Emit(OpCodes.Box, t);
                    var toStringMethod = new MethodReference("ToString", _module.TypeSystem.String, _module.TypeSystem.Object)
                    {
                        HasThis = true
                    };
                    il.Emit(OpCodes.Callvirt, toStringMethod);
                }
                concatArgs.Add(_module.TypeSystem.String);
            }
        }

        // Chain pairwise String.Concat(string, string) calls
        for (int i = 1; i < concatArgs.Count; i++)
        {
            var concatMethod = new MethodReference("Concat", _module.TypeSystem.String,
                new TypeReference("System", "String", _module, _module.TypeSystem.CoreLibrary))
            {
                HasThis = false
            };
            concatMethod.Parameters.Add(new ParameterDefinition(_module.TypeSystem.String));
            concatMethod.Parameters.Add(new ParameterDefinition(_module.TypeSystem.String));
            il.Emit(OpCodes.Call, concatMethod);
        }

        return _module.TypeSystem.String;
    }

    private TypeReference? EmitIndex(IndexExpression idx, BodyContext ctx)
    {
        var il = ctx.IL;
        EmitExpression(idx.Target, ctx);
        EmitExpression(idx.Index, ctx);
        // For arrays: ldelem.ref / for lists: call List.get_Item
        il.Emit(OpCodes.Ldelem_Ref);
        return _module.TypeSystem.Object;
    }

    // ──────────────────────────────────────────────
    // Type resolution
    // ──────────────────────────────────────────────

    private static readonly Dictionary<string, string> DotNetTypeNames = new()
    {
        ["Stream"] = "System.IO.Stream",
        ["StreamReader"] = "System.IO.StreamReader",
        ["StreamWriter"] = "System.IO.StreamWriter",
        ["Console"] = "System.Console",
        ["File"] = "System.IO.File",
        ["IDisposable"] = "System.IDisposable",
        ["List"] = "System.Collections.Generic.List`1",
        ["Exception"] = "System.Exception",
        ["string"] = "System.String",
        ["object"] = "System.Object",
        ["int"] = "System.Int32",
        ["long"] = "System.Int64",
        ["float"] = "System.Single",
        ["double"] = "System.Double",
        ["bool"] = "System.Boolean",
        ["char"] = "System.Char",
        ["void"] = "System.Void",
    };

    private TypeReference? ResolveTypeSyntax(TypeSyntax? typeSyntax)
    {
        if (typeSyntax == null) return null;
        return ResolveTypeName(typeSyntax.Name);
    }

    private TypeReference? ResolveTypeName(string name) => name switch
    {
        "void" => _module.TypeSystem.Void,
        "int" => _module.TypeSystem.Int32,
        "long" => _module.TypeSystem.Int64,
        "float" => _module.TypeSystem.Single,
        "double" => _module.TypeSystem.Double,
        "bool" => _module.TypeSystem.Boolean,
        "string" => _module.TypeSystem.String,
        "char" => _module.TypeSystem.Char,
        "object" => _module.TypeSystem.Object,
        _ when _typeDefs.TryGetValue(name, out var td) => td,
        _ when DotNetTypeNames.TryGetValue(name, out var fullName) => ImportDotNetType(fullName),
        _ => _module.TypeSystem.Object,
    };

    private TypeReference ImportDotNetType(string fullName)
    {
        var type = Type.GetType(fullName)
            ?? Type.GetType(fullName + ", System.Runtime")
            ?? Type.GetType(fullName + ", mscorlib");
        if (type != null)
            return _module.ImportReference(type);

        // Create a TypeReference without an actual assembly resolution
        var lastDot = fullName.LastIndexOf('.');
        var ns = lastDot >= 0 ? fullName[..lastDot] : "";
        var typeName = lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
        return new TypeReference(ns, typeName, _module, _module.TypeSystem.CoreLibrary);
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static MethodAttributes BuildMethodAttributes(
        AccessModifier access, bool isStatic, bool isAbstract, bool isVirtual, bool isOverride)
    {
        var attrs = MethodAttributes.HideBySig;
        if (access == AccessModifier.Public) attrs |= MethodAttributes.Public;
        else if (access == AccessModifier.Private) attrs |= MethodAttributes.Private;
        else attrs |= MethodAttributes.Assembly; // internal
        if (isStatic) attrs |= MethodAttributes.Static;
        if (isAbstract) attrs |= MethodAttributes.Abstract | MethodAttributes.Virtual;
        if (isVirtual) attrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
        if (isOverride) attrs |= MethodAttributes.Virtual;
        return attrs;
    }

    private void AddDefaultConstructor(TypeDefinition typeDef)
    {
        if (typeDef.Methods.Any(m => m.Name == ".ctor" && m.Parameters.Count == 0))
            return;

        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            _module.TypeSystem.Void);
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, DefaultObjectCtor());
        il.Emit(OpCodes.Ret);
        typeDef.Methods.Add(ctor);
    }

    private MethodReference DefaultObjectCtor()
    {
        var ctor = new MethodReference(".ctor", _module.TypeSystem.Void, _module.TypeSystem.Object)
        {
            HasThis = true
        };
        return ctor;
    }

    private MethodReference ImportDispose()
    {
        var disposable = new TypeReference("System", "IDisposable", _module, _module.TypeSystem.CoreLibrary);
        return new MethodReference("Dispose", _module.TypeSystem.Void, disposable)
        {
            HasThis = true
        };
    }

    private MethodReference ImportGetEnumerator()
    {
        var enumerable = new TypeReference("System.Collections", "IEnumerable", _module, _module.TypeSystem.CoreLibrary);
        var enumerator = new TypeReference("System.Collections", "IEnumerator", _module, _module.TypeSystem.CoreLibrary);
        return new MethodReference("GetEnumerator", enumerator, enumerable)
        {
            HasThis = true
        };
    }

    private MethodReference ImportMoveNext()
    {
        var enumerator = new TypeReference("System.Collections", "IEnumerator", _module, _module.TypeSystem.CoreLibrary);
        return new MethodReference("MoveNext", _module.TypeSystem.Boolean, enumerator)
        {
            HasThis = true
        };
    }

    private MethodReference ImportGetCurrent()
    {
        var enumerator = new TypeReference("System.Collections", "IEnumerator", _module, _module.TypeSystem.CoreLibrary);
        return new MethodReference("get_Current", _module.TypeSystem.Object, enumerator)
        {
            HasThis = true
        };
    }

    private MethodReference ImportInvalidOperationExceptionCtor()
    {
        var exType = new TypeReference("System", "InvalidOperationException", _module, _module.TypeSystem.CoreLibrary);
        var ctor = new MethodReference(".ctor", _module.TypeSystem.Void, exType)
        {
            HasThis = true
        };
        ctor.Parameters.Add(new ParameterDefinition(_module.TypeSystem.String));
        return ctor;
    }

    private static void EmitNotImplementedBody(MethodDefinition method)
    {
        var il = method.Body.GetILProcessor();
        if (method.ReturnType.FullName == "System.Void")
        {
            il.Emit(OpCodes.Ret);
        }
        else if (method.ReturnType.IsValueType)
        {
            // Push default value for value types
            EmitDefaultValue(il, method.ReturnType);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
        }
    }

    private static void EmitDefaultValue(ILProcessor il, TypeReference type)
    {
        switch (type.FullName)
        {
            case "System.Int32":
            case "System.Boolean":
            case "System.Char":
                il.Emit(OpCodes.Ldc_I4_0);
                break;
            case "System.Int64":
                il.Emit(OpCodes.Ldc_I8, 0L);
                break;
            case "System.Single":
                il.Emit(OpCodes.Ldc_R4, 0f);
                break;
            case "System.Double":
                il.Emit(OpCodes.Ldc_R8, 0.0);
                break;
            default:
                // Generic value type — use initobj via a local
                il.Emit(OpCodes.Ldc_I4_0);
                break;
        }
    }

    private static bool EndsWithRet(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        return instructions.Count > 0 && instructions[^1].OpCode == OpCodes.Ret;
    }
}

// ──────────────────────────────────────────────
// Body emission context and pending work
// ──────────────────────────────────────────────

internal sealed class BodyContext
{
    public MethodDefinition Method { get; }
    public TypeDefinition? ContainingType { get; }
    public IReadOnlyList<string> ParamNames { get; }
    public ILProcessor IL { get; }
    public Dictionary<string, VariableDefinition> Locals { get; } = new();
    public Stack<(Instruction BreakTarget, Instruction ContinueTarget)> LoopLabels { get; } = new();
    public ModuleDefinition Module { get; }

    public BodyContext(MethodDefinition method, TypeDefinition? containingType,
        IReadOnlyList<string> paramNames, ILProcessor il, ModuleDefinition module)
    {
        Method = method;
        ContainingType = containingType;
        ParamNames = paramNames;
        IL = il;
        Module = module;
    }
}

internal sealed class PendingBody
{
    public SyntaxNode Node { get; }
    public MethodDefinition Method { get; }
    public TypeDefinition? ContainingType { get; }
    public IReadOnlyList<string> ParamNames { get; }
    public bool IsConstructor { get; }

    public PendingBody(SyntaxNode node, MethodDefinition method, TypeDefinition? containingType,
        IReadOnlyList<string> paramNames, bool isConstructor = false)
    {
        Node = node;
        Method = method;
        ContainingType = containingType;
        ParamNames = paramNames;
        IsConstructor = isConstructor;
    }
}
