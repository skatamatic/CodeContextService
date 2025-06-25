using CodeContextService.Model;

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

        public Task<string> CloneRepository(SourceControlConnectionString cs, string? branch = null)
            => GetService(cs).CloneRepository(cs, branch);

        public Task<UnifiedDiff> GetUnifiedDiff(SourceControlConnectionString cs, int prNumber)
            => GetService(cs).GetUnifiedDiff(cs, prNumber);
    }
}
