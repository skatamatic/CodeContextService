using LibGit2Sharp;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodeContextService.Services;

public class AzureDevOpsIntegrationService : ISourceControlIntegrationService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<AzureDevOpsIntegrationService> _log;

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


    public async Task<string> GetPullRequestDiffAsync(SourceControlConnectionString cs, int prId)
    {
        try
        {
            var client = CreateClient(cs);

            var iterationsUrl = $"{cs.Org}/{cs.Project}/_apis/git/repositories/{cs.Repo}/pullRequests/{prId}/iterations?api-version=7.0";
            var iterationsResp = await client.GetAsync(iterationsUrl);
            iterationsResp.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await iterationsResp.Content.ReadAsStringAsync());
            var lastIterationId = json.RootElement.GetProperty("value").EnumerateArray().Last().GetProperty("id").GetInt32();

            var changesUrl = $"{cs.Org}/{cs.Project}/_apis/git/repositories/{cs.Repo}/pullRequests/{prId}/iterations/{lastIterationId}/changes?api-version=7.0";
            var diffResp = await client.GetAsync(changesUrl);
            diffResp.EnsureSuccessStatusCode();

            var diffJson = JsonDocument.Parse(await diffResp.Content.ReadAsStringAsync());
            var sb = new StringBuilder();

            foreach (var fileDiff in diffJson.RootElement.GetProperty("changeEntries").EnumerateArray())
            {
                sb.Append(ConvertAdoDiffToUnified(fileDiff));
            }

            return sb.ToString();
        }
        catch(Exception ex)
        {
            return string.Empty;
        }
    }

    private string ConvertAdoDiffToUnified(JsonElement fileDiff)
    {
        var sb = new StringBuilder();
        var path = fileDiff.GetProperty("item").GetProperty("path").GetString()!;
        sb.AppendLine($"diff --git a/{path} b/{path}");
        sb.AppendLine($"--- a/{path}");
        sb.AppendLine($"+++ b/{path}");

        if (!fileDiff.TryGetProperty("diff", out var diff))
            return sb.ToString(); // No diff content, maybe a rename or binary change

        foreach (var block in diff.GetProperty("lineDiffBlocks").EnumerateArray())
        {
            var oStart = block.GetProperty("originalStartLine").GetInt32();
            var oCount = block.GetProperty("originalLineCount").GetInt32();
            var mStart = block.GetProperty("modifiedStartLine").GetInt32();
            var mCount = block.GetProperty("modifiedLineCount").GetInt32();

            sb.AppendLine($"@@ -{oStart},{oCount} +{mStart},{mCount} @@");

            foreach (var mLine in block.GetProperty("mLines").EnumerateArray())
            {
                var lineText = mLine.GetProperty("line").GetString();
                var type = mLine.GetProperty("lineType").GetString();
                string prefix = type switch
                {
                    "add" => "+",
                    "delete" => "-",
                    _ => " "
                };
                sb.AppendLine($"{prefix}{lineText}");
            }
        }

        return sb.ToString();
    }

    public async Task<string> GetPullRequestHeadBranchAsync(SourceControlConnectionString cs, int prId)
    {
        var client = CreateClient(cs);
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

    public async Task<string> CloneRepository(SourceControlConnectionString cs, string? branch = "master")
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
