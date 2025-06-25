using CodeContextService.Model;

namespace CodeContextService.Services
{
    public interface ISourceControlIntegrationService
    {
        Task<bool> ValidateTokenAsync(SourceControlConnectionString cs);
        Task<UnifiedDiff> GetUnifiedDiff(SourceControlConnectionString cs, int prNumber);
        Task<string> CloneRepository(SourceControlConnectionString cs, string? branch = null);
    }
}
