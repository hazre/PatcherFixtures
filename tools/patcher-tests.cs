#:property TargetFramework=net10.0
#:property Nullable=disable
#:property ImplicitUsings=disable
#:property PublishAot=false
#:property PackAsTool=false
#:package System.CommandLine@2.0.0

// Patcher fixture test runner (file-based app, .NET 10+).
//
// Reproduces the manual old-vs-new test matrix: stages fixture DLLs into a
// Resonite BepInEx profile's patchers/ dir, launches the game, waits, kills
// it, renames LogOutput.log per case, and asserts expected loader output.
//
// Run from the PatcherFixtures repo root:
//   dotnet run --file tools/patcher-tests.cs -- --expect new
//   dotnet run --file tools/patcher-tests.cs -- --list
//   dotnet run --file tools/patcher-tests.cs -- --case RenamedBase --timeout 45 --no-build

using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var rootOption = new Option<string>("--root") { Description = "Fixture repo root." };
rootOption.DefaultValueFactory = _ => Directory.GetCurrentDirectory();
var profileDirOption = new Option<string>("--profile-dir") { Description = "BepInEx profile dir." };
profileDirOption.DefaultValueFactory = _ => Path.Combine(appData, @"com.kesomannen.gale\resonite\profiles\Test\BepInEx");
var gameOption = new Option<string>("--game") { Description = "Resonite.exe path." };
gameOption.DefaultValueFactory = _ => @"C:\Program Files (x86)\Steam\steamapps\common\Resonite\Resonite.exe";
var timeoutOption = new Option<int>("--timeout") { Description = "Seconds per game launch." };
timeoutOption.DefaultValueFactory = _ => 30;
var expectOption = new Option<string>("--expect") { Description = "Assert new-build or old-build behavior." };
expectOption.DefaultValueFactory = _ => "new";
expectOption.AcceptOnlyFromAmong("new", "old");
var caseOption = new Option<string>("--case") { Description = "Run a single case (see --list)." };
var listOption = new Option<bool>("--list") { Description = "List cases and exit." };
var noBuildOption = new Option<bool>("--no-build") { Description = "Skip fixture build." };
var stageOnlyOption = new Option<bool>("--stage-only") { Description = "Stage files without launching." };
var noRestoreOption = new Option<bool>("--no-restore") { Description = "Leave staged files, skip backup restore." };
var skipModsOption = new Option<bool>("--skip-mods") { Description = "Skip the Mods case (no Thunderstore mods installed)." };
var skipUniexampleOption = new Option<bool>("--skip-uniexample") { Description = "Skip the UniExample case (needs UniModFramework clone, see patcher-env.cs)." };

var rootCommand = new RootCommand("Stage patcher fixtures, launch Resonite, and assert loader output.");
rootCommand.Options.Add(rootOption);
rootCommand.Options.Add(profileDirOption);
rootCommand.Options.Add(gameOption);
rootCommand.Options.Add(timeoutOption);
rootCommand.Options.Add(expectOption);
rootCommand.Options.Add(caseOption);
rootCommand.Options.Add(listOption);
rootCommand.Options.Add(noBuildOption);
rootCommand.Options.Add(stageOnlyOption);
rootCommand.Options.Add(noRestoreOption);
rootCommand.Options.Add(skipModsOption);
rootCommand.Options.Add(skipUniexampleOption);
rootCommand.SetAction(result => TesterMain(new PatcherTestOptions
{
    Root = Path.GetFullPath(result.GetValue(rootOption)),
    ProfileDir = result.GetValue(profileDirOption),
    GameExe = result.GetValue(gameOption),
    TimeoutSeconds = result.GetValue(timeoutOption),
    Expect = result.GetValue(expectOption),
    Case = result.GetValue(caseOption),
    ListOnly = result.GetValue(listOption),
    NoBuild = result.GetValue(noBuildOption),
    StageOnly = result.GetValue(stageOnlyOption),
    NoRestore = result.GetValue(noRestoreOption),
    SkipMods = result.GetValue(skipModsOption),
    SkipUniExample = result.GetValue(skipUniexampleOption),
}));
return rootCommand.Parse(args).Invoke();

static int TesterMain(PatcherTestOptions opts)
{
var dotnetExe = DotnetDiscovery.FindDotnet();
if (dotnetExe is null)
{
    Console.Error.WriteLine("error: could not find dotnet (PATH, DOTNET_ROOT, or mise dotnet-root).");
    return 2;
}

var patchersDir = Path.Combine(opts.ProfileDir, "patchers");
var logFile = Path.Combine(opts.ProfileDir, "LogOutput.log");
var cacheFile = Path.Combine(opts.ProfileDir, "cache", "chainloader_typeloader.dat");
foreach (var dir in new[] { opts.Root, opts.ProfileDir, patchersDir })
{
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"error: directory does not exist: {dir}");
        return 2;
    }
}
if (!File.Exists(opts.GameExe))
{
    Console.Error.WriteLine($"error: game exe not found: {opts.GameExe}");
    return 2;
}

