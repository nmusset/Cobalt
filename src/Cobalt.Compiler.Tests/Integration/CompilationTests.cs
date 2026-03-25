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

    // ──────────────────────────────────────────────
    // 1. hello.co compiles and has expected types
    // ──────────────────────────────────────────────

    [Fact]
    public void Compile_HelloCo_ProducesAssemblyWithExpectedTypes()
    {
        var output = Path.GetTempFileName() + ".dll";
        try
        {
            var compilation = new Compilation();
            var success = compilation.Compile([FindSample("hello.co")], output);
            Assert.True(success, "Compilation failed: " + compilation.FormatDiagnostics());

            using var asm = AssemblyDefinition.ReadAssembly(output);
            var typeNames = asm.MainModule.Types.Select(t => t.Name).ToList();
            Assert.Contains("Greeter", typeNames);
            Assert.Contains("MathHelper", typeNames);
            Assert.Contains("Printable", typeNames);
            Assert.Contains("Shape", typeNames);
        }
        finally
        {
            if (File.Exists(output)) try { File.Delete(output); } catch { /* file may be locked */ };
        }
    }

    // ──────────────────────────────────────────────
    // 2. showcase.co compiles with union variants
    // ──────────────────────────────────────────────

    [Fact]
    public void Compile_ShowcaseCo_ProducesAssemblyWithUnionVariants()
    {
        var output = Path.GetTempFileName() + ".dll";
        try
        {
            var compilation = new Compilation();
            var success = compilation.Compile([FindSample("showcase.co")], output);
            Assert.True(success, "Compilation failed: " + compilation.FormatDiagnostics());

            using var asm = AssemblyDefinition.ReadAssembly(output);
            var acquireResult = asm.MainModule.Types.First(t => t.Name == "AcquireResult");
            var nestedNames = acquireResult.NestedTypes.Select(n => n.Name).ToList();
            Assert.Contains("Acquired", nestedNames);
            Assert.Contains("Exhausted", nestedNames);
        }
        finally
        {
            if (File.Exists(output)) try { File.Delete(output); } catch { /* file may be locked */ };
        }
    }

    // ──────────────────────────────────────────────
    // 3. showcase.co has ownership attributes
    // ──────────────────────────────────────────────

    [Fact]
    public void Compile_ShowcaseCo_HasOwnershipAttributes()
    {
        var output = Path.GetTempFileName() + ".dll";
        try
        {
            var compilation = new Compilation();
            var success = compilation.Compile([FindSample("showcase.co")], output);
            Assert.True(success, "Compilation failed: " + compilation.FormatDiagnostics());

            using var asm = AssemblyDefinition.ReadAssembly(output);
            var pool = asm.MainModule.Types.First(t => t.Name == "ResourcePool");

            // own string name field should have [Owned]
            var nameField = pool.Fields.FirstOrDefault(f => f.Name == "name");
            Assert.NotNull(nameField);
            Assert.Contains(nameField.CustomAttributes, a => a.AttributeType.Name == "OwnedAttribute");

            // Acquire() return type should have [Owned]
            var acquire = pool.Methods.First(m => m.Name == "Acquire");
            Assert.Contains(acquire.MethodReturnType.CustomAttributes,
                a => a.AttributeType.Name == "OwnedAttribute");
        }
        finally
        {
            if (File.Exists(output)) try { File.Delete(output); } catch { /* file may be locked */ };
        }
    }

    // ──────────────────────────────────────────────
    // 4. showcase.co match statement emits isinst
    // ──────────────────────────────────────────────

    [Fact]
    public void Compile_ShowcaseCo_MatchEmitsIsinst()
    {
        var output = Path.GetTempFileName() + ".dll";
        try
        {
            var compilation = new Compilation();
            var success = compilation.Compile([FindSample("showcase.co")], output);
            Assert.True(success, "Compilation failed: " + compilation.FormatDiagnostics());

            using var asm = AssemblyDefinition.ReadAssembly(output);
            var pool = asm.MainModule.Types.First(t => t.Name == "ResourcePool");
            var describe = pool.Methods.First(m => m.Name == "DescribeResult");
            Assert.True(describe.Body.Instructions.Any(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Isinst));
        }
        finally
        {
            if (File.Exists(output)) try { File.Delete(output); } catch { /* file may be locked */ };
        }
    }

    // ──────────────────────────────────────────────
    // 5. Negative: use-after-move reports error
    // ──────────────────────────────────────────────

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
        Assert.Contains(compilation.Diagnostics.All, d => d.Id == "CB3006");
    }
}
