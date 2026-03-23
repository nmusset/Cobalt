using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Cobalt.Analyzers;

/// <summary>
/// Detects when [NotSync] values are captured by lambdas passed to concurrency APIs
/// (Task.Run, Parallel.ForEach, Parallel.For, ThreadPool.QueueUserWorkItem, new Thread).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThreadSafetyAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.NotSyncCapturedByConcurrentLambda);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var notSyncAttr = context.Compilation.GetTypeByMetadataName(AttributeNames.NotSync);
        if (notSyncAttr is null) return;

        var concurrencyMethods = ResolveConcurrencyMethods(context.Compilation);
        if (concurrencyMethods.IsEmpty) return;

        context.RegisterOperationAction(
            ctx => AnalyzeInvocation(ctx, notSyncAttr, concurrencyMethods),
            OperationKind.Invocation);

        context.RegisterOperationAction(
            ctx => AnalyzeObjectCreation(ctx, notSyncAttr, concurrencyMethods),
            OperationKind.ObjectCreation);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol notSyncAttr,
        ImmutableHashSet<IMethodSymbol> concurrencyMethods)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (!IsConcurrencyCall(invocation.TargetMethod, concurrencyMethods)) return;

        // Find lambda/delegate arguments and check their captured variables.
        foreach (var argument in invocation.Arguments)
        {
            CheckLambdaCaptures(context, argument.Value, invocation.TargetMethod, notSyncAttr);
        }
    }

    private static void AnalyzeObjectCreation(
        OperationAnalysisContext context,
        INamedTypeSymbol notSyncAttr,
        ImmutableHashSet<IMethodSymbol> concurrencyMethods)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Constructor is null) return;

        if (!IsConcurrencyCall(creation.Constructor, concurrencyMethods)) return;

        foreach (var argument in creation.Arguments)
        {
            CheckLambdaCaptures(context, argument.Value, creation.Constructor, notSyncAttr);
        }
    }

    private static void CheckLambdaCaptures(
        OperationAnalysisContext context,
        IOperation argumentValue,
        IMethodSymbol targetMethod,
        INamedTypeSymbol notSyncAttr)
    {
        // Unwrap conversions (lambda → delegate conversion).
        var operation = argumentValue;
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        while (operation is IDelegateCreationOperation delegateCreation)
            operation = delegateCreation.Target;

        if (operation is not IAnonymousFunctionOperation lambda) return;

        var methodDisplayName = $"{targetMethod.ContainingType.Name}.{targetMethod.Name}";

        // Walk the lambda body for references to captured variables whose type is [NotSync].
        CheckOperationForNotSyncCaptures(context, lambda.Body, notSyncAttr, methodDisplayName);
    }

    private static void CheckOperationForNotSyncCaptures(
        OperationAnalysisContext context,
        IOperation operation,
        INamedTypeSymbol notSyncAttr,
        string methodDisplayName)
    {
        // Track already-reported symbols to avoid duplicates within the same lambda.
        var reported = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var descendant in operation.DescendantsAndSelf())
        {
            ISymbol? symbol = null;
            ITypeSymbol? type = null;

            switch (descendant)
            {
                case ILocalReferenceOperation localRef when !localRef.IsDeclaration:
                    symbol = localRef.Local;
                    type = localRef.Local.Type;
                    break;
                case IParameterReferenceOperation paramRef:
                    symbol = paramRef.Parameter;
                    type = paramRef.Parameter.Type;
                    break;
                case IFieldReferenceOperation fieldRef when fieldRef.Instance is IInstanceReferenceOperation:
                    // 'this.field' — captured instance field.
                    symbol = fieldRef.Field;
                    type = fieldRef.Field.Type;
                    break;
            }

            if (symbol is null || type is null) continue;
            if (!type.HasAttribute(notSyncAttr)) continue;
            if (!reported.Add(symbol)) continue;

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NotSyncCapturedByConcurrentLambda,
                descendant.Syntax.GetLocation(),
                symbol.Name,
                type.Name,
                methodDisplayName));
        }
    }

    private static bool IsConcurrencyCall(
        IMethodSymbol method,
        ImmutableHashSet<IMethodSymbol> concurrencyMethods)
    {
        // Check original definition (before generic substitution).
        var original = method.OriginalDefinition;
        return concurrencyMethods.Contains(original);
    }

    /// <summary>
    /// Resolves well-known concurrency API methods from the compilation.
    /// </summary>
    private static ImmutableHashSet<IMethodSymbol> ResolveConcurrencyMethods(Compilation compilation)
    {
        var builder = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);

        // Task.Run overloads
        AddMethodOverloads(compilation, "System.Threading.Tasks.Task", "Run", builder);

        // Parallel.ForEach / Parallel.For
        AddMethodOverloads(compilation, "System.Threading.Tasks.Parallel", "ForEach", builder);
        AddMethodOverloads(compilation, "System.Threading.Tasks.Parallel", "For", builder);
        AddMethodOverloads(compilation, "System.Threading.Tasks.Parallel", "Invoke", builder);

        // ThreadPool.QueueUserWorkItem
        AddMethodOverloads(compilation, "System.Threading.ThreadPool", "QueueUserWorkItem", builder);
        AddMethodOverloads(compilation, "System.Threading.ThreadPool", "UnsafeQueueUserWorkItem", builder);

        // new Thread(...)
        var threadType = compilation.GetTypeByMetadataName("System.Threading.Thread");
        if (threadType is not null)
        {
            foreach (var ctor in threadType.Constructors)
            {
                builder.Add(ctor);
            }
        }

        return builder.ToImmutable();
    }

    private static void AddMethodOverloads(
        Compilation compilation,
        string typeName,
        string methodName,
        ImmutableHashSet<IMethodSymbol>.Builder builder)
    {
        var type = compilation.GetTypeByMetadataName(typeName);
        if (type is null) return;

        foreach (var member in type.GetMembers(methodName))
        {
            if (member is IMethodSymbol method)
                builder.Add(method);
        }
    }
}