var cases = opts.SkipMods ? TestMatrix.All.Where(c => !c.PreserveMods).ToArray() : TestMatrix.All;
var uniExampleBuilt = File.Exists(Path.Combine(opts.Root, "UniExample", "bin", "Debug", "UniExample.dll"));
if (opts.SkipUniExample || !uniExampleBuilt)
    cases = cases.Where(c => c.Name != "UniExample").ToArray();
if (opts.ListOnly)
{
    foreach (var c in cases)
        Console.WriteLine($"{c.Name}  [{string.Join(", ", c.Files.Select(f => f.DestName))}]");
    return 0;
}
if (opts.Case is not null)
{
    var match = cases.FirstOrDefault(c => c.Name.Equals(opts.Case, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        if (opts.Case.Equals("UniExample", StringComparison.OrdinalIgnoreCase) && !uniExampleBuilt)
            Console.Error.WriteLine("error: UniExample not built. Run patcher-env.cs first (it clones and builds UniModFramework).");
        else
            Console.Error.WriteLine($"error: unknown case '{opts.Case}'. Use --list to see cases.");
        return 2;
    }
    cases = new[] { match };
}

if (!opts.NoBuild)
{
    Console.WriteLine($"Building fixtures in {opts.Root} ...");
    if (Shell.Run(dotnetExe, $"build \"{Path.Combine(opts.Root, "PatcherFixtures.slnx")}\" -c Debug --nologo -v q", opts.Root) != 0)
    {
        Console.Error.WriteLine("error: fixture build failed.");
        return 2;
    }
}

    var backupDir = Path.Combine(Path.GetTempPath(), "patcher-tests-" + Guid.NewGuid().ToString("N"));
    var patchersBackupDir = Path.Combine(backupDir, "patchers");
var failures = 0;
try
{
    BackupDirectory(patchersDir, patchersBackupDir);
    var hadLog = File.Exists(logFile);
    var originalLog = hadLog ? File.ReadAllBytes(logFile) : null;

    foreach (var c in cases)
    {
        try
        {
            RunCase(c, opts, dotnetExe, patchersDir, patchersBackupDir, logFile, cacheFile);
            Console.WriteLine($"PASS {c.Name}");
        }
        catch (CaseFailure ex)
        {
            failures++;
            Console.WriteLine($"FAIL {c.Name}: {ex.Message}");
        }
    }

    if (!opts.NoRestore)
    {
        RestoreDirectory(patchersBackupDir, patchersDir);
        if (originalLog is not null)
            File.WriteAllBytes(logFile, originalLog);
        else if (File.Exists(logFile))
            File.Delete(logFile);
    }
    else
    {
        Console.WriteLine($"Staged files left in place (--no-restore). Backup at {backupDir}");
        backupDir = null;
    }
}
finally
{
    if (backupDir is not null && Directory.Exists(backupDir))
        Directory.Delete(backupDir, recursive: true);
}

Console.WriteLine(failures == 0 ? $"All {cases.Length} case(s) passed (expect={opts.Expect})." : $"{failures}/{cases.Length} case(s) failed (expect={opts.Expect}).");
return failures;
}

static void RunCase(TestCase c, PatcherTestOptions opts, string dotnetExe, string patchersDir, string patchersBackupDir, string logFile, string cacheFile)
{
    StageCase(c, opts, patchersDir, patchersBackupDir);

    if (opts.StageOnly)
    {
        Console.WriteLine($"STAGED {c.Name}: {string.Join(", ", c.Files.Select(f => f.DestName))}");
        return;
    }

    if (File.Exists(logFile)) File.Delete(logFile);
    if (File.Exists(cacheFile)) File.Delete(cacheFile);

    LaunchGameAndWait(opts);
    var stagedLog = Path.Combine(opts.ProfileDir, $"LogOutput_{c.Name}.log");
    var text = WaitForLog(logFile);
    File.Copy(logFile, stagedLog, overwrite: true);
    Console.WriteLine($"  log -> {Path.GetFileName(stagedLog)}");

    var stripped = Ansi.Strip(text);
    var (mustContain, mustNotContain) = c.Expectations(opts.Expect);
    var missing = mustContain.Where(s => !stripped.Contains(s, StringComparison.Ordinal)).ToList();
    var unexpected = mustNotContain.Where(s => stripped.Contains(s, StringComparison.Ordinal)).ToList();
    if (missing.Count > 0 || unexpected.Count > 0)
    {
        var detail = new StringBuilder();
        foreach (var s in missing) detail.Append($" missing<{s}>");
        foreach (var s in unexpected) detail.Append($" unexpected<{s}>");
        throw new CaseFailure(detail.ToString().Trim());
    }
}

