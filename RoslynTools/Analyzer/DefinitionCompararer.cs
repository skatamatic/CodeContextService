namespace RoslynTools.Analyzer;

public class DefinitionCompararer : IEqualityComparer<Definition>
{
    public bool Equals(Definition x, Definition y)
    {
        return x.FullName == y.FullName;
    }

    public int GetHashCode(Definition obj)
    {
        return obj.FullName.GetHashCode();
    }
}