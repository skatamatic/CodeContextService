namespace RoslynTools.Analyzer;

/// <summary>Searches a solution for symbol definitions.</summary>
public interface IDefinitionFinderService : IDisposable
{
    /// <summary>Returns every definition reachable from <paramref name="sourceFile"/> within <paramref name="depth"/> levels.</summary>
    Task<IReadOnlyCollection<DefinitionResult>> FindAllDefinitionsAsync(string sourceFile, int depth);

    /// <summary>Finds the definition of <paramref name="className"/> or <see langword="null"/> if it cannot be located.</summary>
    Task<DefinitionResult?> FindSingleClassDefinitionAsync(string anySourceFileOfSolution, string className);
}
