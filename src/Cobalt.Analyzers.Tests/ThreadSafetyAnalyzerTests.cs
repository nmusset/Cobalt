using Microsoft.CodeAnalysis.Testing;

namespace Cobalt.Analyzers.Tests;

public class ThreadSafetyAnalyzerTests
{
    private static DiagnosticResult Diagnostic(string id) =>
        new(id, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);

    // ---------------------------------------------------------------
    // CB0007: NotSync value captured by concurrent lambda
    // ---------------------------------------------------------------

    [Fact]
    public async Task NotSync_CapturedByTaskRun_Reports_CB0007()
    {
        var source = """
            using System.Threading.Tasks;
            using Cobalt.Annotations;

            [NotSync]
            class Counter
            {
                public int Value;
                public void Increment() => Value++;
            }

            class C
            {
                void M()
                {
                    var counter = new Counter();
                    Task.Run(() =>
                    {
                        {|#0:counter|}.Increment();
                    });
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<ThreadSafetyAnalyzer>(
            source,
            Diagnostic("CB0007").WithLocation(0).WithArguments("counter", "Counter", "Task.Run"));

        await test.RunAsync();
    }

    [Fact]
    public async Task NotSync_CapturedByParallelForEach_Reports_CB0007()
    {
        var source = """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Cobalt.Annotations;

            [NotSync]
            class Accumulator
            {
                public int Total;
                public void Add(int v) => Total += v;
            }

            class C
            {
                void M()
                {
                    var acc = new Accumulator();
                    var items = new List<int> { 1, 2, 3 };
                    Parallel.ForEach(items, item =>
                    {
                        {|#0:acc|}.Add(item);
                    });
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<ThreadSafetyAnalyzer>(
            source,
            Diagnostic("CB0007").WithLocation(0).WithArguments("acc", "Accumulator", "Parallel.ForEach"));

        await test.RunAsync();
    }

    [Fact]
    public async Task NotSync_CapturedByNewThread_Reports_CB0007()
    {
        var source = """
            using System.Threading;
            using Cobalt.Annotations;

            [NotSync]
            class State
            {
                public int Value;
            }

            class C
            {
                void M()
                {
                    var state = new State();
                    var t = new Thread(() =>
                    {
                        {|#0:state|}.Value = 42;
                    });
                    t.Start();
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<ThreadSafetyAnalyzer>(
            source,
            Diagnostic("CB0007").WithLocation(0).WithArguments("state", "State", "Thread..ctor"));

        await test.RunAsync();
    }

    // ---------------------------------------------------------------
    // No false positives
    // ---------------------------------------------------------------

    [Fact]
    public async Task SyncType_CapturedByTaskRun_NoDiagnostic()
    {
        var source = """
            using System.Threading.Tasks;
            using Cobalt.Annotations;

            [Sync]
            class SafeCounter
            {
                private int _value;
                public void Increment() => System.Threading.Interlocked.Increment(ref _value);
            }

            class C
            {
                void M()
                {
                    var counter = new SafeCounter();
                    Task.Run(() =>
                    {
                        counter.Increment();
                    });
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<ThreadSafetyAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task UnannotatedType_CapturedByTaskRun_NoDiagnostic()
    {
        var source = """
            using System.Threading.Tasks;

            class PlainCounter
            {
                public int Value;
            }

            class C
            {
                void M()
                {
                    var counter = new PlainCounter();
                    Task.Run(() =>
                    {
                        counter.Value++;
                    });
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<ThreadSafetyAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task NotSync_NotCaptured_NoDiagnostic()
    {
        var source = """
            using Cobalt.Annotations;

            [NotSync]
            class Counter
            {
                public int Value;
                public void Increment() => Value++;
            }

            class C
            {
                void M()
                {
                    var counter = new Counter();
                    counter.Increment(); // used on same thread — no issue
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<ThreadSafetyAnalyzer>(source);
        await test.RunAsync();
    }

    [Fact]
    public async Task NotSync_MultipleCaptured_ReportsOnce()
    {
        var source = """
            using System.Threading.Tasks;
            using Cobalt.Annotations;

            [NotSync]
            class State
            {
                public int A;
                public int B;
            }

            class C
            {
                void M()
                {
                    var s = new State();
                    Task.Run(() =>
                    {
                        {|#0:s|}.A = 1;
                        s.B = 2; // same symbol — only reported once
                    });
                }
            }
            """;

        var test = AnalyzerTestHelper.CreateTest<ThreadSafetyAnalyzer>(
            source,
            Diagnostic("CB0007").WithLocation(0).WithArguments("s", "State", "Task.Run"));

        await test.RunAsync();
    }
}
