using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Cobalt.Analyzers.Tests;

/// <summary>
/// Helper for running analyzer tests with Cobalt.Annotations available.
/// </summary>
internal static class AnalyzerTestHelper
{
    /// <summary>
    /// Annotations source that is added to every test compilation so the
    /// analyzer can discover the attributes by metadata name.
    /// </summary>
    private const string AnnotationsSource = """
        namespace Cobalt.Annotations
        {
            [System.AttributeUsage(
                System.AttributeTargets.Parameter | System.AttributeTargets.ReturnValue |
                System.AttributeTargets.Field | System.AttributeTargets.Property,
                Inherited = true, AllowMultiple = false)]
            public sealed class OwnedAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
            public sealed class BorrowedAttribute : System.Attribute { }

            [System.AttributeUsage(System.AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
            public sealed class MutBorrowedAttribute : System.Attribute { }

            [System.AttributeUsage(
                System.AttributeTargets.Class | System.AttributeTargets.Struct |
                System.AttributeTargets.ReturnValue,
                Inherited = true, AllowMultiple = false)]
            public sealed class MustDisposeAttribute : System.Attribute { }

            [System.AttributeUsage(
                System.AttributeTargets.Parameter | System.AttributeTargets.Field | System.AttributeTargets.Property,
                Inherited = true, AllowMultiple = false)]
            public sealed class ScopedAttribute : System.Attribute { }

            [System.AttributeUsage(
                System.AttributeTargets.Parameter | System.AttributeTargets.Field | System.AttributeTargets.Property,
                Inherited = true, AllowMultiple = false)]
            public sealed class NoAliasAttribute : System.Attribute { }
        }
        """;

    public static CSharpAnalyzerTest<TAnalyzer, DefaultVerifier> CreateTest<TAnalyzer>(
        string source,
        params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        test.TestState.Sources.Add(("Annotations.cs", AnnotationsSource));
        test.ExpectedDiagnostics.AddRange(expected);

        return test;
    }
}
