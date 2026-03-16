using RoslynTools.Analyzer;

namespace CodeContextService.Model;

public class UploadAnalyzerResponse
{
    public List<string>? Logs { get; set; }
    public List<DefinitionResult>? Results { get; set; }
}
