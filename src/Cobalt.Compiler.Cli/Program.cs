using Cobalt.Compiler.Diagnostics;
using Cobalt.Compiler.Driver;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

// Parse options
var files = new List<string>();
string? outputPath = null;
bool dumpAst = false, dumpSymbols = false, dumpAll = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dump-ast": dumpAst = true; break;
        case "--dump-symbols": dumpSymbols = true; break;
        case "--dump-all": dumpAll = true; break;
        case "-o":
            if (i + 1 < args.Length)
                outputPath = args[++i];
            else
            {
                Console.Error.WriteLine("Error: -o requires an output path");
                return 1;
            }
            break;
        case "--help" or "-h": PrintUsage(); return 0;
        default:
            if (args[i].StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return 1;
            }
            files.Add(args[i]);
            break;
    }
}

if (dumpAll)
{
    dumpAst = true;
    dumpSymbols = true;
}

if (files.Count == 0)
{
    Console.Error.WriteLine("Error: no input files");
    return 1;
}

// Check all files exist
foreach (var file in files)
{
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"Error: file not found: {file}");
        return 1;
    }
}

bool isDumpOnly = dumpAst || dumpSymbols;
bool isCompile = outputPath != null || !isDumpOnly;

var compilation = new Compilation();

if (isDumpOnly && !isCompile)
{
    // Dump mode: analyze each file individually
    foreach (var file in files)
    {
        var source = File.ReadAllText(file);
        var comp = new Compilation();
        comp.Analyze(source, file);

        if (dumpAst)
        {
            Console.WriteLine($"=== AST: {file} ===");
            Console.Write(comp.DumpAst());
            Console.WriteLine();
        }

        if (dumpSymbols)
        {
            Console.WriteLine($"=== Symbols: {file} ===");
            Console.Write(comp.DumpSymbols());
            Console.WriteLine();
        }

        var diagOutput = comp.FormatDiagnostics();
        if (!string.IsNullOrEmpty(diagOutput))
            Console.Write(diagOutput);
    }
    return 0;
}

// Compile mode: multi-file compilation to assembly
outputPath ??= Path.ChangeExtension(files[0], ".dll");

bool success = compilation.Compile(files.ToArray(), outputPath);

// Dump intermediate results if requested
if (dumpAst)
{
    Console.WriteLine($"=== AST (merged) ===");
    Console.Write(compilation.DumpAst());
    Console.WriteLine();
}

if (dumpSymbols)
{
    Console.WriteLine($"=== Symbols ===");
    Console.Write(compilation.DumpSymbols());
    Console.WriteLine();
}

// Print diagnostics
foreach (var d in compilation.Diagnostics.All)
{
    var stream = d.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;
    stream.WriteLine(d);
}

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

return 1;

static void PrintUsage()
{
    Console.WriteLine("""
        cobaltc — Cobalt compiler

        Usage: cobaltc [options] <file.co> [file2.co ...]

        Options:
          -o <path>        Output assembly path (default: <first-file>.dll)
          --dump-ast       Print the parsed AST
          --dump-symbols   Print resolved type symbols
          --dump-all       Print all dump outputs
          --help, -h       Show this help

        Examples:
          cobaltc main.co processor.co -o app.dll
          cobaltc --dump-ast samples/cobalt-syntax/main.co
          cobaltc --dump-all samples/cobalt-syntax/*.co
        """);
}
