namespace CodeContextService.Services
{
    public interface ISourceControlIntegrationService
    {
        Task<bool> ValidateTokenAsync(SourceControlConnectionString cs);
        Task<string> GetPullRequestDiffAsync(SourceControlConnectionString cs, int prNumber);
        Task<string> GetPullRequestHeadBranchAsync(SourceControlConnectionString cs, int prNumber);
        Task<string> CloneRepository(SourceControlConnectionString cs, string? branch = null);
    }
}
