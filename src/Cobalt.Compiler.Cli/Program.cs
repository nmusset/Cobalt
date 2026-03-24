using Cobalt.Compiler.Driver;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

// Parse options
var files = new List<string>();
bool dumpAst = false, dumpSymbols = false, dumpAll = false;

foreach (var arg in args)
{
    switch (arg)
    {
        case "--dump-ast": dumpAst = true; break;
        case "--dump-symbols": dumpSymbols = true; break;
        case "--dump-all": dumpAll = true; break;
        case "--help" or "-h": PrintUsage(); return 0;
        default:
            if (arg.StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {arg}");
                return 1;
            }
            files.Add(arg);
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

int exitCode = 0;

foreach (var file in files)
{
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"Error: file not found: {file}");
        exitCode = 1;
        continue;
    }

    var source = File.ReadAllText(file);
    var compilation = new Compilation(source, file);

    // Run full pipeline
    compilation.RunAll();

    // Dump outputs
    if (dumpAst)
    {
        Console.WriteLine($"=== AST: {file} ===");
        Console.Write(compilation.DumpAst());
        Console.WriteLine();
    }

    if (dumpSymbols)
    {
        Console.WriteLine($"=== Symbols: {file} ===");
        Console.Write(compilation.DumpSymbols());
        Console.WriteLine();
    }

    // Always print diagnostics
    var diagnosticOutput = compilation.DumpDiagnostics();
    if (!string.IsNullOrEmpty(diagnosticOutput))
    {
        Console.Write(diagnosticOutput);
    }

    if (compilation.Diagnostics.HasErrors)
        exitCode = 1;
}

if (exitCode == 0 && !dumpAst && !dumpSymbols)
    Console.WriteLine("Compilation succeeded.");

return exitCode;

static void PrintUsage()
{
    Console.WriteLine("""
        cobaltc — Cobalt compiler

        Usage: cobaltc [options] <file.co> [file2.co ...]

        Options:
          --dump-ast       Print the parsed AST
          --dump-symbols   Print resolved type symbols
          --dump-all       Print all dump outputs
          --help, -h       Show this help

        Examples:
          cobaltc main.co
          cobaltc --dump-ast samples/cobalt-syntax/main.co
          cobaltc --dump-all samples/cobalt-syntax/*.co
        """);
}