static void StageCase(TestCase c, PatcherTestOptions opts, string patchersDir, string patchersBackupDir)
{
    if (c.PreserveMods)
    {
        // Fixture cases wipe patchers/; restore the pre-matrix snapshot so
        // Thunderstore mods (subdirs and root files alike) are all present.
        foreach (var entry in Directory.GetFileSystemEntries(patchersDir))
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        CopyDir(patchersBackupDir, patchersDir);
        return;
    }
    foreach (var entry in Directory.GetFileSystemEntries(patchersDir))
    {
        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
        else File.Delete(entry);
    }
    foreach (var f in c.Files)
    {
        var src = Path.Combine(opts.Root, f.Project, "bin", "Debug", f.SrcFile);
        if (!File.Exists(src))
            throw new CaseFailure($"fixture DLL not built: {src}");
        File.Copy(src, Path.Combine(patchersDir, f.DestName), overwrite: true);
    }
}

static void LaunchGameAndWait(PatcherTestOptions opts)
{
    var psi = new ProcessStartInfo(opts.GameExe,
        $"-Screen -DoNotAutoLoadHome -SkipIntroTutorial --hookfxr-enable --bepinex-target \"{opts.ProfileDir}\"")
    {
        UseShellExecute = false,
    };
    using var game = Process.Start(psi);
    if (game is null)
        throw new CaseFailure("could not start game process");
    if (game.WaitForExit(opts.TimeoutSeconds * 1000))
        return;
    try { game.Kill(entireProcessTree: true); } catch { }
    game.WaitForExit(10_000);
    foreach (var name in new[] { "Resonite", "Renderite.Host", "Renderite.Renderer" })
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(); } catch { }
            p.Dispose();
        }
    }
}

static string WaitForLog(string logFile)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!File.Exists(logFile) && DateTime.UtcNow < deadline)
        Thread.Sleep(500);
    if (!File.Exists(logFile))
        throw new CaseFailure("LogOutput.log never appeared");
    long lastSize = -1;
    var stableSince = DateTime.UtcNow;
    while ((DateTime.UtcNow - stableSince).TotalSeconds < 2 && DateTime.UtcNow < deadline.AddSeconds(15))
    {
        Thread.Sleep(500);
        try
        {
            var size = new FileInfo(logFile).Length;
            if (size != lastSize) { lastSize = size; stableSince = DateTime.UtcNow; }
        }
        catch { }
    }
    using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return reader.ReadToEnd();
}

static void BackupDirectory(string source, string backup)
{
    Directory.CreateDirectory(backup);
    foreach (var entry in Directory.GetFileSystemEntries(source))
    {
        var dest = Path.Combine(backup, Path.GetFileName(entry));
        if (Directory.Exists(entry))
            CopyDir(entry, dest);
        else
            File.Copy(entry, dest, overwrite: true);
    }
}

static void RestoreDirectory(string backup, string target)
{
    foreach (var entry in Directory.GetFileSystemEntries(target))
    {
        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
        else File.Delete(entry);
    }
    foreach (var entry in Directory.GetFileSystemEntries(backup))
    {
        var dest = Path.Combine(target, Path.GetFileName(entry));
        if (Directory.Exists(entry))
            CopyDir(entry, dest);
        else
            File.Copy(entry, dest, overwrite: true);
    }
}

static void CopyDir(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    foreach (var dir in Directory.GetDirectories(source))
        CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)));
}

sealed class CaseFailure : Exception
{
    public CaseFailure(string message) : base(message) { }
}

static class Ansi
{
    private static readonly Regex Escape = new("\x1B\\[[0-9;]*m", RegexOptions.Compiled);
    public static string Strip(string text) => Escape.Replace(text, "");
}

static class Shell
{
    public static int Run(string exe, string arguments, string workDir)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
        });
        if (p is null) return 1;
        p.WaitForExit();
        return p.ExitCode;
    }
}

static class DotnetDiscovery
{
    public static string FindDotnet()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_EXE");
        if (fromEnv is not null && File.Exists(fromEnv)) return fromEnv;
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("dotnet", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is not null && probe.WaitForExit(15_000) && probe.ExitCode == 0)
                return "dotnet";
            probe?.Dispose();
        }
        catch { }
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"mise\dotnet-root\dotnet.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

