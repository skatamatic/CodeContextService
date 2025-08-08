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

        private ISourceControlIntegrationService GetService(SourceControlConnectionInfo cs)
        {
            if (cs.IsGitHub)
                return _gitHub;
            else
                return _ado;
        }

        public Task<bool> ValidateTokenAsync(SourceControlConnectionInfo cs)
            => GetService(cs).ValidateTokenAsync(cs);

        public Task<string> CloneRepository(SourceControlConnectionInfo cs, string? branch = null)
            => GetService(cs).CloneRepository(cs, branch);

        public Task<UnifiedDiff> GetUnifiedDiff(SourceControlConnectionInfo cs, int prNumber)
            => GetService(cs).GetUnifiedDiff(cs, prNumber);
    }
}
