namespace RoslynTools.Analyzer;

public class Definition
{
    public string Symbol { get; set; }
    public string Code { get; set; }
    public string Namespace { get; set; }
    public string FullName => $"{Namespace}.{Symbol}";

    public DefinitionSupplement Supplement { get; set; }
}

