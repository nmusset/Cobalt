using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Cobalt.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OwnershipAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            DiagnosticDescriptors.OwnedNotDisposed,
            DiagnosticDescriptors.UseAfterMove,
            DiagnosticDescriptors.UseAfterDispose);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;

        var knownTypes = KnownTypes.Resolve(compilation);
        if (knownTypes is null) return; // Annotations not referenced — nothing to analyze.

        context.RegisterOperationBlockStartAction(blockStart => OnOperationBlockStart(blockStart, knownTypes));
    }

    private static void OnOperationBlockStart(OperationBlockStartAnalysisContext context, KnownTypes known)
    {
        if (context.OwningSymbol is not IMethodSymbol method) return;

        var tracker = new OwnershipTracker(method, known);

        context.RegisterOperationAction(tracker.AnalyzeVariableDeclarator, OperationKind.VariableDeclarator);
        context.RegisterOperationAction(tracker.AnalyzeSimpleAssignment, OperationKind.SimpleAssignment);
        context.RegisterOperationAction(tracker.AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(tracker.AnalyzeObjectCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(tracker.AnalyzeLocalReference, OperationKind.LocalReference);
        context.RegisterOperationAction(tracker.AnalyzeParameterReference, OperationKind.ParameterReference);
        context.RegisterOperationAction(tracker.AnalyzeReturn, OperationKind.Return);
        context.RegisterOperationAction(tracker.AnalyzeUsing, OperationKind.Using);
        context.RegisterOperationAction(tracker.AnalyzeUsingDeclaration, OperationKind.UsingDeclaration);

        context.RegisterOperationBlockEndAction(tracker.OnBlockEnd);
    }

    /// <summary>
    /// Resolved attribute and interface types needed for analysis.
    /// Returns null if the Cobalt.Annotations assembly is not referenced.
    /// </summary>
    private sealed class KnownTypes
    {
        public INamedTypeSymbol Owned { get; }
        public INamedTypeSymbol MustDispose { get; }
        public INamedTypeSymbol? IDisposable { get; }

        private KnownTypes(INamedTypeSymbol owned, INamedTypeSymbol mustDispose, INamedTypeSymbol? idisposable)
        {
            Owned = owned;
            MustDispose = mustDispose;
            IDisposable = idisposable;
        }

        public static KnownTypes? Resolve(Compilation compilation)
        {
            var owned = compilation.GetTypeByMetadataName(AttributeNames.Owned);
            var mustDispose = compilation.GetTypeByMetadataName(AttributeNames.MustDispose);

            // At least [Owned] or [MustDispose] must be available.
            if (owned is null && mustDispose is null) return null;

            var idisposable = compilation.GetTypeByMetadataName("System.IDisposable");

            return new KnownTypes(
                owned!,       // At least one is non-null.
                mustDispose!, // Both can be used in the tracker with null checks.
                idisposable);
        }
    }

    private enum VariableState
    {
        Active,
        Disposed,
        Transferred,
        InUsing
    }

    /// <summary>
    /// Tracks ownership state of variables within a single method body.
    /// </summary>
    private sealed class OwnershipTracker
    {
        private readonly IMethodSymbol _method;
        private readonly KnownTypes _known;

        // Tracked variables and their current state.
        private readonly Dictionary<ISymbol, VariableState> _states =
            new(SymbolEqualityComparer.Default);

        // Location where state changed (for diagnostic reporting).
        private readonly Dictionary<ISymbol, Location> _stateChangeLocations =
            new(SymbolEqualityComparer.Default);

        // Declaration locations for tracked variables.
        private readonly Dictionary<ISymbol, Location> _declarationLocations =
            new(SymbolEqualityComparer.Default);

        public OwnershipTracker(IMethodSymbol method, KnownTypes known)
        {
            _method = method;
            _known = known;

            // Track [Owned] parameters that implement IDisposable.
            foreach (var param in method.Parameters)
            {
                if (IsOwnedDisposable(param))
                {
                    _states[param] = VariableState.Active;
                    _declarationLocations[param] = param.Locations.FirstOrDefault() ?? Location.None;
                }
            }
        }

        public void AnalyzeVariableDeclarator(OperationAnalysisContext context)
        {
            var declarator = (IVariableDeclaratorOperation)context.Operation;
            var local = declarator.Symbol;
            var initializer = declarator.Initializer?.Value ?? GetParentDeclarationInitializer(declarator);

            if (initializer is null) return;

            if (IsOwnedValue(initializer, local.Type))
            {
                // Check if this declarator is inside a using statement or using declaration.
                var state = IsInsideUsing(declarator) ? VariableState.InUsing : VariableState.Active;
                _states[local] = state;
                _declarationLocations[local] = local.Locations.FirstOrDefault() ?? declarator.Syntax.GetLocation();
            }
        }

        public void AnalyzeSimpleAssignment(OperationAnalysisContext context)
        {
            var assignment = (ISimpleAssignmentOperation)context.Operation;

            if (assignment.Target is ILocalReferenceOperation localRef)
            {
                if (IsOwnedValue(assignment.Value, localRef.Local.Type))
                {
                    _states[localRef.Local] = VariableState.Active;
                    _declarationLocations[localRef.Local] = assignment.Syntax.GetLocation();
                }
            }

            // Assignment to a field (including [Owned] fields) transfers ownership
            // from the source variable.
            if (assignment.Target is IFieldReferenceOperation)
            {
                if (GetReferencedSymbol(assignment.Value) is { } source && _states.ContainsKey(source))
                {
                    _states[source] = VariableState.Transferred;
                    _stateChangeLocations[source] = assignment.Syntax.GetLocation();
                }
            }
        }

        public void AnalyzeInvocation(OperationAnalysisContext context)
        {
            var invocation = (IInvocationOperation)context.Operation;

            // Check for .Dispose() calls.
            if (IsDisposeCall(invocation))
            {
                if (GetReferencedSymbol(invocation.Instance) is { } disposed && _states.ContainsKey(disposed))
                {
                    _states[disposed] = VariableState.Disposed;
                    _stateChangeLocations[disposed] = invocation.Syntax.GetLocation();
                }
                return;
            }

            // Check for ownership transfer via [Owned] parameters.
            // Any variable passed to an [Owned] parameter is considered moved,
            // even if it wasn't previously tracked (enables use-after-move detection).
            foreach (var argument in invocation.Arguments)
            {
                var param = argument.Parameter;
                if (param is null) continue;

                if (param.HasAttribute(_known.Owned))
                {
                    if (GetReferencedSymbol(argument.Value) is { } transferred)
                    {
                        _states[transferred] = VariableState.Transferred;
                        _stateChangeLocations[transferred] = argument.Syntax.GetLocation();
                    }
                }
            }
        }

        public void AnalyzeObjectCreation(OperationAnalysisContext context)
        {
            var creation = (IObjectCreationOperation)context.Operation;

            // Check for ownership transfer via [Owned] constructor parameters.
            foreach (var argument in creation.Arguments)
            {
                var param = argument.Parameter;
                if (param is null) continue;

                if (param.HasAttribute(_known.Owned))
                {
                    if (GetReferencedSymbol(argument.Value) is { } transferred)
                    {
                        _states[transferred] = VariableState.Transferred;
                        _stateChangeLocations[transferred] = argument.Syntax.GetLocation();
                    }
                }
            }
        }

        public void AnalyzeLocalReference(OperationAnalysisContext context)
        {
            var localRef = (ILocalReferenceOperation)context.Operation;
            CheckUseAfterStateChange(localRef.Local, localRef.Syntax.GetLocation(), context);
        }

        public void AnalyzeParameterReference(OperationAnalysisContext context)
        {
            var paramRef = (IParameterReferenceOperation)context.Operation;
            CheckUseAfterStateChange(paramRef.Parameter, paramRef.Syntax.GetLocation(), context);
        }

        public void AnalyzeReturn(OperationAnalysisContext context)
        {
            var ret = (IReturnOperation)context.Operation;
            if (ret.ReturnedValue is null) return;

            // If the method returns [Owned] or the return value is [return: Owned],
            // the returned variable transfers ownership to the caller.
            if (_method.HasReturnAttribute(_known.Owned) || HasMustDisposeReturnType())
            {
                if (GetReferencedSymbol(ret.ReturnedValue) is { } returned && _states.ContainsKey(returned))
                {
                    _states[returned] = VariableState.Transferred;
                    _stateChangeLocations[returned] = ret.Syntax.GetLocation();
                }
            }
        }

        public void AnalyzeUsing(OperationAnalysisContext context)
        {
            var usingOp = (IUsingOperation)context.Operation;

            // using (var x = ...) or using (expr)
            if (usingOp.Resources is IVariableDeclarationGroupOperation declGroup)
            {
                foreach (var decl in declGroup.Declarations)
                {
                    foreach (var declarator in decl.Declarators)
                    {
                        MarkInUsing(declarator.Symbol);
                    }
                }
            }
            else if (GetReferencedSymbol(usingOp.Resources) is { } symbol)
            {
                MarkInUsing(symbol);
            }
        }

        public void AnalyzeUsingDeclaration(OperationAnalysisContext context)
        {
            var usingDecl = (IUsingDeclarationOperation)context.Operation;
            foreach (var declarator in usingDecl.DeclarationGroup.Declarations.SelectMany(d => d.Declarators))
            {
                MarkInUsing(declarator.Symbol);
            }
        }

        public void OnBlockEnd(OperationBlockAnalysisContext context)
        {
            foreach (var kvp in _states)
            {
                if (kvp.Value == VariableState.Active)
                {
                    var location = _declarationLocations.TryGetValue(kvp.Key, out var loc)
                        ? loc
                        : kvp.Key.Locations.FirstOrDefault() ?? Location.None;

                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.OwnedNotDisposed,
                        location,
                        kvp.Key.Name));
                }
            }
        }

        // --- Helpers ---

        private static bool IsInsideUsing(IOperation operation)
        {
            var current = operation.Parent;
            while (current is not null)
            {
                if (current is IUsingOperation or IUsingDeclarationOperation)
                    return true;
                current = current.Parent;
            }

            return false;
        }

        private void MarkInUsing(ISymbol symbol)
        {
            if (_states.ContainsKey(symbol))
            {
                _states[symbol] = VariableState.InUsing;
                _stateChangeLocations[symbol] = Location.None;
            }
        }

        private void CheckUseAfterStateChange(ISymbol symbol, Location useLocation, OperationAnalysisContext context)
        {
            if (!_states.TryGetValue(symbol, out var state)) return;

            // Skip if this reference IS the state change itself (e.g., the .Dispose() call target,
            // or the argument being passed as [Owned]).
            if (IsPartOfStateChangeOperation(context.Operation)) return;

            switch (state)
            {
                case VariableState.Transferred:
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.UseAfterMove,
                        useLocation,
                        symbol.Name));
                    break;

                case VariableState.Disposed:
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.UseAfterDispose,
                        useLocation,
                        symbol.Name));
                    break;
            }
        }

        private bool IsPartOfStateChangeOperation(IOperation operation)
        {
            // Walk up the operation tree to see if this reference is part of
            // a Dispose() call or an [Owned] argument — those are the state
            // changes themselves, not "uses" after the change.
            var current = operation.Parent;
            while (current is not null)
            {
                switch (current)
                {
                    case IInvocationOperation invocation:
                        // The reference is the instance of a Dispose() call — state change, not a use.
                        if (IsDisposeCall(invocation)) return true;
                        // The reference is the instance of a non-Dispose call (e.g. .ToString()) — this IS a use.
                        return false;

                    case IArgumentOperation arg:
                        // The reference is passed as an argument. It's a state change only if
                        // the parameter is [Owned] (ownership transfer).
                        return arg.Parameter is not null && arg.Parameter.HasAttribute(_known.Owned);

                    case IReturnOperation:
                        return true;

                    case ISimpleAssignmentOperation assignment:
                        // The reference is the source value of a field assignment — this is the transfer itself.
                        return assignment.Target is IFieldReferenceOperation;

                    case IConversionOperation:
                    case IParenthesizedOperation:
                        current = current.Parent;
                        continue;

                    default:
                        return false;
                }
            }

            return false;
        }

        private bool IsOwnedDisposable(IParameterSymbol param)
        {
            return param.HasAttribute(_known.Owned)
                   && param.Type.ImplementsIDisposable(_known.IDisposable);
        }

        private bool IsOwnedValue(IOperation value, ITypeSymbol targetType)
        {
            // Unwrap conversions.
            while (value is IConversionOperation conversion)
                value = conversion.Operand;

            // new MustDisposeType(...)
            if (value is IObjectCreationOperation creation
                && creation.Type is { } createdType
                && createdType.HasAttribute(_known.MustDispose)
                && createdType.ImplementsIDisposable(_known.IDisposable))
            {
                return true;
            }

            // Call to method returning [return: Owned] or [return: MustDispose],
            // where the type implements IDisposable.
            if (value is IInvocationOperation invocation)
            {
                var method = invocation.TargetMethod;
                if ((method.HasReturnAttribute(_known.Owned) || method.HasReturnAttribute(_known.MustDispose))
                    && method.ReturnType.ImplementsIDisposable(_known.IDisposable))
                {
                    return true;
                }

                // Method returning a [MustDispose] type.
                if (method.ReturnType.HasAttribute(_known.MustDispose)
                    && method.ReturnType.ImplementsIDisposable(_known.IDisposable))
                {
                    return true;
                }
            }

            // Direct constructor call for a [MustDispose] type (via implicit object creation).
            if (targetType.HasAttribute(_known.MustDispose)
                && targetType.ImplementsIDisposable(_known.IDisposable)
                && value is IObjectCreationOperation)
            {
                return true;
            }

            return false;
        }

        private bool HasMustDisposeReturnType()
        {
            return _method.ReturnType.HasAttribute(_known.MustDispose)
                   || _method.HasReturnAttribute(_known.MustDispose);
        }

        private static bool IsDisposeCall(IInvocationOperation invocation)
        {
            return invocation.TargetMethod.Name == "Dispose"
                   && invocation.Arguments.Length == 0
                   && invocation.Instance is not null;
        }

        private static ISymbol? GetReferencedSymbol(IOperation? operation)
        {
            if (operation is null) return null;

            // Unwrap conversions.
            while (operation is IConversionOperation conversion)
                operation = conversion.Operand;

            return operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation param => param.Parameter,
                _ => null
            };
        }

        /// <summary>
        /// Gets the initializer value from a parent IVariableDeclarationOperation
        /// when the declarator itself doesn't have one (single-declarator case).
        /// </summary>
        private static IOperation? GetParentDeclarationInitializer(IVariableDeclaratorOperation declarator)
        {
            if (declarator.Parent is IVariableDeclarationOperation declaration
                && declaration.Declarators.Length == 1
                && declaration.Initializer?.Value is { } value)
            {
                return value;
            }

            return null;
        }
    }
}
