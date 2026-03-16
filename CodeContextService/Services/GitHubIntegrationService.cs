using System.Net.Http.Headers;
using System.Net;
using CodeContextService.Model;
using LibGit2Sharp;
using Microsoft.Extensions.Options;

namespace CodeContextService.Services;

public class GitHubIntegrationService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<GitHubIntegrationService> _log;
    private readonly string _cloneRoot;

    public GitHubIntegrationService(
        IHttpClientFactory factory,
        ILogger<GitHubIntegrationService> log,
        IOptions<RepoCloneOptions> cloneOptions)
    {
        _factory = factory;
        _log = log;
        _cloneRoot = string.IsNullOrWhiteSpace(cloneOptions.Value.CloneRootPath)
            ? Path.GetTempPath()
            : cloneOptions.Value.CloneRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private HttpClient CreateClient(string token)
    {
        var client = _factory.CreateClient("github-no-redirect");
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BlazorCodeAnalyzer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void ThrowIfRedirect(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
        {
            var location = resp.Headers.Location?.ToString() ?? "(none)";
            throw new InvalidOperationException(
                $"GitHub API returned redirect ({(int)resp.StatusCode}) to '{location}'. " +
                "This usually means non-GitHub inputs, SSO/browser auth redirects, or proxy/auth replay. " +
                "Use a GitHub PAT and GitHub owner/repo values."
            );
        }
    }

    public async Task<bool> ValidateTokenAsync(string token)
    {
        var client = CreateClient(token);
        var resp = await client.GetAsync("user");
        ThrowIfRedirect(resp);
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
        ThrowIfRedirect(resp);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public async Task<string> CloneRepository(string token, string owner, string repo, string? branch = null)
    {
        var url = $"https://github.com/{owner}/{repo}.git";
        var destDir = Path.Combine(_cloneRoot, $"{repo}-{Guid.NewGuid()}");
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
