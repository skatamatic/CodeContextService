using CodeContextService.Model;

namespace CodeContextService.Services
{
    public interface ISourceControlIntegrationService
    {
        Task<bool> ValidateTokenAsync(SourceControlConnectionInfo cs);
        Task<UnifiedDiff> GetUnifiedDiff(SourceControlConnectionInfo cs, int prNumber);
        Task<string> CloneRepository(SourceControlConnectionInfo cs, string? branch = null);
    }
}
