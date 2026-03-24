namespace Cobalt.Compiler.Dump;

using System.Text;
using Cobalt.Compiler.Syntax;

/// <summary>
/// Produces an indented text representation of the Cobalt AST.
/// Used for debugging and --dump-ast output.
/// </summary>
public class AstPrinter
{
    private readonly StringBuilder _sb = new();
    private int _depth;

    public string Print(CompilationUnit unit)
    {
        _sb.Clear();
        _depth = 0;
        PrintCompilationUnit(unit);
        return _sb.ToString();
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private void Line(string text) =>
        _sb.AppendLine(new string(' ', _depth * 2) + text);

    private void WithIndent(Action body)
    {
        _depth++;
        body();
        _depth--;
    }

    private static string TypeStr(TypeSyntax t)
    {
        var own = t.Ownership != OwnershipModifier.None ? OwnerStr(t.Ownership) + " " : "";
        var args = t.TypeArguments.Count > 0
            ? $"<{string.Join(", ", t.TypeArguments.Select(TypeStr))}>"
            : "";
        return $"{own}{t.Name}{args}";
    }

    private static string OwnerStr(OwnershipModifier m) => m switch
    {
        OwnershipModifier.Own => "own",
        OwnershipModifier.Ref => "ref",
        OwnershipModifier.RefMut => "ref mut",
        _ => "",
    };

    private static string AccessStr(AccessModifier a) => a switch
    {
        AccessModifier.Public => "public ",
        AccessModifier.Private => "private ",
        AccessModifier.Protected => "protected ",
        AccessModifier.Internal => "internal ",
        _ => "",
    };

    // ──────────────────────────────────────────────
    // Top-level
    // ──────────────────────────────────────────────

    private void PrintCompilationUnit(CompilationUnit unit)
    {
        Line("CompilationUnit");
        WithIndent(() =>
        {
            if (unit.Namespace != null)
                Line($"namespace {unit.Namespace.Name}");
            foreach (var use in unit.Uses)
                Line($"use {use.Name}");
            foreach (var member in unit.Members)
                PrintMember(member);
        });
    }

    // ──────────────────────────────────────────────
    // Declarations
    // ──────────────────────────────────────────────

    private void PrintMember(SyntaxNode member)
    {
        switch (member)
        {
            case ClassDeclaration cls: PrintClass(cls); break;
            case TraitDeclaration trait: PrintTrait(trait); break;
            case UnionDeclaration union: PrintUnion(union); break;
            case ImplBlock impl: PrintImpl(impl); break;
            case MethodDeclaration method: PrintMethod(method); break;
            case FieldDeclaration field: PrintField(field); break;
            case PropertyDeclaration prop: PrintProperty(prop); break;
            case ConstructorDeclaration ctor: PrintConstructor(ctor); break;
            default: Line($"<unknown member: {member.GetType().Name}>"); break;
        }
    }

    private void PrintClass(ClassDeclaration cls)
    {
        var mods = AccessStr(cls.Access)
            + (cls.IsAbstract ? "abstract " : "")
            + (cls.IsSealed ? "sealed " : "");
        var bases = cls.BaseTypes.Count > 0
            ? $" : {string.Join(", ", cls.BaseTypes.Select(TypeStr))}"
            : "";
        Line($"class {mods}{cls.Name}{bases}");
        WithIndent(() =>
        {
            foreach (var m in cls.Members)
                PrintMember(m);
        });
    }

    private void PrintTrait(TraitDeclaration trait)
    {
        Line($"trait {trait.Name}");
        WithIndent(() =>
        {
            foreach (var m in trait.Members)
                PrintMember(m);
        });
    }

    private void PrintUnion(UnionDeclaration union)
    {
        Line($"{AccessStr(union.Access)}union {union.Name}");
        WithIndent(() =>
        {
            foreach (var v in union.Variants)
            {
                var fields = v.Fields.Count > 0
                    ? $"({string.Join(", ", v.Fields.Select(f => $"{TypeStr(f.Type)} {f.Name}"))})"
                    : "()";
                Line($"variant {v.Name}{fields}");
            }
        });
    }

    private void PrintImpl(ImplBlock impl)
    {
        Line($"impl {impl.TraitName} for {impl.TargetTypeName}");
        WithIndent(() =>
        {
            foreach (var m in impl.Members)
                PrintMember(m);
        });
    }

    private void PrintMethod(MethodDeclaration method)
    {
        var mods = AccessStr(method.Access)
            + (method.IsStatic ? "static " : "")
            + (method.IsAbstract ? "abstract " : "")
            + (method.IsVirtual ? "virtual " : "")
            + (method.IsOverride ? "override " : "");
        var returnOwn = method.ReturnOwnership != OwnershipModifier.None
            ? OwnerStr(method.ReturnOwnership) + " "
            : "";
        var @params = string.Join(", ", method.Parameters.Select(p =>
        {
            var own = p.Ownership != OwnershipModifier.None ? OwnerStr(p.Ownership) + " " : "";
            return $"{TypeStr(p.Type)} {own}{p.Name}";
        }));
        Line($"method {mods}{returnOwn}{TypeStr(method.ReturnType)} {method.Name}({@params})");
        if (method.Body != null)
            WithIndent(() => PrintBlock(method.Body));
    }

    private void PrintField(FieldDeclaration field)
    {
        var own = field.Ownership != OwnershipModifier.None ? OwnerStr(field.Ownership) + " " : "";
        Line($"field {AccessStr(field.Access)}{own}{TypeStr(field.Type)} {field.Name}");
    }

    private void PrintProperty(PropertyDeclaration prop)
    {
        var accessors = (prop.HasGetter ? "get; " : "") + (prop.HasSetter ? "set; " : "");
        Line($"property {AccessStr(prop.Access)}{TypeStr(prop.Type)} {prop.Name} {{ {accessors}}}");
        if (prop.ExpressionBody != null)
            WithIndent(() => PrintExpression(prop.ExpressionBody));
    }

    private void PrintConstructor(ConstructorDeclaration ctor)
    {
        var @params = string.Join(", ", ctor.Parameters.Select(p =>
        {
            var own = p.Ownership != OwnershipModifier.None ? OwnerStr(p.Ownership) + " " : "";
            return $"{TypeStr(p.Type)} {own}{p.Name}";
        }));
        Line($"constructor {AccessStr(ctor.Access)}({@params})");
        if (ctor.Body != null)
            WithIndent(() => PrintBlock(ctor.Body));
    }

    // ──────────────────────────────────────────────
    // Statements
    // ──────────────────────────────────────────────

    private void PrintBlock(BlockStatement block)
    {
        Line("block");
        WithIndent(() =>
        {
            foreach (var stmt in block.Statements)
                PrintStatement(stmt);
        });
    }

    private void PrintStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                PrintBlock(block);
                break;
            case VariableDeclaration varDecl:
                var typeAnnotation = varDecl.Type != null ? $": {TypeStr(varDecl.Type)}" : "";
                Line($"var {varDecl.Name}{typeAnnotation}");
                if (varDecl.Initializer != null)
                    WithIndent(() => PrintExpression(varDecl.Initializer));
                break;
            case UsingVarDeclaration usingDecl:
                var usingType = usingDecl.Type != null ? $": {TypeStr(usingDecl.Type)}" : "";
                Line($"using var {usingDecl.Name}{usingType}");
                if (usingDecl.Initializer != null)
                    WithIndent(() => PrintExpression(usingDecl.Initializer));
                break;
            case ReturnStatement ret:
                Line("return");
                if (ret.Expression != null)
                    WithIndent(() => PrintExpression(ret.Expression));
                break;
            case IfStatement ifStmt:
                Line("if");
                WithIndent(() => PrintExpression(ifStmt.Condition));
                Line("then");
                WithIndent(() => PrintStatement(ifStmt.ThenBody));
                if (ifStmt.ElseBody != null)
                {
                    Line("else");
                    WithIndent(() => PrintStatement(ifStmt.ElseBody));
                }
                break;
            case WhileStatement whileStmt:
                Line("while");
                WithIndent(() => PrintExpression(whileStmt.Condition));
                Line("body");
                WithIndent(() => PrintStatement(whileStmt.Body));
                break;
            case ForStatement forStmt:
                Line("for");
                WithIndent(() =>
                {
                    if (forStmt.Initializer != null) PrintStatement(forStmt.Initializer);
                    if (forStmt.Condition != null) PrintExpression(forStmt.Condition);
                    if (forStmt.Increment != null) PrintExpression(forStmt.Increment);
                });
                Line("body");
                WithIndent(() => PrintStatement(forStmt.Body));
                break;
            case ForEachStatement forEach:
                var forOwn = forEach.Ownership != OwnershipModifier.None
                    ? OwnerStr(forEach.Ownership) + " "
                    : "";
                Line($"foreach {forOwn}{forEach.VariableName} in");
                WithIndent(() => PrintExpression(forEach.Iterable));
                Line("body");
                WithIndent(() => PrintStatement(forEach.Body));
                break;
            case MatchStatement matchStmt:
                Line("match");
                WithIndent(() => PrintExpression(matchStmt.Subject));
                foreach (var arm in matchStmt.Arms)
                {
                    Line($"arm {PrintPattern(arm.Pattern)} =>");
                    WithIndent(() =>
                    {
                        if (arm.Body is StatementNode s) PrintStatement(s);
                        else if (arm.Body is ExpressionNode e) PrintExpression(e);
                    });
                }
                break;
            case ExpressionStatement exprStmt:
                PrintExpression(exprStmt.Expression);
                break;
            case BreakStatement:
                Line("break");
                break;
            case ContinueStatement:
                Line("continue");
                break;
            default:
                Line($"<unknown stmt: {stmt.GetType().Name}>");
                break;
        }
    }

    // ──────────────────────────────────────────────
    // Expressions
    // ──────────────────────────────────────────────

    private void PrintExpression(ExpressionNode expr)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                Line($"literal {lit.Kind} {lit.Value}");
                break;
            case IdentifierExpression ident:
                Line($"identifier {ident.Name}");
                break;
            case ThisExpression:
                Line("this");
                break;
            case BinaryExpression bin:
                Line($"binary {bin.Operator}");
                WithIndent(() =>
                {
                    PrintExpression(bin.Left);
                    PrintExpression(bin.Right);
                });
                break;
            case UnaryExpression unary:
                Line($"unary {unary.Operator} prefix={unary.IsPrefix}");
                WithIndent(() => PrintExpression(unary.Operand));
                break;
            case OwnExpression own:
                Line("own");
                WithIndent(() => PrintExpression(own.Inner));
                break;
            case RefMutExpression refMut:
                Line("ref mut");
                WithIndent(() => PrintExpression(refMut.Inner));
                break;
            case MemberAccessExpression memberAccess:
                Line($"member-access .{memberAccess.MemberName}");
                WithIndent(() => PrintExpression(memberAccess.Target));
                break;
            case InvocationExpression invocation:
                Line("invocation");
                WithIndent(() =>
                {
                    Line("target:");
                    WithIndent(() => PrintExpression(invocation.Target));
                    if (invocation.Arguments.Count > 0)
                    {
                        Line("args:");
                        WithIndent(() =>
                        {
                            foreach (var arg in invocation.Arguments)
                            {
                                var own = arg.Ownership != OwnershipModifier.None
                                    ? OwnerStr(arg.Ownership) + " "
                                    : "";
                                Line($"arg {own}");
                                WithIndent(() => PrintExpression(arg.Expression));
                            }
                        });
                    }
                });
                break;
            case ObjectCreationExpression objCreate:
                Line($"new {TypeStr(objCreate.Type)}");
                WithIndent(() =>
                {
                    foreach (var arg in objCreate.Arguments)
                    {
                        var own = arg.Ownership != OwnershipModifier.None ? OwnerStr(arg.Ownership) + " " : "";
                        Line($"arg {own}");
                        WithIndent(() => PrintExpression(arg.Expression));
                    }
                    if (objCreate.InitializerClauses != null)
                    {
                        foreach (var clause in objCreate.InitializerClauses)
                        {
                            var own = clause.Ownership != OwnershipModifier.None ? OwnerStr(clause.Ownership) + " " : "";
                            Line($"init {clause.FieldName} = {own}");
                            WithIndent(() => PrintExpression(clause.Value));
                        }
                    }
                });
                break;
            case AssignmentExpression assign:
                Line("assign");
                WithIndent(() =>
                {
                    PrintExpression(assign.Target);
                    PrintExpression(assign.Value);
                });
                break;
            case IsPatternExpression isPat:
                Line($"is {PrintPattern(isPat.Pattern)}");
                WithIndent(() => PrintExpression(isPat.Expression));
                break;
            case SwitchExpression switchExpr:
                Line("switch");
                WithIndent(() => PrintExpression(switchExpr.Subject));
                foreach (var arm in switchExpr.Arms)
                {
                    Line($"arm {PrintPattern(arm.Pattern)} =>");
                    WithIndent(() => PrintExpression(arm.Expression));
                }
                break;
            case InterpolatedStringExpression interp:
                Line("interpolated-string");
                WithIndent(() =>
                {
                    foreach (var part in interp.Parts)
                    {
                        if (part is InterpolatedStringText text)
                            Line($"text \"{text.Text}\"");
                        else if (part is InterpolatedStringInsertion ins)
                        {
                            Line("insert");
                            WithIndent(() => PrintExpression(ins.Expression));
                        }
                    }
                });
                break;
            case IndexExpression idx:
                Line("index");
                WithIndent(() =>
                {
                    PrintExpression(idx.Target);
                    PrintExpression(idx.Index);
                });
                break;
            case CastExpression cast:
                Line($"cast ({TypeStr(cast.Type)})");
                WithIndent(() => PrintExpression(cast.Expression));
                break;
            default:
                Line($"<unknown expr: {expr.GetType().Name}>");
                break;
        }
    }

    // ──────────────────────────────────────────────
    // Patterns
    // ──────────────────────────────────────────────

    private static string PrintPattern(PatternNode pattern) => pattern switch
    {
        VariantPattern vp when vp.SubPatterns.Count > 0
            => $"{vp.VariantName}({string.Join(", ", vp.SubPatterns.Select(PrintPattern))})",
        VariantPattern vp => vp.VariantName,
        VarPattern vp => $"var {vp.VariableName}",
        DiscardPattern => "_",
        TypePattern tp when tp.VariableName != null => $"{tp.TypeName} {tp.VariableName}",
        TypePattern tp => tp.TypeName,
        _ => $"<{pattern.GetType().Name}>",
    };
}
