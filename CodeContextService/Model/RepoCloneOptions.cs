namespace CodeContextService.Model;

/// <summary>
/// Options for repository clone location and behavior.
/// In GCP (e.g. Cloud Run), use ephemeral temp; set REPO_CLONE_ROOT to control where clones go.
/// </summary>
public class RepoCloneOptions
{
    public const string SectionName = "RepoClone";

    /// <summary>
    /// Root directory for cloning repositories. Should be on ephemeral/local disk in production.
    /// Override via env: REPO_CLONE_ROOT (or RepoClone__CloneRootPath).
    /// If not set, uses Path.GetTempPath().
    /// </summary>
    public string? CloneRootPath { get; set; }
}
