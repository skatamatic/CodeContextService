using CodeContextService.Model;
using LibGit2Sharp;
using Microsoft.Build.Tasks;
using Newtonsoft.Json.Linq;
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

    private HttpClient CreateClient(SourceControlConnectionString cs)
    {
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri($"https://dev.azure.com/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{cs.Token}")));
        return client;
    }

    public async Task<bool> ValidateTokenAsync(SourceControlConnectionString cs)
    {
        var client = CreateClient(cs);
        var resp = await client.GetAsync($"{cs.Org}/_apis/projects?api-version=2.0");
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Extracts the short name (last part) of a branch reference.
    /// For example, "refs/heads/feature/xyz" becomes "xyz".
    /// </summary>
    private static string GetShortBranchName(string fullRef)
    {
        if (string.IsNullOrEmpty(fullRef))
            return string.Empty;

        var parts = fullRef.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : fullRef;
    }

    public async Task<string> CloneRepository(SourceControlConnectionString cs, string? branch = "master")
    {
        var url = $"https://dev.azure.com/{cs.Org}/{cs.Project}/_git/{cs.Repo}";
        var destDir = Path.Combine(Path.GetTempPath(), $"{cs.Repo}-{Guid.NewGuid()}");
        var co = new CloneOptions(new FetchOptions()
        {
            CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials { Username = cs.Token, Password = string.Empty }
        });

        if (!string.IsNullOrEmpty(branch))
        {
            _log.LogInformation("Setting branch to '{Branch}'", branch);
            co.BranchName = branch;
        }


        _log.LogInformation("Cloning {Url} to {Dir}", url, destDir);
        await Task.Run(() => Repository.Clone(url, destDir, co));
        _repo = new Repository(destDir);
        return destDir;
    }

    public async Task<UnifiedDiff> GetUnifiedDiff(SourceControlConnectionString cs, int prNumber)
    {
        var client = CreateClient(cs);
        var url = $"{cs.Org}/{cs.Project}/_apis/git/repositories/{cs.Repo}/pullRequests/{prNumber}?api-version=7.0";
        _log.LogInformation("Fetching PR head branch from {Url}", url);
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var content = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(content);
        var sourceBranch = doc.RootElement.GetProperty("sourceRefName").GetString();
        var targetBranch = doc.RootElement.GetProperty("targetRefName").GetString();

        var path = await CloneRepository(cs, GetShortBranchName(targetBranch));

        // Get the tips (latest commits) of the two branches
        var branch1Commit = _repo.Branches.Where(s => GetShortBranchName(s.CanonicalName) == GetShortBranchName(sourceBranch)).First().Tip;
        var branch2Commit = _repo.Branches.Where(s => GetShortBranchName(s.CanonicalName) == GetShortBranchName(targetBranch)).First().Tip;

        // Get the trees for each commit
        Tree branch1Tree = branch1Commit.Tree;
        Tree branch2Tree = branch2Commit.Tree;

        // Generate the diff between the trees
        Patch patch = _repo.Diff.Compare<Patch>(branch1Tree, branch2Tree);

        if (string.IsNullOrEmpty(sourceBranch))
            throw new InvalidOperationException($"Could not determine source branch for PR {prNumber}");

        return new UnifiedDiff(patch.Content, path);
    }
}
