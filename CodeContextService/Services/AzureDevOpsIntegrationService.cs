using LibGit2Sharp;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodeContextService.Services;

public class AzureDevOpsIntegrationService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<AzureDevOpsIntegrationService> _log;

    public AzureDevOpsIntegrationService(IHttpClientFactory factory, ILogger<AzureDevOpsIntegrationService> log)
    {
        _factory = factory;
        _log = log;
    }

    private HttpClient CreateClient(string token)
    {
        var client = _factory.CreateClient();
        client.BaseAddress = new Uri("https://dev.azure.com/");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{token}")));
        return client;
    }

    public Task<bool> ValidateTokenAsync(AdoConnectionString cs)
        => ValidateTokenAsync(cs.Token);

    private async Task<bool> ValidateTokenAsync(string token)
    {
        var client = CreateClient(token);
        var resp = await client.GetAsync("_apis/profile/profiles/me?api-version=7.0");
        return resp.IsSuccessStatusCode;
    }

    public async Task<string> GetPullRequestDiffAsync(AdoConnectionString cs, int prId)
    {
        var client = CreateClient(cs.Token);

        var iterationsUrl = $"{cs.Org}/{cs.Project}/_apis/git/repositories/{cs.Repo}/pullRequests/{prId}/iterations?api-version=7.0";
        var iterationsResp = await client.GetAsync(iterationsUrl);
        iterationsResp.EnsureSuccessStatusCode();

        var json = JsonDocument.Parse(await iterationsResp.Content.ReadAsStringAsync());
        var lastIterationId = json.RootElement.GetProperty("value").EnumerateArray().Last().GetProperty("id").GetInt32();

        var changesUrl = $"{cs.Org}/{cs.Project}/_apis/git/repositories/{cs.Repo}/pullRequests/{prId}/iterations/{lastIterationId}/changes?api-version=7.0";
        var diffResp = await client.GetAsync(changesUrl);
        diffResp.EnsureSuccessStatusCode();
        return await diffResp.Content.ReadAsStringAsync();
    }

    public async Task<string> GetPullRequestHeadBranchAsync(AdoConnectionString cs, int prId)
    {
        var client = CreateClient(cs.Token);
        var url = $"{cs.Org}/{cs.Project}/_apis/git/repositories/{cs.Repo}/pullRequests/{prId}?api-version=7.0";
        _log.LogInformation("Fetching PR head branch from {Url}", url);
        var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var content = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(content);
        var refName = doc.RootElement.GetProperty("sourceRefName").GetString();
        var branch = refName?.Replace("refs/heads/", "");

        if (string.IsNullOrEmpty(branch))
            throw new InvalidOperationException($"Could not determine source branch for PR {prId}");

        return branch;
    }

    public async Task<string> CloneRepository(AdoConnectionString cs, string? branch = null)
    {
        var url = $"https://{cs.Org}@dev.azure.com/{cs.Org}/{cs.Project}/_git/{cs.Repo}";
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
        return destDir;
    }
}
