using System.IO.Compression;
using Microsoft.Extensions.Options;
using RoslynTools.Analyzer;
using CodeContextService.Model;

namespace CodeContextService.Services;

/// <summary>
/// Accepts a zip of a folder, extracts to temp, runs code analysis on a target file, then cleans up.
/// </summary>
public class UploadAnalyzerService
{
    private readonly IOptions<RepoCloneOptions> _cloneOptions;
    private readonly ILogger<UploadAnalyzerService> _log;

    public UploadAnalyzerService(IOptions<RepoCloneOptions> cloneOptions, ILogger<UploadAnalyzerService> log)
    {
        _cloneOptions = cloneOptions;
        _log = log;
    }

    /// <summary>
    /// Extract zip to a temp dir, run FindMinimalDefinitionsAsync on the target file, then delete the temp dir.
    /// </summary>
    /// <param name="zipStream">Zip archive stream (e.g. from uploaded file)</param>
    /// <param name="targetFileRelative">Relative path inside the zip to the .cs file to analyze</param>
    /// <param name="solutionFileRelative">Relative path inside the zip to the .sln file (required; validated to exist after extract)</param>
    /// <param name="depth">Analysis depth</param>
    /// <param name="log">Optional log callback</param>
    public async Task<(IReadOnlyCollection<DefinitionResult> Results, List<string> Logs)> AnalyzeZipAsync(
        Stream zipStream,
        string targetFileRelative,
        string solutionFileRelative,
        int depth,
        Action<string>? log = null)
    {
        log ??= _ => { };
        var logs = new List<string>();
        void AddLog(string msg)
        {
            logs.Add(msg);
            log(msg);
            _log.LogInformation("[AnalyzeZip] {Message}", msg);
        }

        var root = string.IsNullOrWhiteSpace(_cloneOptions.Value.CloneRootPath)
            ? Path.GetTempPath()
            : _cloneOptions.Value.CloneRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var extractDir = Path.Combine(root, $"upload-{Guid.NewGuid():N}");

        _log.LogInformation("[AnalyzeZip] Starting: targetFile={TargetFile}, solutionFile={SolutionFile}, depth={Depth}, extractDir={ExtractDir}",
            targetFileRelative, solutionFileRelative, depth, extractDir);

        try
        {
            AddLog($"Extracting to {extractDir}");
            Directory.CreateDirectory(extractDir);
            int entryCount = 0;
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                _log.LogInformation("[AnalyzeZip] Zip archive opened, entry count={Count}", archive.Entries.Count);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var destPath = Path.Combine(extractDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    var destDir = Path.GetDirectoryName(destPath)!;
                    Directory.CreateDirectory(destDir);
                    entry.ExtractToFile(destPath, overwrite: true);
                    entryCount++;
                }
            }
            _log.LogInformation("[AnalyzeZip] Extracted {Count} file(s) to {Path}", entryCount, extractDir);

            // Normalize target path: form may send "Folder\file.cs" or "Folder/file.cs"
            var targetNormalized = targetFileRelative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var fullTargetPath = Path.Combine(extractDir, targetNormalized);
            _log.LogInformation("[AnalyzeZip] Resolved target path: {FullPath} (exists={Exists})", fullTargetPath, File.Exists(fullTargetPath));
            if (!File.Exists(fullTargetPath))
            {
                throw new FileNotFoundException($"Target file not found inside zip: {targetFileRelative}", fullTargetPath);
            }

            var solutionNormalized = solutionFileRelative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var fullSolutionPath = Path.Combine(extractDir, solutionNormalized);
            _log.LogInformation("[AnalyzeZip] Resolved solution path: {FullPath} (exists={Exists})", fullSolutionPath, File.Exists(fullSolutionPath));
            if (!File.Exists(fullSolutionPath))
            {
                throw new FileNotFoundException($"Solution file not found inside zip: {solutionFileRelative}. The uploaded folder must contain a .sln file.", fullSolutionPath);
            }

            AddLog($"Analyzing {fullTargetPath} (depth={depth})");
            _log.LogInformation("[AnalyzeZip] Creating DefinitionFinderService and running FindMinimalDefinitionsAsync...");
            var finder = new DefinitionFinderService(msg => AddLog(msg));
            var results = await finder.FindMinimalDefinitionsAsync(fullTargetPath, depth, ExplainMode.ReasonForInclusion);
            finder.Dispose();
            AddLog($"Done. {results.Count} result(s).");
            _log.LogInformation("[AnalyzeZip] Analysis complete. Result count={Count}", results.Count);
            return (results, logs);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[AnalyzeZip] Error: {Message}", ex.Message);
            throw;
        }
        finally
        {
            if (Directory.Exists(extractDir))
            {
                try
                {
                    Directory.Delete(extractDir, recursive: true);
                    _log.LogInformation("[AnalyzeZip] Cleaned up upload dir {Path}", extractDir);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[AnalyzeZip] Failed to delete upload dir {Path}", extractDir);
                    logs.Add($"Warning: could not delete temp dir: {ex.Message}");
                }
            }
        }
    }
}
