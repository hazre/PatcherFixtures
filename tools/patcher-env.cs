#:property TargetFramework=net10.0
#:property Nullable=disable
#:property ImplicitUsings=disable
#:property PublishAot=false
#:property PackAsTool=false
#:package System.CommandLine@2.0.0

// Deterministic patcher test environment (file-based app, .NET 10+).
//
// One command that does everything: clones the loader repo twice (PR base +
// PR head), builds both with the repo's own Cake build (MakeDist), assembles
// two isolated roots, downloads the pinned Thunderstore shim + real mods,
// builds the fixtures, optionally swaps the game-dir bootstrap per side,
// then runs tools/patcher-tests.cs against old (expect=old) and new
// (expect=new) and records exact versions in versions.json.
//
// NOTE on git: scratch clones under --work are ephemeral build inputs, not
// project checkouts. Plain git is used there on purpose (jj never touches
// them, and no git command ever runs inside a project repo).
//
//   dotnet run --file tools/patcher-env.cs -- --only new --case Base

using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

var rootOption = new Option<string>("--root") { Description = "Fixture repo root." };
rootOption.DefaultValueFactory = _ => Directory.GetCurrentDirectory();
var workOption = new Option<string>("--work") { Description = "Scratch root for clones, dists and roots." };
var prOption = new Option<int>("--pr") { Description = "PR number in --loader-repo." };
prOption.DefaultValueFactory = _ => 39;
var loaderRepoOption = new Option<string>("--loader-repo") { Description = "Loader repo (owner/name)." };
loaderRepoOption.DefaultValueFactory = _ => "ResoniteModding/BepisLoader";
var loaderOldOption = new Option<string>("--loader-old") { Description = "Pin old loader commit (default: base branch HEAD)." };
var loaderNewOption = new Option<string>("--loader-new") { Description = "Pin new loader commit (default: PR head)." };
var loaderBaseOption = new Option<string>("--loader-base") { Description = "Base branch in --loader-repo used for old." };
loaderBaseOption.DefaultValueFactory = _ => "master";
var cloverOption = new Option<string>("--clover-version") { Description = "FourLeafClover version." };
cloverOption.DefaultValueFactory = _ => "3.0.1";
var deleagateOption = new Option<string>("--deleagate-version") { Description = "DeleagateRefEditing version." };
deleagateOption.DefaultValueFactory = _ => "1.0.1";
var shimOption = new Option<string>("--shim-version") { Description = "BepInExResoniteShim version." };
shimOption.DefaultValueFactory = _ => "0.9.3";
var gameOption = new Option<string>("--game") { Description = "Resonite.exe path." };
gameOption.DefaultValueFactory = _ => @"C:\Program Files (x86)\Steam\steamapps\common\Resonite\Resonite.exe";
var timeoutOption = new Option<int>("--timeout") { Description = "Seconds per game launch." };
timeoutOption.DefaultValueFactory = _ => 30;
var onlyOption = new Option<string>("--only") { Description = "Run old, new, or both sides." };
onlyOption.DefaultValueFactory = _ => "both";
onlyOption.AcceptOnlyFromAmong("old", "new", "both");
var caseOption = new Option<string>("--case") { Description = "Run a single tester case." };
var skipModsOption = new Option<bool>("--skip-mods") { Description = "Skip mod downloads and the Mods case." };
var unimodShaOption = new Option<string>("--unimod-sha") { Description = "Pin UniModFramework commit for the UniExample case." };
unimodShaOption.DefaultValueFactory = _ => "fb84fff996df15580e4df2747c7e12b68d3a33ac";
var skipUniexampleOption = new Option<bool>("--skip-uniexample") { Description = "Skip the UniModFramework clone and the UniExample case." };
var skipLoaderBuildOption = new Option<bool>("--skip-loader-build") { Description = "Reuse existing clones and dists in --work." };
var noBootstrapSwapOption = new Option<bool>("--no-bootstrap-swap") { Description = "Keep the game-dir BepisLoader for both sides." };

