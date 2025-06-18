namespace CodeContextService.Services
{
    public class SourceControlIntegrationService : ISourceControlIntegrationService
    {
        private readonly GitHubIntegrationService _gitHub;
        private readonly AzureDevOpsIntegrationService _ado;

        public SourceControlIntegrationService(GitHubIntegrationService gitHub, AzureDevOpsIntegrationService ado)
        {
            _gitHub = gitHub;
            _ado = ado;
        }

        private ISourceControlIntegrationService GetService(SourceControlConnectionString cs)
        {
            if (cs.IsGitHub)
                return _gitHub;
            else
                return _ado;

        }

        public Task<bool> ValidateTokenAsync(SourceControlConnectionString cs)
            => GetService(cs).ValidateTokenAsync(cs);

        public Task<string> GetPullRequestDiffAsync(SourceControlConnectionString cs, int prNumber)
            => GetService(cs).GetPullRequestDiffAsync(cs, prNumber);

        public Task<string> GetPullRequestHeadBranchAsync(SourceControlConnectionString cs, int prNumber)
            => GetService(cs).GetPullRequestHeadBranchAsync(cs, prNumber);

        public Task<string> CloneRepository(SourceControlConnectionString cs, string? branch = null)
            => GetService(cs).CloneRepository(cs, branch);
    }
}
