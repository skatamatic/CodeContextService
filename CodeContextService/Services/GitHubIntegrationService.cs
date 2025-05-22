using System.Net.Http.Headers;
using System.Text.Json; // Added for JSON parsing
using LibGit2Sharp;
using Microsoft.Extensions.Logging; // Assuming ILogger is from here

namespace CodeContextService.Services;

public class GitHubIntegrationService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<GitHubIntegrationService> _log;

    public GitHubIntegrationService(IHttpClientFactory factory, ILogger<GitHubIntegrationService> log)
    {
        _factory = factory;
        _log = log;
    }

    private HttpClient CreateClient(string token)
    {
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BlazorCodeAnalyzer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json"); // Standard JSON for PR details
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<bool> ValidateTokenAsync(string token)
    {
        var client = CreateClient(token);
        var resp = await client.GetAsync("user");
        return resp.IsSuccessStatusCode;
    }

    public async Task<string> GetPullRequestDiffAsync(string token, string owner, string repo, int prNumber)
    {
        var client = CreateClient(token);
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{owner}/{repo}/pulls/{prNumber}");
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.v3.diff"); // Specific header for diff
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    // New method to get the PR's head branch
    public async Task<string> GetPullRequestHeadBranchAsync(string token, string owner, string repo, int prNumber)
    {
        var client = CreateClient(token); // Uses standard JSON accept header by default
        var url = $"repos/{owner}/{repo}/pulls/{prNumber}";
        _log.LogInformation("Fetching PR details from {Url} to get head branch", url);
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var content = await resp.Content.ReadAsStringAsync();

        // Parse the JSON to get the head branch name
        using (JsonDocument doc = JsonDocument.Parse(content))
        {
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("head", out JsonElement headElement) &&
                headElement.TryGetProperty("ref", out JsonElement refElement))
            {
                var branchName = refElement.GetString();
                if (!string.IsNullOrEmpty(branchName))
                {
                    _log.LogInformation("Found PR head branch: {BranchName}", branchName);
                    return branchName;
                }
            }
        }
        _log.LogError("Could not find head branch name in PR details for PR {PrNumber} in {Owner}/{Repo}", prNumber, owner, repo);
        throw new InvalidOperationException($"Could not determine head branch for PR {prNumber}");
    }

    public async Task<string> CloneRepository(string token, string owner, string repo, string? branch = null)
    {
        var url = $"https://github.com/{owner}/{repo}.git";
        var destDir = Path.Combine(Path.GetTempPath(), $"{repo}-{Guid.NewGuid()}");
        var co = new CloneOptions();

        if (!string.IsNullOrEmpty(branch))
        {
            _log.LogInformation("Setting branch to '{Branch}' for cloning", branch);
            co.BranchName = branch;
        }
        else
        {
            _log.LogInformation("No specific branch provided, cloning default branch.");
        }

        co.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
        {
            Username = token, // GitHub PAT can be used as username with empty pwd or vice-versa
            Password = string.Empty
        };

        _log.LogInformation("Attempting to clone {Url} (Branch: {BranchName}) to {Dir}", url, branch ?? "default", destDir);
        await Task.Run(() => Repository.Clone(url, destDir, co));
        _log.LogInformation("Cloned {Url} to {Dir}", url, destDir);
        return destDir;
    }
}