var rootCommand = new RootCommand("Build old/new loaders, fetch mods, and run the patcher matrix in isolation.");
rootCommand.Options.Add(rootOption);
rootCommand.Options.Add(workOption);
rootCommand.Options.Add(prOption);
rootCommand.Options.Add(loaderRepoOption);
rootCommand.Options.Add(loaderOldOption);
rootCommand.Options.Add(loaderNewOption);
rootCommand.Options.Add(loaderBaseOption);
rootCommand.Options.Add(cloverOption);
rootCommand.Options.Add(deleagateOption);
rootCommand.Options.Add(shimOption);
rootCommand.Options.Add(gameOption);
rootCommand.Options.Add(timeoutOption);
rootCommand.Options.Add(onlyOption);
rootCommand.Options.Add(caseOption);
rootCommand.Options.Add(skipModsOption);
rootCommand.Options.Add(unimodShaOption);
rootCommand.Options.Add(skipUniexampleOption);
rootCommand.Options.Add(skipLoaderBuildOption);
rootCommand.Options.Add(noBootstrapSwapOption);
rootCommand.SetAction(result => EnvMain(new EnvOptions
{
    Root = Path.GetFullPath(result.GetValue(rootOption)),
    Work = result.GetValue(workOption),
    Pr = result.GetValue(prOption),
    LoaderRepo = result.GetValue(loaderRepoOption),
    LoaderOld = result.GetValue(loaderOldOption),
    LoaderNew = result.GetValue(loaderNewOption),
    LoaderBase = result.GetValue(loaderBaseOption),
    CloverVersion = result.GetValue(cloverOption),
    DeleagateVersion = result.GetValue(deleagateOption),
    ShimVersion = result.GetValue(shimOption),
    GameExe = result.GetValue(gameOption),
    TimeoutSeconds = result.GetValue(timeoutOption),
    Only = result.GetValue(onlyOption),
    Case = result.GetValue(caseOption),
    SkipMods = result.GetValue(skipModsOption),
    UniModSha = result.GetValue(unimodShaOption),
    SkipUniExample = result.GetValue(skipUniexampleOption),
    SkipLoaderBuild = result.GetValue(skipLoaderBuildOption),
    NoBootstrapSwap = result.GetValue(noBootstrapSwapOption),
}).GetAwaiter().GetResult());
return rootCommand.Parse(args).Invoke();

