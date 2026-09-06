using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// R6 — the guard that keeps this repository independently buildable and one-way.
/// <para>
/// It is an <b>ALLOWLIST over structure</b>, and that shape is the finding, not a style choice. The
/// obvious guard would be a denylist of forbidden assembly and namespace names, but such a list would
/// have to live in this public repository and would therefore publish exactly the names the rule exists
/// to keep out of it. The guard below never names anything outside this tree. It asserts, instead, that
/// every compile reference and every MSBuild import resolves INSIDE the tracked tree, with one declared
/// and documented exception: the conditional extension-point import, whose file must not exist.
/// </para>
/// <para>
/// It reads the TRACKED tree via <c>git ls-files</c>, not the working directory. That distinction is the
/// point of assertion (a): <c>.gitignore</c> does not stop <c>git add -f</c>, so only the index can say
/// whether a file is actually published.
/// </para>
/// </summary>
public sealed class CommunityBoundaryGuardTests
{
    /// <summary>The one extension point. Declared here so the exception is explicit, never implicit.</summary>
    private const string ExtensionPointImport = "build\\Commercial.targets";

    /// <summary>APIs that would turn explicit composition into arbitrary module loading (M14-MOD-1).</summary>
    private static readonly string[] DynamicLoadingApis =
    [
        "Assembly.Load",
        "Assembly.LoadFrom",
        "Assembly.LoadFile",
        "Assembly.UnsafeLoadFrom",
        "AssemblyLoadContext",
        "Activator.CreateInstanceFrom",
        "Type.GetType(",
        "AppDomain.CurrentDomain.Load"
    ];

    // ---------------------------------------------------------------- (a) nothing tracked at the hook

    [Fact]
    public void R6a_no_tracked_file_exists_at_the_build_extension_point()
    {
        var tracked = TrackedFiles();

        var offenders = tracked
            .Where(path => path.EndsWith("build/Commercial.targets", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("ProModuleRegistrar.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void R6a_the_extension_point_file_is_absent_from_the_working_tree_too()
    {
        // Belt and braces: if it were present but untracked, this repository would still build (the
        // import is conditional), yet a release built from this checkout would not be the tracked source.
        var root = FindRepositoryRoot();
        Assert.False(File.Exists(Path.Combine(root, "build", "Commercial.targets")));
    }

    // ------------------------------------------------- (b) every reference/import resolves in-tree

    [Fact]
    public void R6b_every_project_reference_resolves_inside_this_repository()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var project in BuildFiles(".csproj"))
        {
            var directory = Path.GetDirectoryName(project)!;
            var document = XDocument.Load(project);

            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(
                    directory,
                    include.Replace('\\', Path.DirectorySeparatorChar)));

                if (!IsInside(root, resolved))
                {
                    offenders.Add($"{Path.GetRelativePath(root, project)} -> {include}");
                }
                else if (!File.Exists(resolved))
                {
                    offenders.Add($"{Path.GetRelativePath(root, project)} -> {include} (missing)");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void R6b_the_only_import_of_an_absent_file_is_the_declared_extension_point_and_it_is_conditional()
    {
        var root = FindRepositoryRoot();
        var unconditionalOrUndeclared = new List<string>();

        foreach (var buildFile in BuildFiles(".csproj", ".props", ".targets"))
        {
            var relative = Path.GetRelativePath(root, buildFile);

            foreach (var import in XDocument.Load(buildFile).Descendants("Import"))
            {
                var project = import.Attribute("Project")?.Value ?? string.Empty;
                var condition = import.Attribute("Condition")?.Value ?? string.Empty;

                var isExtensionPoint = project.Replace('/', '\\')
                    .EndsWith(ExtensionPointImport, StringComparison.OrdinalIgnoreCase);

                if (!isExtensionPoint)
                {
                    // Any other import must point at something that is actually here.
                    continue;
                }

                // The declared exception, and it only stays an exception while it is conditional on the
                // file's existence. An unconditional import of an absent file breaks a public clone.
                if (!condition.Contains("Exists(", StringComparison.OrdinalIgnoreCase))
                {
                    unconditionalOrUndeclared.Add($"{relative}: extension-point import is not conditional");
                }
            }
        }

        Assert.Empty(unconditionalOrUndeclared);
    }

    // ------------------------------------------------------------- (c) no dynamic module loading

    [Fact]
    public void R6c_no_source_file_uses_dynamic_assembly_loading()
    {
        // Scope is what COMPILES, not what is tracked. The first version scanned only `git ls-files`, and
        // a counterproof that added Assembly.LoadFrom to a newly created (still untracked) file passed
        // green: the guard never opened the file. Anything under src/ that the build will compile is in
        // scope here, whether or not it has reached the index yet.
        var root = FindRepositoryRoot();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .Select(path => (
                Path: Path.GetRelativePath(root, path).Replace('\\', '/'),
                Source: StripComments(File.ReadAllText(path))))
            .SelectMany(file => DynamicLoadingApis
                .Where(api => file.Source.Contains(api, StringComparison.Ordinal))
                .Select(api => $"{file.Path}: {api}"))
            .ToArray();

        Assert.Empty(offenders);
    }

    // ------------------------------------------------ (d) no PR-triggered workflow sees secrets

    [Fact]
    public void R6d_no_workflow_triggered_by_pull_request_references_secrets()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var relative in TrackedFiles()
            .Where(path => path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)))
        {
            var text = StripYamlComments(
                File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));

            // pull_request_target is deliberately included: it runs with the base repository's secrets.
            var prTriggered = Regex.IsMatch(text, @"^\s{0,4}pull_request(_target)?\s*:", RegexOptions.Multiline);

            // Match how a workflow can actually READ a secret, not the word. The first version of this
            // guard used a plain substring and reported ci.yml, whose only match was the phrase
            // "no secrets." inside a comment - a defect in the instrument, not in the workflow.
            var readsSecret =
                Regex.IsMatch(text, @"\$\{\{[^}]*secrets\.[A-Za-z_]")
                || Regex.IsMatch(text, @"^\s*secrets\s*:\s*inherit\s*$", RegexOptions.Multiline);

            if (prTriggered && readsSecret)
            {
                offenders.Add(relative);
            }
        }

        Assert.Empty(offenders);
    }

