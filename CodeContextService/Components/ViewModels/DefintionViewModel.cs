namespace CodeContextService.Components.ViewModels;

public class DefinitionDisplayModel
{
    public string Symbol { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
}
