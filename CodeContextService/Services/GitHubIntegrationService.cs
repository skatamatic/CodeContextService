using System.Net.Http.Headers;
using LibGit2Sharp;

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
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
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
        req.Headers.Accept.ParseAdd("application/vnd.github.v3.diff");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public async Task<string> CloneRepository(string token, string owner, string repo, string? branch = null)
    {
        var url = $"https://github.com/{owner}/{repo}.git";
        var destDir = Path.Combine(Path.GetTempPath(), $"{repo}-{Guid.NewGuid()}");
        var co = new CloneOptions
        {
            BranchName = branch
        };
        co.FetchOptions.CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
        {
            Username = token,   // GitHub PAT can be used as username with empty pwd or vice‑versa
            Password = string.Empty
        };
        await Task.Run(() => Repository.Clone(url, destDir, co));
        _log.LogInformation("Cloned {Url} to {Dir}", url, destDir);
        return destDir;
    }
}
