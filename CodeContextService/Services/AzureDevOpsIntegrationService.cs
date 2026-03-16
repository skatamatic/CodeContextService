using CodeContextService.Model;
using LibGit2Sharp;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeContextService.Services;

public class AzureDevOpsIntegrationService : ISourceControlIntegrationService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<AzureDevOpsIntegrationService> _log;
    private Repository? _repo;

    public AzureDevOpsIntegrationService(IHttpClientFactory factory, ILogger<AzureDevOpsIntegrationService> log)
    {
        _factory = factory;
        _log = log;
    }

    private HttpClient CreateClient(SourceControlConnectionInfo cs)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{cs.Token}"))
        );
        return client;
    }

    public async Task<bool> ValidateTokenAsync(SourceControlConnectionInfo cs)
    {
        var client = CreateClient(cs);
        var resp = await client.GetAsync($"{cs.Org}/_apis/projects?api-version=2.0");
        return resp.IsSuccessStatusCode;
    }

    // ---- Helpers ----

    // Keep full ref if present; if a short branch name/path is provided (e.g., "feature/x"),
    // promote it to "refs/heads/feature/x".
    private static string NormalizeToHeads(string? refName)
    {
        if (string.IsNullOrWhiteSpace(refName)) return string.Empty;
        var r = refName.Trim();

        if (r.StartsWith("refs/", StringComparison.OrdinalIgnoreCase))
            return r;

        // e.g. "feature/foo" or "main"
        return $"refs/heads/{r.TrimStart('/')}";
    }

    // Convert canonical to remote-tracking branch name (e.g., refs/heads/feature/x -> origin/feature/x)
    private static string ToOriginRemoteName(string canonicalHeadRef)
    {
        var suffix = canonicalHeadRef.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? canonicalHeadRef.Substring("refs/heads/".Length)
            : canonicalHeadRef.TrimStart('/');
        return $"origin/{suffix}";
    }

    // Fetch a specific head ref into a remote-tracking branch (origin/<branchPath>) and return that Branch.
    private Branch FetchRemoteHead(Repository repo, SourceControlConnectionInfo cs, string canonicalHeadRef)
    {
        var remote = repo.Network.Remotes["origin"]
                     ?? throw new InvalidOperationException("Remote 'origin' not found after clone.");

        var fetchOptions = new FetchOptions
        {
            CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials { Username = cs.Token, Password = string.Empty }
        };

        // Map the single head ref into a remote-tracking ref under refs/remotes/origin/...
        // e.g. +refs/heads/feature/x:refs/remotes/origin/feature/x
        var refspec = $"+{canonicalHeadRef}:refs/remotes/{ToOriginRemoteName(canonicalHeadRef)}";

        _log.LogInformation("Fetching ref {Refspec}", refspec);
        Commands.Fetch(repo, remote.Name, new[] { refspec }, fetchOptions, logMessage: null);

        var originBranchName = ToOriginRemoteName(canonicalHeadRef); // "origin/feature/x"
        var remoteBranch = repo.Branches[originBranchName]
                           ?? throw new InvalidOperationException($"Remote branch '{originBranchName}' not found after fetch.");

        return remoteBranch;
    }

    // Ensure we end up with the working copy checked out to the PR source branch tip.
    private Branch EnsureLocalBranchCheckedOut(Repository repo, Branch remoteBranch)
    {
        // remoteBranch.FriendlyName like "origin/feature/foo" → local "feature/foo"
        var localName = remoteBranch.FriendlyName.StartsWith("origin/")
            ? remoteBranch.FriendlyName.Substring("origin/".Length)
            : remoteBranch.FriendlyName;

        var local = repo.Branches[localName];
        if (local is null)
        {
            _log.LogInformation("Creating local branch {Branch} at {Commit}", localName, remoteBranch.Tip.Sha);
            local = repo.CreateBranch(localName, remoteBranch.Tip);
        }
        else
        {
            _log.LogInformation("Updating local branch {Branch} to {Commit}", localName, remoteBranch.Tip.Sha);
            // Move the branch pointer to the new tip if needed
            repo.Branches.Remove(local);
            local = repo.CreateBranch(localName, remoteBranch.Tip);
        }

        // Set tracking information (optional but nice)
        try
        {
            repo.Branches.Update(local,
                b => b.TrackedBranch = remoteBranch.CanonicalName,
                b => b.Remote = "origin");
        }
        catch
        {
            // Older libgit2sharp versions may not support updating both; non-fatal.
        }

        // Checkout local branch and hard reset so worktree matches remote tip exactly
        _log.LogInformation("Checking out {Branch}", localName);
        Commands.Checkout(repo, local, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        repo.Reset(ResetMode.Hard, remoteBranch.Tip);

        return local;
    }

    // ---- Clone & Diff ----

    public async Task<string> CloneRepository(SourceControlConnectionInfo cs, string? branch = null)
    {
        var url = $"https://{cs.Org}/{cs.Project}/_git/{cs.Repo}";
        var destDir = Path.Combine(Path.GetTempPath(), $"{cs.Repo}-{Guid.NewGuid()}");

        var co = new CloneOptions();
        co.FetchOptions.CredentialsProvider = (_, _, _) =>
                    new UsernamePasswordCredentials { Username = cs.Token, Password = string.Empty };

        _log.LogInformation("Cloning {Url} to {Dir}", url, destDir);
        await Task.Run(() => Repository.Clone(url, destDir, co));
        _repo = new Repository(destDir);
        return destDir;
    }

    public async Task<UnifiedDiff> GetUnifiedDiff(SourceControlConnectionInfo cs, int prNumber)
    {
        var client = CreateClient(cs);
        var url = $"https://{cs.Org}/{cs.Project}/_apis/git/repositories/{cs.Repo}/pullRequests/{prNumber}?api-version=7.0";
        _log.LogInformation("Fetching PR from {Url}", url);

        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var content = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(content);

        var sourceRefRaw = doc.RootElement.GetProperty("sourceRefName").GetString();
        var targetRefRaw = doc.RootElement.GetProperty("targetRefName").GetString();

        if (string.IsNullOrWhiteSpace(sourceRefRaw))
            throw new InvalidOperationException($"Could not determine source branch for PR {prNumber}");
        if (string.IsNullOrWhiteSpace(targetRefRaw))
            throw new InvalidOperationException($"Could not determine target branch for PR {prNumber}");

        var sourceRef = NormalizeToHeads(sourceRefRaw); // e.g., refs/heads/feature/foo
        var targetRef = NormalizeToHeads(targetRefRaw); // e.g., refs/heads/develop

        // Clone the repo (default branch); we’ll fetch the specific refs next.
        var path = await CloneRepository(cs, branch: null);
        if (_repo is null) throw new InvalidOperationException("Repository not initialized after clone.");

        // Ensure both refs are present as remote-tracking branches under origin/...
        var sourceRemoteBranch = FetchRemoteHead(_repo, cs, sourceRef);
        var targetRemoteBranch = FetchRemoteHead(_repo, cs, targetRef);

        // Leave the local repo on the PR SOURCE branch (working copy checked out to source tip)
        EnsureLocalBranchCheckedOut(_repo, sourceRemoteBranch);

        // Compute the diff between target and source tips
        var sourceTree = sourceRemoteBranch.Tip.Tree;
        var targetTree = targetRemoteBranch.Tip.Tree;
        Patch patch = _repo.Diff.Compare<Patch>(targetTree, sourceTree);

        return new UnifiedDiff(patch.Content, path);
    }
}
