namespace CodeContextService.Services;

public class SourceControlConnectionInfo
{
    public bool IsGitHub { get; init; }
    public string Token { get; init; }
    public string Org { get; init; }
    public string Owner { get; init; }
    public string Project { get; init; }
    public string Repo { get; init; }

}
