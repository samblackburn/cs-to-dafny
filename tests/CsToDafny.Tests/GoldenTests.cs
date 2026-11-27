using System.Diagnostics;
using System.Runtime.CompilerServices;
using CsToDafny.Translation;

namespace CsToDafny.Tests;

/// <summary>
/// Each Cases/*.cs file is converted and compared with Cases/*.expected.dfy (module body only; the prelude is
/// excluded). Set UPDATE_GOLDEN=1 to rewrite the expectations. When Dafny is on the PATH, the full output must
/// also resolve, and a "// verify: N" comment in the case states how many verification errors Dafny must report.
/// </summary>
public class GoldenTests
{
    private static string CasesDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Cases");

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(CasesDir(), "*.cs").Order()) data.Add(Path.GetFileNameWithoutExtension(f));
        return data;
    }

    private static ConversionResult Convert(string name)
    {
        var path = Path.Combine(CasesDir(), name + ".cs");
        var text = File.ReadAllText(path);
        // "// include: X" lines in a case select what to translate.
        var include = System.Text.RegularExpressions.Regex.Matches(text, @"^//\s*include:\s*(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        return Converter.Convert([new SourceFile(name + ".cs", text)], new ConverterOptions { Include = include });
    }

    private static string ModuleBody(string dafny) => dafny[dafny.IndexOf("module Translated", StringComparison.Ordinal)..].ReplaceLineEndings("\n");

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesGolden(string name)
    {
        var result = Convert(name);
        var actual = ModuleBody(result.Dafny);
        var diagnostics = string.Join("\n", result.Diagnostics.Select(d => "// " + d));
        var snapshot = (diagnostics.Length > 0 ? diagnostics + "\n\n" : "") + actual;
        var expectedPath = Path.Combine(CasesDir(), name + ".expected.dfy");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1" || !File.Exists(expectedPath))
        {
            File.WriteAllText(expectedPath, snapshot);
            if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") != "1")
                Assert.Fail($"No expectation existed for {name}; wrote one. Review it and re-run.");
            return;
        }
        Assert.Equal(File.ReadAllText(expectedPath).ReplaceLineEndings("\n"), snapshot);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HasNoUntranslatedCode(string name)
    {
        var result = Convert(name);
        Assert.DoesNotContain("UNTRANSLATED", result.Dafny);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ResolvesAndVerifiesAsExpected(string name)
    {
        if (!DafnyAvailable()) return;
        var source = File.ReadAllText(Path.Combine(CasesDir(), name + ".cs"));
        var dir = Directory.CreateTempSubdirectory("cs2dfy-test");
        try
        {
            var file = Path.Combine(dir.FullName, name + ".dfy");
            File.WriteAllText(file, Convert(name).Dafny);

            var (resolveCode, resolveOut) = RunDafny("resolve", file);
            Assert.True(resolveCode == 0, resolveOut);

            var expected = System.Text.RegularExpressions.Regex.Match(source, @"//\s*verify:\s*(\d+)");
            if (!expected.Success) return;
            var (_, verifyOut) = RunDafny("verify", file);
            var errors = System.Text.RegularExpressions.Regex.Match(verifyOut, @"finished with \d+ verified, (\d+) error");
            var count = errors.Success ? int.Parse(errors.Groups[1].Value) : 0;
            Assert.True(count == int.Parse(expected.Groups[1].Value), verifyOut);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static bool? _dafny;

    private static bool DafnyAvailable()
    {
        if (_dafny is { } known) return known;
        try
        {
            _dafny = RunDafny("--version").Code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _dafny = false;
        }
        return _dafny.Value;
    }

    private static (int Code, string Output) RunDafny(params string[] args)
    {
        var psi = new ProcessStartInfo("dafny") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (args[0] is "verify") psi.ArgumentList.Add("--allow-warnings");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, output.Result + error.Result);
    }
}