    // ----------------------------------------------- (e) the tree builds with no private component

    [Fact]
    public void R6e_the_solution_graph_contains_only_projects_from_this_repository()
    {
        var root = FindRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(root, "ServerMonitor.slnx"));
        var offenders = new List<string>();

        foreach (var project in solution.Descendants("Project"))
        {
            var path = project.Attribute("Path")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(path));

            var resolved = Path.GetFullPath(Path.Combine(
                root, path!.Replace('/', Path.DirectorySeparatorChar)));

            if (!IsInside(root, resolved) || !File.Exists(resolved))
            {
                offenders.Add(path!);
            }
        }

        Assert.Empty(offenders);
        // Sanity: the guard iterated something. An empty solution would pass vacuously.
        Assert.True(solution.Descendants("Project").Count() >= 7);
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Every build file the build itself will read, tracked or not.
    /// <para>
    /// Scope is deliberately the working tree rather than <c>git ls-files</c>. A counterproof that made
    /// the extension-point import unconditional passed green because <c>Directory.Build.targets</c> was
    /// still untracked and the guard never opened it — the same blindness R6c had. What MSBuild reads is
    /// what matters here; the tracked-vs-untracked distinction belongs to assertion (a), which is about
    /// publication, not about the build.
    /// </para>
    /// </summary>
    private static IEnumerable<string> BuildFiles(params string[] extensions)
    {
        var root = FindRepositoryRoot();

        // Scope is what MSBuild reads for THIS solution: the repository-level build files plus src/ and
        // tests/. Walking the whole tree was tried and was wrong in a way worth recording - it swept up
        // unrelated copies of project files sitting in scratch directories and reported them as
        // unresolvable references, which is a false positive, and a guard that cries wolf gets muted.
        var roots = new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") };

        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Concat(roots
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)));

        return files
            .Where(path => extensions.Any(extension =>
                path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInside(string root, string candidate) =>
        candidate.StartsWith(
            root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The files git actually tracks. Deliberately not a directory walk: only the index distinguishes
    /// "ignored" from "published", and <c>git add -f</c> defeats <c>.gitignore</c>.
    /// </summary>
    private static IReadOnlyList<string> TrackedFiles()
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git to enumerate the tracked tree.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        // A guard that cannot run is NOT a passing guard.
        Assert.True(
            process.ExitCode == 0,
            $"git ls-files failed with exit code {process.ExitCode}: {error}");

        var files = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(files);
        return files;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }

    /// <summary>Removes YAML line comments so the scan reads directives, not prose.</summary>
    private static string StripYamlComments(string yaml) =>
        string.Join(
            '\n',
            yaml.Split('\n').Select(line =>
            {
                var hash = line.IndexOf('#');
                return hash < 0 ? line : line[..hash];
            }));

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//.*?$|/\*.*?\*/", string.Empty,
            RegexOptions.Multiline | RegexOptions.Singleline);
}
