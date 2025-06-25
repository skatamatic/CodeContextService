using RoslynTools.Analyzer;
using CodeContextService.Model;
using Newtonsoft.Json;

namespace CodeContextService.Services;

public enum DefinitionAnalysisMode
{
    Full,
    Minified,
    MinifiedExplain
}

public class PRAnalyzerService
{
    readonly DefinitionFinderServiceV2 referenceFinder;
    readonly SourceControlIntegrationService sourceControlIntegrationService;

    public PRAnalyzerService(SourceControlIntegrationService sourceControlIntegrationService, DefinitionFinderServiceV2 definitionFinder)
    {
        this.referenceFinder = definitionFinder;
        this.sourceControlIntegrationService = sourceControlIntegrationService;
    }

    public async Task<AnalysisResult> RunAnalysis(
        SourceControlConnectionInfo cs,
        int prNumber,
        int depth,
        DefinitionAnalysisMode mode,
        Action<string>? log = null)
    {
        log ??= (_ => { });

        var definitionMap = new Dictionary<string, IEnumerable<Definition>>();

        log($"Fetching PR details for org {cs.Org}, repo {cs.Repo}, PR #{prNumber} to determine branch.");

        var unifiedDiff = await sourceControlIntegrationService.GetUnifiedDiff(cs, prNumber);
        var diff = ParseUnifiedDiff(unifiedDiff.Diff);
        log($"Fetched diff, files changed: {diff.Count()}");

        bool omitSourceFile = true;

        var aggregateResults = mode switch
        {
            DefinitionAnalysisMode.Minified => await referenceFinder.FindAggregatedMinimalDefinitionsAsync(
                diff.Select(f => Path.Combine(unifiedDiff.Path, f.FileName)),
                depth,
                ExplainMode.None,
                excludeTargetSourceFileDefinitionsPerFile: omitSourceFile
            ),
            DefinitionAnalysisMode.MinifiedExplain => await referenceFinder.FindAggregatedMinimalDefinitionsAsync(
                diff.Select(f => Path.Combine(unifiedDiff.Path, f.FileName)),
                depth,
                ExplainMode.ReasonForInclusion,
                excludeTargetSourceFileDefinitionsPerFile: omitSourceFile
            ),
            _ => Enumerable.Empty<DefinitionResult>()
        };

        Dictionary<string, IEnumerable<Definition>> flatAggregate = new();
        foreach (var result in aggregateResults)
        {
            flatAggregate[result.File] = result.Definitions.Values;
        }

        var json = JsonConvert.SerializeObject(flatAggregate, Formatting.Indented);
        log($"Flat analysis complete:\n{json}");

        foreach (var file in diff.Where(f => f.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            log($"Analyzing {file.FileName}...");
            try
            {
                IEnumerable<DefinitionResult> results = mode switch
                {
                    DefinitionAnalysisMode.Full => await referenceFinder.FindAllDefinitionsAsync(
                        Path.Combine(unifiedDiff.Path, file.FileName),
                        depth
                    ),
                    DefinitionAnalysisMode.Minified => await referenceFinder.FindMinimalDefinitionsAsync(
                        Path.Combine(unifiedDiff.Path, file.FileName),
                        depth,
                        excludeTargetSourceFileDefinitions: omitSourceFile
                    ),
                    DefinitionAnalysisMode.MinifiedExplain => await referenceFinder.FindMinimalDefinitionsAsync(
                        Path.Combine(unifiedDiff.Path, file.FileName),
                        depth,
                        ExplainMode.ReasonForInclusion,
                        excludeTargetSourceFileDefinitions: omitSourceFile
                    ),
                    _ => throw new NotImplementedException(),
                };
                var flat = results.SelectMany(r => r.Definitions.Values);
                definitionMap[file.FileName] = flat;
                log($"Found {flat.Count()} definitions");
            }
            catch (Exception ex)
            {
                log($"❌ Error loading {file.FileName} - {ex.Message}");
            }
        }

        log("Analysis complete.");

        var analysisResult = new AnalysisResult
        {
            DefinitionMap = definitionMap,
            FileDiffs = diff,
            MergedResults = aggregateResults
        };

        try
        {
            Directory.Delete(unifiedDiff.Path, true);
        }
        catch (Exception ex)
        {
            log($"❌ Error deleting cloned repository: {ex.Message}");
        }

        return analysisResult;
    }

    private IEnumerable<FileDiff> ParseUnifiedDiff(string diff)
    {
        var diffs = new List<FileDiff>();
        FileDiff? current = null;

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git"))
            {
                if (current != null)
                    diffs.Add(current);

                var parts = line.Split(' ');
                var pathPart = parts[^1];
                var fileName = pathPart.StartsWith("b/") ? pathPart[2..] : pathPart;
                current = new FileDiff { FileName = fileName };
                continue;
            }

            if (current == null)
                continue;

            if (line.StartsWith("+") && !line.StartsWith("+++"))
                current.Added++;
            else if (line.StartsWith("-") && !line.StartsWith("---"))
                current.Removed++;

            current.DiffLines.Add(line);
        }

        if (current != null)
            diffs.Add(current);

        return diffs;
    }
}