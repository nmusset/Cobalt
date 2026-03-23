using Microsoft.CodeAnalysis.Testing;

namespace Cobalt.Analyzers.Tests;

public class OwnershipAnalyzerTests
{
    private static DiagnosticResult Diagnostic(string id) =>
        new(id, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);

    // ---------------------------------------------------------------
    // CB0001: Owned disposable not disposed
    // ---------------------------------------------------------------

    [Fact]
    public async Task OwnedParameter_NotDisposed_Reports_CB0001()
    {
        var source = """
            using System;
            using Cobalt.Annotations;

            class C
            {
                void M([Owned] IDisposable {|#0:d|})
                {
                    // d is not disposed or transferred.
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(
            source,
            Diagnostic("CB0001").WithLocation(0).WithArguments("d"));

        await test.RunAsync();
    }

    [Fact]
    public async Task OwnedParameter_Disposed_NoDiagnostic()
    {
        var source = """
            using System;
            using Cobalt.Annotations;

            class C
            {
                void M([Owned] IDisposable d)
                {
                    d.Dispose();
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task OwnedParameter_InUsing_NoDiagnostic()
    {
        var source = """
            using System;
            using Cobalt.Annotations;

            class C
            {
                void M([Owned] IDisposable d)
                {
                    using (d)
                    {
                        // used within using
                    }
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task OwnedParameter_Transferred_NoDiagnostic()
    {
        var source = """
            using System;
            using Cobalt.Annotations;

            class C
            {
                void M([Owned] IDisposable d)
                {
                    Consume(d);
                }

                void Consume([Owned] IDisposable d)
                {
                    d.Dispose();
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task MustDisposeType_NotDisposed_Reports_CB0001()
    {
        var source = """
            using System;
            using Cobalt.Annotations;

            [MustDispose]
            class MyResource : IDisposable
            {
                public void Dispose() { }
            }

            class C
            {
                void M()
                {
                    var {|#0:r|} = new MyResource();
                    // r is not disposed.
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(
            source,
            Diagnostic("CB0001").WithLocation(0).WithArguments("r"));

        await test.RunAsync();
    }

    [Fact]
    public async Task MustDisposeType_WithUsing_NoDiagnostic()
    {
        var source = """
            using System;
            using Cobalt.Annotations;

            [MustDispose]
            class MyResource : IDisposable
            {
                public void Dispose() { }
            }

            class C
            {
                void M()
                {
                    using var r = new MyResource();
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task MustDisposeReturnValue_NotDisposed_Reports_CB0001()
    {
        var source = """
            using System;
            using System.IO;
            using Cobalt.Annotations;

            class C
            {
                [return: MustDispose]
                Stream CreateStream() => new MemoryStream();

                void M()
                {
                    var {|#0:s|} = CreateStream();
                    // s is not disposed.
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(
            source,
            Diagnostic("CB0001").WithLocation(0).WithArguments("s"));

        await test.RunAsync();
    }

    // ---------------------------------------------------------------
    // CB0002: Use after ownership transfer (move)
    // ---------------------------------------------------------------

    [Fact]
    public async Task UseAfterMove_Reports_CB0002()
    {
        var source = """
            using System;
            using System.IO;
            using Cobalt.Annotations;

            class C
            {
                void M()
                {
                    var s = new MemoryStream();
                    Consume(s);
                    {|#0:s|}.Read(new byte[1], 0, 1); // use after move
                }

                void Consume([Owned] Stream s) { s.Dispose(); }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(
            source,
            Diagnostic("CB0002").WithLocation(0).WithArguments("s"));

        await test.RunAsync();
    }

    [Fact]
    public async Task NoUseAfterMove_NoDiagnostic()
    {
        var source = """
            using System;
            using System.IO;
            using Cobalt.Annotations;

            class C
            {
                void M()
                {
                    var s = new MemoryStream();
                    s.Write(new byte[] { 1 }, 0, 1); // use before move — OK
                    Consume(s);
                }

                void Consume([Owned] Stream s) { s.Dispose(); }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }

    // ---------------------------------------------------------------
    // CB0003: Use after dispose
    // ---------------------------------------------------------------

    [Fact]
    public async Task UseAfterDispose_Reports_CB0003()
    {
        var source = """
            using System;
            using Cobalt.Annotations;

            class C
            {
                void M([Owned] IDisposable d)
                {
                    d.Dispose();
                    {|#0:d|}.ToString(); // use after dispose
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(
            source,
            Diagnostic("CB0003").WithLocation(0).WithArguments("d"));

        await test.RunAsync();
    }

    // ---------------------------------------------------------------
    // No false positives
    // ---------------------------------------------------------------

    [Fact]
    public async Task NonOwnedParameter_NoDiagnostic()
    {
        var source = """
            using System;

            class C
            {
                void M(IDisposable d)
                {
                    // No [Owned] attribute — not tracked.
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task OwnedNonDisposable_NoDiagnostic()
    {
        var source = """
            using Cobalt.Annotations;

            class C
            {
                void M([Owned] string s)
                {
                    // string is not IDisposable — not tracked.
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task NoAnnotations_NoDiagnostic()
    {
        var source = """
            using System;
            using System.IO;

            class C
            {
                void M()
                {
                    var s = new MemoryStream();
                    // No Cobalt annotations — analyzer is silent.
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<OwnershipAnalyzer>(source);
        await test.RunAsync();
    }
}
