using RoslynTools.Analyzer;

namespace CodeContextService.Model;

public class AnalysisResult
{
    public IEnumerable<FileDiff>? FileDiffs { get; set; }
    public Dictionary<string, IEnumerable<Definition>>? DefinitionMap { get; set; }
    public IEnumerable<DefinitionResult> MergedResults { get; set; }
}