static async Task<int> EnvMain(EnvOptions o)
{
    o.Work ??= Path.Combine(o.Root, ".test-env");
    var dotnet = DotnetDiscovery.FindDotnet();
    if (dotnet is null) { Console.Error.WriteLine("error: dotnet not found."); return 2; }
    var dotnetDir = dotnet != "dotnet" ? Path.GetDirectoryName(dotnet) : null;
    if (dotnetDir is not null)
    {
        // The loader's Cake build shells bare `dotnet`; make it resolvable
        // for this process and every child (build, tester, game tooling).
        Environment.SetEnvironmentVariable("PATH",
            dotnetDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }
    if (!File.Exists(o.GameExe)) { Console.Error.WriteLine($"error: game exe not found: {o.GameExe}"); return 2; }

    var (oldSha, newSha, prHeadRef) = ResolveLoaderCommits(o);
    Console.WriteLine($"loader old={oldSha} new={newSha} (pr #{o.Pr} {o.LoaderRepo} base {o.LoaderBase} head {prHeadRef})");
    var gameVersion = FileVersionInfo.GetVersionInfo(o.GameExe).ProductVersion;

    var oldClone = Path.Combine(o.Work, "loader-old");
    var newClone = Path.Combine(o.Work, "loader-new");
    var oldRoot = Path.Combine(o.Work, "roots", "old");
    var newRoot = Path.Combine(o.Work, "roots", "new");

    if (!o.SkipLoaderBuild)
    {
        var needOld = o.Only != "new";
        var needNew = o.Only != "old";
        if (needOld)
        {
            CloneFresh($"https://github.com/{o.LoaderRepo}.git", oldClone);
            Checkout(oldClone, oldSha);
            BuildDist(dotnet, oldClone);
        }
        if (needNew)
        {
            if (Directory.Exists(oldClone))
                CloneFresh(oldClone, newClone);
            else
                CloneFresh($"https://github.com/{o.LoaderRepo}.git", newClone);
            if (!string.IsNullOrEmpty(prHeadRef))
                Git(newClone, $"fetch https://github.com/{o.LoaderRepo}.git {prHeadRef}");
            Checkout(newClone, newSha);
            BuildDist(dotnet, newClone);
        }
    }
    if (o.Only != "new")
        AssembleRoot(oldClone, oldRoot);
    if (o.Only != "old")
        AssembleRoot(newClone, newRoot);

    if (!o.SkipMods)
    {
        await FetchMod(o, oldRoot, newRoot, "art0007i", "FourLeafClover", o.CloverVersion);
        await FetchMod(o, oldRoot, newRoot, "eia485", "DeleagateRefEditing", o.DeleagateVersion);
        await FetchMod(o, oldRoot, newRoot, "ResoniteModding", "BepInExResoniteShim", o.ShimVersion);
    }

    Console.WriteLine("Building fixtures ...");
    if (Shell.Run(dotnet, $"build \"{Path.Combine(o.Root, "PatcherFixtures.slnx")}\" -c Debug --nologo -v q", o.Root) != 0)
    {
        Console.Error.WriteLine("error: fixture build failed.");
        return 2;
    }

    if (!o.SkipUniExample)
    {
        // Real-framework consumer: clone pinned UniModFramework, then build
        // UniExample (a ProjectReference builds the framework automatically).
        // Excluded from the slnx on purpose: it only resolves after this clone.
        var unimodClone = Path.Combine(o.Work, "unimod");
        CloneFresh("https://github.com/Nytra/UniModFramework.git", unimodClone);
        Checkout(unimodClone, o.UniModSha);
        Console.WriteLine("Building UniExample against pinned UniModFramework ...");
        if (Shell.Run(dotnet, $"build \"{Path.Combine(o.Root, "UniExample", "UniExample.csproj")}\" -c Debug --nologo -v q", o.Root) != 0)
        {
            Console.Error.WriteLine("error: UniExample build failed.");
            return 2;
        }
    }

    var sides = o.Only == "both" ? new[] { "old", "new" } : new[] { o.Only };
    var results = new System.Collections.Generic.Dictionary<string, int>();
    var gameDir = Path.GetDirectoryName(o.GameExe);
    var swapped = false;
    try
    {
        foreach (var side in sides)
        {
            var sideRoot = side == "old" ? oldRoot : newRoot;
            if (!o.NoBootstrapSwap)
            {
                SwapBootstrap(sideRoot, gameDir, Path.Combine(o.Work, "bootstrap-backup"));
                swapped = true;
            }
            var code = RunTester(dotnet, o, Path.Combine(sideRoot, "BepInEx"), side);
            results[side] = code;
        }
    }
    finally
    {
        if (swapped)
            RestoreBootstrap(gameDir, Path.Combine(o.Work, "bootstrap-backup"));
    }

    var manifest = new
    {
        pr = o.Pr,
        loaderRepo = o.LoaderRepo,
        loaderBase = o.LoaderBase,
        prHeadRef,
        oldSha,
        newSha,
        mods = new { clover = o.CloverVersion, deleagate = o.DeleagateVersion, shim = o.ShimVersion, skipped = o.SkipMods },
        unimod = new { sha = o.UniModSha, skipped = o.SkipUniExample },
        game = new { path = o.GameExe, version = gameVersion },
        dotnet,
        results,
        timestampUtc = DateTime.UtcNow.ToString("o"),
    };
    var manifestPath = Path.Combine(o.Work, "versions.json");
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"manifest -> {manifestPath}");

    var failed = results.Values.Count(c => c != 0);
    Console.WriteLine(failed == 0 ? "ENV PASS: all sides green." : $"ENV FAIL: {failed} side(s) failed.");
    return failed;
}