sealed record FixtureFile(string Project, string DestName, string SrcFile)
{
    public FixtureFile(string project) : this(project, project + ".dll", project + ".dll") { }
    public FixtureFile(string project, string destName) : this(project, destName, project + ".dll") { }
}

sealed record TestCase(string Name, FixtureFile[] Files, string[] Present, string[] Absent, bool NewOnly, bool PreserveMods = false)
{
    public (string[] MustContain, string[] MustNotContain) Expectations(string expect)
    {
        if (expect == "old" && NewOnly)
            return (Array.Empty<string>(), Present.Concat(Absent).ToArray());
        return (Present, Absent);
    }
}

static class TestMatrix
{
    public static readonly TestCase[] All = new[]
    {
        new TestCase("Base", Array.Empty<FixtureFile>(),
            new[] { "0 patcher definition(s) loaded" }, new[] { "active" }, false),
        new TestCase("DirectPatcher_IndirectPatcher",
            new[] { new FixtureFile("DirectPatcher"), new FixtureFile("IndirectPatcher") },
            new[] { "DirectPatcher active", "Loaded 1 patcher type from [DirectPatcher 1.0.0.0]" },
            new[] { "IndirectPatcher active" }, false),
        new TestCase("IndirectBase_IndirectPatcher",
            new[] { new FixtureFile("IndirectBase"), new FixtureFile("IndirectPatcher") },
            new[] { "IndirectPatcher active", "Loaded 1 patcher type from [IndirectPatcher 1.0.0.0]" },
            Array.Empty<string>(), false),
        new TestCase("DerivedAttributePatcher",
            new[] { new FixtureFile("DerivedAttributePatcher") },
            new[] { "DerivedAttributePatcher active", "Loaded 1 patcher type from [DerivedAttributePatcher 1.0.0.0]" },
            Array.Empty<string>(), true),
        new TestCase("FrameworkPatcher_MiniFramework",
            new[] { new FixtureFile("MiniFramework"), new FixtureFile("FrameworkPatcher") },
            new[] { "FrameworkPatcher active", "Loaded 1 patcher type from [FrameworkPatcher 1.0.0.0]" },
            Array.Empty<string>(), true),
        new TestCase("UniExample",
            new[] { new FixtureFile("UniExample"), new FixtureFile("UniExample", "UniModFramework.dll", "UniModFramework.dll") },
            new[] { "UniExamplePatcher active", "Loaded 1 patcher type from [UniExample 1.0.0.0]" },
            Array.Empty<string>(), true),
        new TestCase("zzIndirectBase_IndirectPatcher",
            new[] { new FixtureFile("IndirectBase", "zzIndirectBase.dll"), new FixtureFile("IndirectPatcher") },
            new[] { "IndirectPatcher active", "Loaded 1 patcher type from [IndirectPatcher 1.0.0.0]" },
            Array.Empty<string>(), true),
        new TestCase("IndirectPatcherAlone",
            new[] { new FixtureFile("IndirectPatcher") },
            Array.Empty<string>(), new[] { "active" }, false),
        new TestCase("PlainPlugin",
            new[] { new FixtureFile("PlainPlugin") },
            Array.Empty<string>(), new[] { "active", "PlainPlugin loaded" }, false),
        new TestCase("EarlyPatcher_LateBase",
            new[] { new FixtureFile("EarlyPatcher"), new FixtureFile("LateBase") },
            new[] { "EarlyPatcher active", "Loaded 1 patcher type from [EarlyPatcher 1.0.0.0]" },
            Array.Empty<string>(), false),
        new TestCase("Mods", Array.Empty<FixtureFile>(),
            new[] { "2 patcher definition(s) loaded", "Loaded 1 patcher type from [DeleagateRefEditingPrePatcher 1.0.0.0]", "Loaded 1 patcher type from [🍀Patcher 1.0.0.0]", "Loading [BepInEx Resonite Shim 0.9.3]" },
            Array.Empty<string>(), false, PreserveMods: true),
    };
}

sealed class PatcherTestOptions
{
    public string Root { get; init; }
    public string ProfileDir { get; init; }
    public string GameExe { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public string Expect { get; init; } = "new";
    public string Case { get; init; }
    public bool ListOnly { get; init; }
    public bool NoBuild { get; init; }
    public bool StageOnly { get; init; }
    public bool NoRestore { get; init; }
    public bool SkipMods { get; init; }
    public bool SkipUniExample { get; init; }
}
