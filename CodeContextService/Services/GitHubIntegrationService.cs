using LibGit2Sharp;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodeContextService.Services;

public class GitHubIntegrationService : ISourceControlIntegrationService
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
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public Task<bool> ValidateTokenAsync(SourceControlConnectionString cs)
        => ValidateTokenAsync(cs.Token);

    private async Task<bool> ValidateTokenAsync(string token)
    {
        var client = CreateClient(token);
        var resp = await client.GetAsync("user");
        return resp.IsSuccessStatusCode;
    }

    public async Task<string> GetPullRequestDiffAsync(SourceControlConnectionString cs, int prNumber)
    {
        var client = CreateClient(cs.Token);
        var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{cs.Org}/{cs.Repo}/pulls/{prNumber}");
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.v3.diff");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public async Task<string> GetPullRequestHeadBranchAsync(SourceControlConnectionString cs, int prNumber)
    {
        var client = CreateClient(cs.Token);
        var url = $"repos/{cs.Org}/{cs.Repo}/pulls/{prNumber}";
        _log.LogInformation("Fetching PR details from {Url}", url);
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var content = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (root.TryGetProperty("head", out var head) &&
            head.TryGetProperty("ref", out var @ref))
        {
            var branchName = @ref.GetString();
            if (!string.IsNullOrEmpty(branchName))
            {
                _log.LogInformation("Found PR head branch: {BranchName}", branchName);
                return branchName;
            }
        }

        _log.LogError("Could not find head branch name in PR {PrNumber}", prNumber);
        throw new InvalidOperationException($"Could not determine head branch for PR {prNumber}");
    }

    public async Task<string> CloneRepository(SourceControlConnectionString cs, string? branch = null)
    {
        var url = $"https://github.com/{cs.Org}/{cs.Repo}.git";
        var destDir = Path.Combine(Path.GetTempPath(), $"{cs.Repo}-{Guid.NewGuid()}");
        var co = new CloneOptions();

        if (!string.IsNullOrEmpty(branch))
        {
            _log.LogInformation("Setting branch to '{Branch}'", branch);
            co.BranchName = branch;
        }

        co.FetchOptions.CredentialsProvider = (_, _, _) =>
            new UsernamePasswordCredentials { Username = cs.Token, Password = string.Empty };

        _log.LogInformation("Cloning {Url} to {Dir}", url, destDir);
        await Task.Run(() => Repository.Clone(url, destDir, co));
        return destDir;
    }
}