static (string Old, string New, string PrHeadRef) ResolveLoaderCommits(EnvOptions o)
{
    if (!string.IsNullOrEmpty(o.LoaderOld) && !string.IsNullOrEmpty(o.LoaderNew))
        return (o.LoaderOld, o.LoaderNew, "");
    var repoUrl = $"https://github.com/{o.LoaderRepo}.git";
    var oldSha = !string.IsNullOrEmpty(o.LoaderOld)
        ? o.LoaderOld
        : LsRemote(repoUrl, $"refs/heads/{o.LoaderBase}");
    if (!string.IsNullOrEmpty(o.LoaderNew))
        return (oldSha, o.LoaderNew, "");
    var prHeadRef = $"refs/pull/{o.Pr}/head";
    return (oldSha, LsRemote(repoUrl, prHeadRef), prHeadRef);
}

static string LsRemote(string repoUrl, string reference)
{
    var output = Shell.Capture("git", $"ls-remote \"{repoUrl}\" \"{reference}\"");
    var sha = output.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (string.IsNullOrEmpty(sha))
        throw new EnvFailure($"git ls-remote found no {reference} in {repoUrl}");
    return sha.Trim();
}

static void CloneFresh(string source, string dest)
{
    // Ephemeral scratch clone (see file header note on git usage).
    // Reuses an existing clone when present, then pins the exact commit
    // at the call site, so the tree is deterministic either way.
    if (!Directory.Exists(Path.Combine(dest, ".git")))
    {
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        if (Shell.Run("git", $"clone \"{source}\" \"{dest}\"", Directory.GetCurrentDirectory()) != 0)
            throw new EnvFailure($"git clone failed: {source}");
    }
    else if (Shell.Run("git", $"-C \"{dest}\" fetch \"{source}\" +refs/heads/*:refs/remotes/scratch/* --tags --force", Directory.GetCurrentDirectory()) != 0)
    {
        throw new EnvFailure($"git fetch failed in {dest}");
    }
}

static void Git(string repo, string arguments)
{
    if (Shell.Run("git", $"-C \"{repo}\" {arguments}", Directory.GetCurrentDirectory()) != 0)
        throw new EnvFailure($"git {arguments} failed in {repo}");
}

static void Checkout(string repo, string sha)
{
    Git(repo, "checkout --detach " + sha);
    // Submodules (HarmonyX) are pinned per commit; sync them for a hermetic tree.
    Git(repo, "submodule update --init --recursive");
}

static void BuildDist(string dotnet, string clone)
{
    Console.WriteLine($"Building dist in {clone} ...");
    if (Shell.Stream(dotnet, "run --project build/Build.csproj -- --target MakeDist", clone) != 0)
        throw new EnvFailure($"MakeDist failed in {clone}");
}

static void AssembleRoot(string clone, string root)
{
    var dist = Path.Combine(clone, "bin", "dist");
    var target = Directory.GetDirectories(dist, "BepisLoader-*").FirstOrDefault(d => Directory.Exists(Path.Combine(d, "BepInEx")));
    if (target is null)
        throw new EnvFailure($"no BepisLoader dist found in {dist}");
    Console.WriteLine($"Assembling {root} from {target} ...");
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
    CopyDir(target, root);
    foreach (var sub in new[] { "patchers", "plugins", "config", "cache" })
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", sub));
}

static async Task FetchMod(EnvOptions o, string oldRoot, string newRoot, string owner, string name, string version)
{
    var id = $"{owner}-{name}";
    var url = $"https://thunderstore.io/package/download/{owner}/{name}/{version}/";
    var zipPath = Path.Combine(o.Work, "downloads", $"{id}-{version}.zip");
    var extractDir = Path.Combine(o.Work, "packages", $"{id}-{version}");
    Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
    if (!File.Exists(zipPath))
    {
        Console.WriteLine($"Downloading {id} {version} ...");
        using var response = await EnvHttp.Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(zipPath);
        await response.Content.CopyToAsync(fs);
    }
    if (Directory.Exists(extractDir))
        Directory.Delete(extractDir, recursive: true);
    ZipFile.ExtractToDirectory(zipPath, extractDir);
    foreach (var side in new[] { oldRoot, newRoot })
    {
        if (!Directory.Exists(side))
            continue;
        foreach (var sub in new[] { "patchers", "plugins", "config" })
        {
            var src = Path.Combine(extractDir, sub);
            if (Directory.Exists(src))
                MergeDir(src, Path.Combine(side, "BepInEx", sub));
        }
    }
}

