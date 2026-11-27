using CsToDafny;
using CsToDafny.Translation;
using Microsoft.CodeAnalysis.CSharp;

var files = new List<string>();
var projects = new List<string>();
string? output = null;
var options = new ConverterOptions();
var references = new List<string>();
var includes = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" or "--output":
            output = args[++i];
            break;
        case "--math-ints":
            options = options with { Integers = IntegerMode.Mathematical };
            break;
        case "--throw-as-expect":
            options = options with { Throws = ThrowMode.Expect };
            break;
        case "--no-preconditions":
            options = options with { GuardsAsPreconditions = false };
            break;
        case "--module":
            options = options with { ModuleName = args[++i] };
            break;
        case "-i" or "--include":
            includes.Add(args[++i]);
            break;
        case "-r" or "--reference":
            references.Add(args[++i]);
            break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            var arg = args[i];
            if (arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || arg.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                arg.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                projects.Add(arg);
            else if (Directory.Exists(arg))
                files.AddRange(Directory.EnumerateFiles(arg, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)));
            else if (File.Exists(arg))
                files.Add(arg);
            else
            {
                Console.Error.WriteLine($"cs2dfy: no such file or directory: {arg}");
                return 2;
            }
            break;
    }
}

if (files.Count + projects.Count == 0 || files.Count > 0 && projects.Count > 0 || projects.Count > 1)
{
    PrintUsage();
    return 2;
}

options = options with { References = references, Include = includes };
var result = projects.Count == 1
    ? await ProjectLoader.ConvertProject(projects[0], options)
    : Converter.Convert(files.Select(p => new SourceFile(p, File.ReadAllText(p))), options);
foreach (var d in result.Diagnostics) Console.Error.WriteLine(d);
if (output is null) Console.Out.Write(result.Dafny);
else File.WriteAllText(output, result.Dafny);
return result.HasErrors ? 1 : 0;

static bool IsBuildOutput(string path) =>
    path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin");

static void PrintUsage() => Console.Error.WriteLine("""
    usage: cs2dfy [options] <project.csproj | solution.sln | file.cs | directory>...

    Translates C# into Dafny for verification. Untranslatable constructs are reported and
    left as UNTRANSLATED(...) holes so the output never silently drops code.

    Give a project (or solution) for large codebases: its package and project references are
    loaded so the code binds exactly as it builds. Combine with --include to lift out just the
    logic to verify.

    options:
      -o, --output <file>    write Dafny here (default: stdout)
      -i, --include <name>   translate only this namespace or type (repeatable); other source
                             types it uses become signature-only stubs
      --module <name>        name of the generated module (default: Translated)
      --math-ints            map C# integers to unbounded int (ignore overflow)
      --throw-as-expect      translate throw as a runtime 'expect' instead of a proof obligation
      --no-preconditions     do not lift argument-validation throws into 'requires'
      -r, --reference <dll>  additional assembly needed to compile loose .cs files
    """);

/// <summary>Loads a project through MSBuild so package and project references bind as in a real build.</summary>
internal static class ProjectLoader
{
    // MSBuildWorkspace evaluates projects in a separate build-host process, so no MSBuildLocator is needed.
    public static async Task<ConversionResult> ConvertProject(string path, ConverterOptions options)
    {
        using var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            Console.Error.WriteLine($"cs2dfy: workspace {e.Diagnostic.Kind.ToString().ToLowerInvariant()}: {e.Diagnostic.Message}");
        });
        Microsoft.CodeAnalysis.Project project;
        if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            project = await workspace.OpenProjectAsync(path);
        }
        else
        {
            // For a solution, translate the project that the includes point into, or the only project.
            var solution = await workspace.OpenSolutionAsync(path);
            var candidates = solution.Projects.Where(p => p.Language == Microsoft.CodeAnalysis.LanguageNames.CSharp).ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"solution has {candidates.Count} C# projects; pass the .csproj of the one to translate (its project references are still loaded)");
            project = candidates[0];
        }
        var compilation = (CSharpCompilation?)await project.GetCompilationAsync()
                          ?? throw new InvalidOperationException($"could not compile {path}");
        return Converter.Convert(compilation, options);
    }
}
