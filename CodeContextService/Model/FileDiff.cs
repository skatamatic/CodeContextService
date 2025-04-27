namespace CodeContextService.Model;

public class FileDiff
{
    public string FileName { get; set; } = "";
    public List<string> DiffLines { get; } = new();
    public int Added { get; set; }
    public int Removed { get; set; }
}