static void SwapBootstrap(string sideRoot, string gameDir, string backupDir)
{
    Directory.CreateDirectory(backupDir);
    foreach (var file in Bootstrap.Files)
    {
        var gameFile = Path.Combine(gameDir, file);
        var sideFile = Path.Combine(sideRoot, file);
        if (!File.Exists(gameFile))
            throw new EnvFailure($"game-dir bootstrap file missing: {gameFile}");
        var backupFile = Path.Combine(backupDir, file);
        if (!File.Exists(backupFile))
            File.Copy(gameFile, backupFile);
        if (File.Exists(sideFile))
            File.Copy(sideFile, gameFile, overwrite: true);
    }
    Console.WriteLine($"bootstrap swapped from {sideRoot}");
}

static void RestoreBootstrap(string gameDir, string backupDir)
{
    foreach (var file in Bootstrap.Files)
    {
        var backupFile = Path.Combine(backupDir, file);
        if (File.Exists(backupFile))
            File.Copy(backupFile, Path.Combine(gameDir, file), overwrite: true);
    }
    Console.WriteLine("bootstrap restored.");
}

static int RunTester(string dotnet, EnvOptions o, string profileDir, string side)
{
    var args = $"run --file \"{Path.Combine(o.Root, "tools", "patcher-tests.cs")}\" -- --root \"{o.Root}\" --profile-dir \"{profileDir}\" --game \"{o.GameExe}\" --expect {(side == "old" ? "old" : "new")} --timeout {o.TimeoutSeconds} --no-build";
    if (o.Case is not null) args += $" --case {o.Case}";
    if (o.SkipMods) args += " --skip-mods";
    if (o.SkipUniExample) args += " --skip-uniexample";
    Console.WriteLine($"=== side {side} ===");
    return Shell.Stream(dotnet, args, o.Root);
}

static void CopyDir(string source, string dest)
{
    Directory.CreateDirectory(dest);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    foreach (var dir in Directory.GetDirectories(source))
        CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)));
}

static void MergeDir(string source, string dest)
{
    CopyDir(source, dest);
}

sealed class EnvFailure : Exception
{
    public EnvFailure(string message) : base(message) { }
}

static class Bootstrap
{
    public static readonly string[] Files = new[]
    {
        "BepisLoader.dll", "BepisLoader.deps.json", "BepisLoader.runtimeconfig.json", "BepisLoader.pdb",
    };
}

static class EnvHttp
{
    public static readonly HttpClient Client = new();
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

    public static string Capture(string exe, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p is null) throw new EnvFailure($"could not start {exe}");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new EnvFailure($"{exe} {arguments} failed: {p.StandardError.ReadToEnd()}");
        return output;
    }

    public static int Stream(string exe, string arguments, string workDir)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
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

sealed class EnvOptions
{
    public string Root { get; init; }
    public string Work { get; set; }
    public int Pr { get; init; } = 39;
    public string LoaderRepo { get; init; }
    public string LoaderOld { get; init; }
    public string LoaderNew { get; init; }
    public string LoaderBase { get; init; } = "master";
    public string CloverVersion { get; init; }
    public string DeleagateVersion { get; init; }
    public string ShimVersion { get; init; }
    public string GameExe { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public string Only { get; init; } = "both";
    public string Case { get; init; }
    public bool SkipMods { get; init; }
    public string UniModSha { get; init; }
    public bool SkipUniExample { get; init; }    public bool SkipLoaderBuild { get; init; }
    public bool NoBootstrapSwap { get; init; }
}
