using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// A real git repository in a temp directory, built with the <c>git</c> CLI.
/// </summary>
/// <remarks>
/// Every invocation runs with the machine's own <c>~/.gitconfig</c> and system config replaced by
/// empty ones, and with an author identity supplied through the environment. Without that a
/// developer's <c>core.autocrlf</c>, <c>init.templateDir</c>, commit hooks or missing
/// <c>user.email</c> decide whether the suite passes.
/// </remarks>
internal sealed class TestRepository
{
    private TestRepository(string path) => Path = path;

    /// <summary>The working tree (or, for a destination not yet cloned into, where it will be).</summary>
    public string Path { get; }

    /// <summary>A directory inside a fresh temp root that no repository has been created in yet.</summary>
    public static string EmptyDestination(string name = "clone") =>
        System.IO.Path.Combine(Directory.CreateTempSubdirectory().FullName, name);

    /// <summary>
    /// A repository with a commit on the default branch holding "main content", a lightweight tag
    /// <c>v1.0.0</c> on it, and a <c>feature/x</c> branch whose commit holds "feature content".
    /// </summary>
    public static TestRepository CreateOrigin()
    {
        var repository = new TestRepository(Directory.CreateTempSubdirectory().FullName);

        repository.Git("-c", "init.defaultBranch=main", "init", "--quiet", ".");
        repository.Commit("file.txt", "main content", "main commit");
        repository.Git("tag", "v1.0.0");

        repository.Git("checkout", "--quiet", "-b", "feature/x");
        repository.Commit("file.txt", "feature content", "feature commit");

        repository.Git("checkout", "--quiet", "main");

        return repository;
    }

    /// <summary>An existing checkout, so a test can act on a clone the client produced.</summary>
    public static TestRepository At(string path) => new(path);

    /// <summary>Writes <paramref name="content"/> to <paramref name="relativePath"/> and commits it.</summary>
    public void Commit(string relativePath, string content, string message)
    {
        Write(relativePath, content);
        Git("add", "--", relativePath);
        Git("commit", "--quiet", "-m", message);
    }

    public void Write(string relativePath, string content) =>
        File.WriteAllText(System.IO.Path.Combine(Path, relativePath), content);

    public string Read(string relativePath) =>
        File.ReadAllText(System.IO.Path.Combine(Path, relativePath));

    /// <summary>Runs a git command in this repository, failing the test if it doesn't succeed.</summary>
    public string Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in IsolatedEnvironment())
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Task.WaitAll(stdout, stderr);
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} exited with {process.ExitCode}: {stderr.Result}");

        return stdout.Result.Trim();
    }

    /// <summary>
    /// An environment that keeps git away from the machine's own configuration and identity.
    /// </summary>
    /// <remarks>
    /// The config paths point at files that don't exist, which git reads as empty. Also used by the
    /// credential tests, where the whole point is that only the helpers the test configures are
    /// consulted.
    /// </remarks>
    public static Dictionary<string, string?> IsolatedEnvironment()
    {
        var absent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "servicesources-tests-no-such-gitconfig");

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_GLOBAL"] = absent,
            ["GIT_CONFIG_SYSTEM"] = absent,
            ["GIT_AUTHOR_NAME"] = "test",
            ["GIT_AUTHOR_EMAIL"] = "test@test.invalid",
            ["GIT_COMMITTER_NAME"] = "test",
            ["GIT_COMMITTER_EMAIL"] = "test@test.invalid",
            // Nothing may reach out to a human or to the developer's stored credentials.
            ["GIT_ASKPASS"] = null,
            ["SSH_ASKPASS"] = null,
            ["SERVICESOURCES_GIT_USERNAME"] = null,
            ["SERVICESOURCES_GIT_TOKEN"] = null,
        };
    }
}
