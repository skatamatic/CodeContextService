using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynTools.Analyzer;

/// <summary>
/// Produces minimal, depth-limited source definitions for symbols referenced in a C# file.
/// This service uses Roslyn to analyze C# code and extract relevant type and member definitions
/// based on their usage and reference depth from an initial entry point file.
/// </summary>
public sealed class DefinitionFinderServiceV2 : FinderServiceBase, IDefinitionFinderService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DefinitionFinderServiceV2"/> class.
    /// </summary>
    /// <param name="log">An action to be used for logging messages during the service's operations.</param>
    public DefinitionFinderServiceV2(Action<string> log) : base(log) { }

    #region PUBLIC CONTRACT
    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<DefinitionResult>> FindAllDefinitionsAsync(string file, int depth)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth), "Depth cannot be negative.");
        var solution = await GetSolutionAsync(file);
        var doc = LocateDocument(solution, file)
                                 ?? throw new FileNotFoundException("The specified root file was not found in the solution.", file);

        var acc = new Dictionary<string, DefinitionResult>();
        await WalkFullAsync(solution, doc, depth, acc);
        return acc.Values.ToArray();
    }

    /// <inheritdoc/>
    public async Task<DefinitionResult?> FindSingleClassDefinitionAsync(string anyFile, string className)
    {
        var solution = await GetSolutionAsync(anyFile);
        var sym = await LocateClassSymbolAsync(solution, className);
        if (sym is null) return null;

        var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc == null || loc.SourceTree == null) return null; // No source location

        var node = (await loc.SourceTree.GetRootAsync()).FindNode(loc.SourceSpan);
        return new DefinitionResult
        {
            File = loc.SourceTree.FilePath,
            Definitions = { [loc.SourceTree.FilePath] = BuildDefinition(sym, node) }
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<DefinitionResult>> FindMinimalDefinitionsAsync(
        string file,
        int depth,
        ExplainMode explain = ExplainMode.None,
        bool excludeTargetSourceFileDefinitions = false) // New parameter
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth), "Depth cannot be negative.");

        var solution = await GetSolutionAsync(file);
        var rootDoc = LocateDocument(solution, file)
                                ?? throw new FileNotFoundException("The specified root file was not found in the solution.", file);

        var (keepMap, rootTypes) = await CrawlSymbolsAsync(solution, rootDoc, depth, excludeTargetSourceFileDefinitions); // Pass new parameter
        return await EmitMinimalDefinitionsAsync(solution, keepMap, rootTypes, explain);
    }

    /// <summary>
    /// Finds minimal definitions by analyzing a collection of source files and merging their symbol requirements.
    /// If multiple source files reference the same downstream file, the definitions from that downstream file
    /// will be a union of what's needed by all referrers.
    /// </summary>
    /// <param name="sourceFiles">A collection of C# file paths to analyze as entry points.</param>
    /// <param name="depth">The maximum depth of references to follow from each source file.</param>
    /// <param name="explain">The mode for adding explanatory comments to the output.</param>
    /// <param name="excludeTargetSourceFileDefinitionsPerFile">
    /// If true, for each file in <paramref name="sourceFiles"/>, its own declared definitions
    /// will not be included in its individual analysis pass. However, if another file in
    /// <paramref name="sourceFiles"/> references it, its definitions might be included due to that reference.
    /// </param>
    /// <returns>A collection of <see cref="DefinitionResult"/>, each containing the merged and minimized source code for a file.</returns>
    public async Task<IReadOnlyCollection<DefinitionResult>> FindAggregatedMinimalDefinitionsAsync(
        IEnumerable<string> sourceFiles,
        int depth,
        ExplainMode explain = ExplainMode.None,
        bool excludeTargetSourceFileDefinitionsPerFile = false)
    {
        if (sourceFiles == null || !sourceFiles.Any())
        {
            _log("No source files provided for aggregation. Returning empty result.");
            return Array.Empty<DefinitionResult>();
        }

        // It's crucial that all sourceFiles can be found within the same Roslyn Solution.
        // GetSolutionAsync is part of FinderServiceBase. Its implementation details matter here.
        // Assuming GetSolutionAsync(firstFile) can provide a solution context for all files,
        // or that LocateDocument can work across projects if they are part of the loaded solution.
        // A more robust approach might involve creating an AdhocWorkspace and adding all sourceFiles to it explicitly.
        var firstFile = sourceFiles.First();
        _log($"Attempting to load solution context using starting file: {firstFile}");
        var solution = await GetSolutionAsync(firstFile);
        if (solution == null)
        {
            _log($"Error: Could not load solution from file {firstFile}. Cannot perform aggregation.");
            // Consider throwing an exception or returning an error indicator.
            return Array.Empty<DefinitionResult>();
        }


        var aggregatedKeepData = new Dictionary<INamedTypeSymbol, Dictionary<string, MemberInfo>>(SymbolEqualityComparer.Default);
        var aggregatedRootTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        _log($"Starting aggregation for {sourceFiles.Count()} files. Depth: {depth}, Explain: {explain}, ExcludePerFile: {excludeTargetSourceFileDefinitionsPerFile}");

        foreach (var filePath in sourceFiles)
        {
            _log($"Processing file for aggregation: {filePath}");
            var rootDoc = LocateDocument(solution, filePath); // LocateDocument is from FinderServiceBase

            if (rootDoc == null)
            {
                _log($"Warning: Could not locate document for '{filePath}' in the solution. Skipping this file.");
                continue;
            }

            // Perform the crawl for the current file.
            // The `excludeTargetSourceFileDefinitionsPerFile` flag applies to this specific crawl,
            // determining if `rootDoc`'s own definitions are initially kept.
            var (currentKeepMap, currentRootTypes) = await CrawlSymbolsAsync(solution, rootDoc, depth, excludeTargetSourceFileDefinitionsPerFile);
            _log($"Crawl for '{filePath}' found {currentKeepMap.Count} types to potentially keep.");

            // Merge currentKeepMap into aggregatedKeepData
            foreach (var typeEntry in currentKeepMap)
            {
                INamedTypeSymbol typeSymbol = typeEntry.Key;
                Dictionary<string, MemberInfo> membersFromCurrentCrawl = typeEntry.Value;

                if (!aggregatedKeepData.TryGetValue(typeSymbol, out var aggregatedMembersForType))
                {
                    aggregatedMembersForType = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                    aggregatedKeepData[typeSymbol] = aggregatedMembersForType;
                }

                foreach (var memberEntry in membersFromCurrentCrawl)
                {
                    string memberKey = memberEntry.Key; // Display string of the member
                    MemberInfo memberInfoFromCurrentCrawl = memberEntry.Value;

                    if (aggregatedMembersForType.TryGetValue(memberKey, out var existingAggregatedMemberInfo))
                    {
                        // Member already exists from a previous file's crawl, merge paths
                        foreach (string path in memberInfoFromCurrentCrawl.Paths)
                        {
                            existingAggregatedMemberInfo.Paths.Add(path); // HashSet ensures uniqueness of paths
                        }
                    }
                    else
                    {
                        // This member is new to the aggregated set for this type, add it.
                        // Create a new MemberInfo with a new HashSet for paths to ensure no unintended sharing.
                        aggregatedMembersForType[memberKey] = new MemberInfo(memberInfoFromCurrentCrawl.Symbol, new HashSet<string>(memberInfoFromCurrentCrawl.Paths));
                    }
                }
            }

            // Merge currentRootTypes into aggregatedRootTypes
            // If a type was a "root type" for any of the input files, it's considered a root type for the aggregate.
            foreach (var rootTypeSymbol in currentRootTypes)
            {
                aggregatedRootTypes.Add(rootTypeSymbol);
            }
            _log($"After merging '{filePath}', aggregatedKeepData has {aggregatedKeepData.Count} types. AggregatedRootTypes has {aggregatedRootTypes.Count} types.");
        }

        if (!aggregatedKeepData.Any())
        {
            _log("Aggregation resulted in no symbols to keep. Returning empty result.");
            return Array.Empty<DefinitionResult>();
        }

        _log($"Aggregation complete. Emitting minimal definitions for {aggregatedKeepData.Count} types.");
        return await EmitMinimalDefinitionsAsync(solution, aggregatedKeepData, aggregatedRootTypes, explain);
    }
    #endregion

    #region FULL WALK
    private async Task WalkFullAsync(
        Solution solution, Document doc, int maxDepth,
        IDictionary<string, DefinitionResult> acc, int level = 0)
    {
        if (level > maxDepth || doc.FilePath is null) return;

        var model = await doc.GetSemanticModelAsync();
        if (model is null)
        {
            _log($"Semantic model not found for {doc.FilePath}. Skipping.");
            return;
        }
        var root = await model.SyntaxTree.GetRootAsync();

        foreach (var node in root.DescendantNodes())
        {
            var type = (model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol) as INamedTypeSymbol;
            if (type is null || IsExcluded(type)) continue;

            var srcSymbolDefinition = await SymbolFinder.FindSourceDefinitionAsync(type, solution) as INamedTypeSymbol ?? type;
            foreach (var loc in srcSymbolDefinition.Locations.Where(l => l.IsInSource && l.SourceTree != null))
            {
                var filePath = loc.SourceTree!.FilePath;
                if (string.IsNullOrEmpty(filePath)) continue;

                var declarationNode = (await loc.SourceTree.GetRootAsync()).FindNode(loc.SourceSpan);

                var definitionResult = acc.TryGetValue(filePath, out var r) ? r : acc[filePath] = new() { File = filePath };
                var definitionKey = $"{filePath}:{type.ToDisplayString()}"; // Use original type for key consistency
                if (definitionResult.Definitions.ContainsKey(definitionKey)) continue;

                definitionResult.Definitions[definitionKey] = BuildDefinition(srcSymbolDefinition, declarationNode);
                var nextDoc = solution.GetDocument(loc.SourceTree);
                if (nextDoc != null)
                {
                    await WalkFullAsync(solution, nextDoc, maxDepth, acc, level + 1);
                }
            }
        }
    }
    #endregion

    #region MINIMAL WALK - Symbol Crawling Logic
    private sealed record MemberInfo(ISymbol Symbol, HashSet<string> Paths);
    private sealed record Frontier(ISymbol Symbol, int DepthLeft, string Path);

    private async Task<(Dictionary<INamedTypeSymbol, Dictionary<string, MemberInfo>> Keep, HashSet<INamedTypeSymbol> RootTypes)>
        CrawlSymbolsAsync(Solution solution, Document rootDoc, int maxDepth, bool excludeTargetSourceFileDefinitions) // Added excludeTargetSourceFileDefinitions
    {
        var keep = new Dictionary<INamedTypeSymbol, Dictionary<string, MemberInfo>>(SymbolEqualityComparer.Default);
        var rootTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var symbolQueue = new Queue<Frontier>();

        // Store rootDoc.FilePath for efficient access in local functions
        var rootDocFilePath = rootDoc.FilePath;
        if (string.IsNullOrEmpty(rootDocFilePath))
        {
            _log("Warning: Root document file path is null or empty. Exclusion of target source file definitions might not work correctly.");
            // Potentially throw or handle this case, though LocateDocument should ensure a valid doc.
        }


        // Local helper for registering symbols to be kept.
        void RegisterMember(INamedTypeSymbol typeSymbol, ISymbol memberSymbol, string path)
        {
            if (excludeTargetSourceFileDefinitions && rootDocFilePath != null)
            {
                // Check if the typeSymbol is declared in the root document
                bool isTypeInRootDoc = typeSymbol.DeclaringSyntaxReferences
                    .Any(sr => sr.SyntaxTree?.FilePath?.Equals(rootDocFilePath, StringComparison.OrdinalIgnoreCase) == true);

                if (isTypeInRootDoc)
                {
                    _log($"Excluding from keep: {memberSymbol.ToDisplayString()} (owner: {typeSymbol.Name}) because its type is in the target source file '{rootDocFilePath}'. Path: {path}");
                    return; // Do not register if it's from the root document and exclusion is enabled
                }
            }

            if (!keep.TryGetValue(typeSymbol, out var bucket))
            {
                bucket = keep[typeSymbol] = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
            }
            var key = memberSymbol.OriginalDefinition.ToDisplayString();
            if (!bucket.TryGetValue(key, out var mi))
            {
                mi = bucket[key] = new MemberInfo(memberSymbol.OriginalDefinition, new HashSet<string>());
            }
            mi.Paths.Add(path);
        }

        // Local helper for including mandatory members (e.g., static constructors, const fields).
        void IncludeMandatoryMembers(INamedTypeSymbol typeSymbol)
        {
            if (excludeTargetSourceFileDefinitions && rootDocFilePath != null)
            {
                bool isTypeInRootDoc = typeSymbol.DeclaringSyntaxReferences
                   .Any(sr => sr.SyntaxTree?.FilePath?.Equals(rootDocFilePath, StringComparison.OrdinalIgnoreCase) == true);
                if (isTypeInRootDoc)
                {
                    // _log($"Skipping mandatory members for {typeSymbol.Name} as it's in the excluded target source file.");
                    return;
                }
            }

            foreach (var cctor in typeSymbol.StaticConstructors)
                RegisterMember(typeSymbol, cctor, $"{typeSymbol.Name}::(static ctor; mandatory)");
            foreach (var fld in typeSymbol.GetMembers().OfType<IFieldSymbol>()
                                    .Where(f => f.IsConst || (f.IsStatic && f.IsReadOnly)))
                RegisterMember(typeSymbol, fld, $"{typeSymbol.Name}::{fld.Name} (const/static readonly; mandatory)");
        }

        // Phase 1: Initialize with types from the root document.
        // Pass excludeTargetSourceFileDefinitions to control direct registration.
        await InitializeFromRootDocumentTypesAsync(rootDoc, rootTypes, RegisterMember, excludeTargetSourceFileDefinitions);

        // Phase 2: Seed the BFS queue with symbols directly used (referenced) in the root document.
        // This is crucial for finding dependencies even if root definitions are excluded.
        await SeedBfsQueueFromUseSitesAsync(rootDoc, symbolQueue, maxDepth);

        // Phase 3: Process the BFS queue to discover further referenced symbols up to the specified depth.
        // RegisterMember and IncludeMandatoryMembers (called within) will respect the exclusion flag.
        await ProcessBfsSymbolQueueAsync(solution, symbolQueue, seenSymbols, RegisterMember, IncludeMandatoryMembers);

        return (keep, rootTypes);
    }

    private async Task InitializeFromRootDocumentTypesAsync(
        Document rootDoc,
        HashSet<INamedTypeSymbol> rootTypes,
        Action<INamedTypeSymbol, ISymbol, string> registerMemberAction,
        bool excludeTargetSourceFileDefinitions) // New parameter
    {
        await foreach (var t in GetDeclaredTypesAsync(rootDoc))
        {
            var originalT = t.OriginalDefinition as INamedTypeSymbol ?? t;
            rootTypes.Add(originalT); // Always populate rootTypes for identification

            // Only register members if we are NOT excluding target source file definitions
            if (!excludeTargetSourceFileDefinitions)
            {
                _log($"Including root type: {originalT.Name} and its members because exclusion is off.");
                registerMemberAction(originalT, originalT, SigWithLine(originalT) + " (declared in source file)");

                foreach (var m in originalT.GetMembers())
                    registerMemberAction(originalT, m, $"{SigWithLine(originalT)} member '{m.Name}' (declared in source file type)");

                foreach (var iface in originalT.AllInterfaces)
                {
                    var originalIface = iface.OriginalDefinition as INamedTypeSymbol ?? iface;
                    registerMemberAction(originalIface, originalIface, $"{SigWithLine(originalT)} implements {originalIface.Name}");
                    foreach (var mem in originalIface.GetMembers())
                        registerMemberAction(originalIface, mem, $"{SigWithLine(originalT)} implements {originalIface.Name} (interface member)");
                }
            }
            else
            {
                _log($"Identified root type: {originalT.Name}, but its definitions will be excluded from direct registration.");
            }
        }
    }

    private async Task SeedBfsQueueFromUseSitesAsync(Document rootDoc, Queue<Frontier> queue, int maxDepth)
    {
        await foreach (var (sym, path) in EnumerateUseSiteSymbolsAsync(rootDoc))
        {
            queue.Enqueue(new Frontier(sym.OriginalDefinition, maxDepth, path));
        }
    }
    
    private async Task ProcessBfsSymbolQueueAsync(
        Solution solution,
        Queue<Frontier> queue,
        HashSet<ISymbol> seen,
        Action<INamedTypeSymbol, ISymbol, string> registerMemberAction, // This action is now exclusion-aware
        Action<INamedTypeSymbol> includeMandatoryMembersAction) // This action is now exclusion-aware
    {
        while (queue.TryDequeue(out var frontierItem))
        {
            var currentSymbol = frontierItem.Symbol.OriginalDefinition;
            if (!seen.Add(currentSymbol) || currentSymbol.Locations.All(l => !l.IsInSource))
                continue;

            var ownerType = currentSymbol as INamedTypeSymbol ?? currentSymbol.ContainingType?.OriginalDefinition as INamedTypeSymbol;
            if (ownerType is null) continue;

            // RegisterMember will check the exclusion flag internally
            registerMemberAction(ownerType, currentSymbol, frontierItem.Path);
            // IncludeMandatoryMembers will also check the exclusion flag internally
            includeMandatoryMembersAction(ownerType);

            if (frontierItem.DepthLeft == 0) continue;

            await EnqueueReferencedChildrenAsync(solution, frontierItem, ownerType, queue);
        }
    }
    
    private async Task EnqueueReferencedChildrenAsync(
        Solution solution,
        Frontier parentFrontierItem,
        INamedTypeSymbol parentOwnerType,
        Queue<Frontier> queue)
    {
        foreach (var childSymbolOriginalDef in await CollectReferencedSymbolsAsync(parentFrontierItem.Symbol, solution))
        {
            var childOwnerType = childSymbolOriginalDef as INamedTypeSymbol ?? childSymbolOriginalDef.ContainingType?.OriginalDefinition as INamedTypeSymbol;
            if (childOwnerType is null) continue;

            var nextDepth = SymbolEqualityComparer.Default.Equals(childOwnerType, parentOwnerType.OriginalDefinition)
                                ? parentFrontierItem.DepthLeft
                                : parentFrontierItem.DepthLeft - 1;

            if (nextDepth < 0) continue;

            var nextPath = $"{parentFrontierItem.Path} -> {SigWithLine(childSymbolOriginalDef)}";
            queue.Enqueue(new Frontier(childSymbolOriginalDef, nextDepth, nextPath));
        }
    }
    #endregion

    #region EMIT - Generating Minimal Definitions
    private async Task<IReadOnlyCollection<DefinitionResult>> EmitMinimalDefinitionsAsync(
        Solution solution,
        Dictionary<INamedTypeSymbol, Dictionary<string, MemberInfo>> keepData, // This map is now pre-filtered by CrawlSymbolsAsync
        HashSet<INamedTypeSymbol> rootTypes,
        ExplainMode explain)
    {
        var resultsByFile = new Dictionary<string, DefinitionResult>();

        foreach (var (typeSymbol, membersToKeepInfo) in keepData)
        {
            var originalTypeSymbol = typeSymbol.OriginalDefinition as INamedTypeSymbol ?? typeSymbol;
            if (originalTypeSymbol.DeclaringSyntaxReferences.Length == 0) continue;

            var renderedSyntax = await RenderTypeSyntaxAsync(solution, originalTypeSymbol, membersToKeepInfo, rootTypes, explain);
            if (renderedSyntax is null) continue;

            var filePath = originalTypeSymbol.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath)) continue;

            if (!resultsByFile.TryGetValue(filePath, out var definitionResultForFile))
            {
                definitionResultForFile = resultsByFile[filePath] = new DefinitionResult { File = filePath };
            }

            var definitionKey = $"{filePath}:{originalTypeSymbol.ToDisplayString()}";
            definitionResultForFile.Definitions[definitionKey] = BuildDefinition(originalTypeSymbol, renderedSyntax);
        }
        return resultsByFile.Values.ToArray();
    }

    private async Task<SyntaxNode?> RenderTypeSyntaxAsync(
        Solution solution,
        INamedTypeSymbol typeSymbol, // Should be OriginalDefinition
        Dictionary<string, MemberInfo> membersToKeepInfo,
        HashSet<INamedTypeSymbol> rootTypes,
        ExplainMode explain)
    {
        var firstSyntaxRef = typeSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (firstSyntaxRef == null) return null;
        var originalSyntaxNode = await firstSyntaxRef.GetSyntaxAsync();

        bool shouldKeepAllMembers = rootTypes.Contains(typeSymbol) || rootTypes.Contains(typeSymbol.OriginalDefinition as INamedTypeSymbol);


        if (originalSyntaxNode is TypeDeclarationSyntax typeDeclarationSyntax)
        {
            return await ProcessTypeDeclarationForEmitAsync(solution, typeDeclarationSyntax, typeSymbol, membersToKeepInfo, shouldKeepAllMembers, explain);
        }
        else if (originalSyntaxNode is EnumDeclarationSyntax or DelegateDeclarationSyntax)
        {
            return AddExplanationTrivia(originalSyntaxNode, typeSymbol.ToDisplayString(), membersToKeepInfo, explain);
        }
        else
        {
            _log($"Unhandled syntax kind for type {typeSymbol.Name}: {originalSyntaxNode.Kind()}");
            if (membersToKeepInfo.ContainsKey(typeSymbol.ToDisplayString()))
                return AddExplanationTrivia(originalSyntaxNode, typeSymbol.ToDisplayString(), membersToKeepInfo, explain);
            return originalSyntaxNode;
        }
    }

    private async Task<TypeDeclarationSyntax> ProcessTypeDeclarationForEmitAsync(
    Solution solution,
    TypeDeclarationSyntax originalTypeSyntax,
    INamedTypeSymbol typeSymbol, // Should be OriginalDefinition
    Dictionary<string, MemberInfo> membersToKeepInfo, // This IS the source of truth for minimal members
    bool keepAllMembers, // This flag indicates if typeSymbol was from a rootDoc of a crawl
    ExplainMode explain)
    {
        var keptMemberSyntaxes = new List<MemberDeclarationSyntax>();
        // Use a set for efficient lookup of wanted member display strings.
        // These are the members that the crawling phase determined should be kept.
        var wantedMemberSymbolKeys = new HashSet<string>(membersToKeepInfo.Keys, StringComparer.Ordinal);

        // Iterate over all declaring syntax references to handle partial types correctly.
        foreach (var syntaxRef in typeSymbol.DeclaringSyntaxReferences)
        {
            var syntaxNodePart = await syntaxRef.GetSyntaxAsync();
            if (syntaxNodePart is not TypeDeclarationSyntax typeDeclarationPart) continue;

            var semanticModel = await solution.GetDocument(typeDeclarationPart.SyntaxTree)!.GetSemanticModelAsync();
            if (semanticModel is null) continue;

            foreach (var memberSyntax in typeDeclarationPart.Members)
            {
                var declaredMemberSymbols = GetDeclaredSymbolsForMemberSyntax(memberSyntax, semanticModel)
                                            .Select(s => s.OriginalDefinition)
                                            .ToList();

                // ***** MODIFIED CONDITION *****
                // Only include the member if its symbol (or one of its declared symbols for multi-declarations)
                // is present in the 'wantedMemberSymbolKeys' (derived from membersToKeepInfo/aggregatedKeepData).
                // The 'keepAllMembers' flag should not override this for member inclusion if minimality is desired.
                if (declaredMemberSymbols.Any(ms => ms != null && wantedMemberSymbolKeys.Contains(ms.ToDisplayString())))
                {
                    var representativeSymbol = semanticModel.GetDeclaredSymbol(memberSyntax)?.OriginalDefinition ?? declaredMemberSymbols.FirstOrDefault(s => s != null);
                    if (representativeSymbol != null)
                    {
                        keptMemberSyntaxes.Add(AddExplanationTrivia(memberSyntax, representativeSymbol.ToDisplayString(), membersToKeepInfo, explain));
                    }
                    else
                    {
                        keptMemberSyntaxes.Add(memberSyntax);
                        _log($"Could not find representative symbol for member syntax (but was in wanted keys): {memberSyntax.ToString().Substring(0, Math.Min(100, memberSyntax.ToString().Length))}");
                    }
                }
            }
        }

        // This part uses keepAllMembers for adjusting explanatory paths, which is fine.
        // It does not affect which members are included in keptMemberSyntaxes.
        if (!keepAllMembers && keptMemberSyntaxes.Count == 0 && membersToKeepInfo.TryGetValue(typeSymbol.ToDisplayString(), out var typeMemberInfo))
        {
            // This case is for when the type symbol itself was marked to be kept, but none of its members were.
            typeMemberInfo.Paths.Clear();
            typeMemberInfo.Paths.Add($"(type '{typeSymbol.Name}' kept, but all its specific members were filtered or not directly used/referenced)");
        }

        var decoratedTypeSyntax = AddExplanationTrivia(originalTypeSyntax, typeSymbol.ToDisplayString(), membersToKeepInfo, explain);
        return decoratedTypeSyntax.WithMembers(SyntaxFactory.List(keptMemberSyntaxes));
    }

    private IEnumerable<ISymbol> GetDeclaredSymbolsForMemberSyntax(MemberDeclarationSyntax memberSyntax, SemanticModel semanticModel)
    {
        switch (memberSyntax)
        {
            case BaseMethodDeclarationSyntax:
            case EventDeclarationSyntax:
                if (semanticModel.GetDeclaredSymbol(memberSyntax) is ISymbol mainSym) yield return mainSym;
                break;
            case PropertyDeclarationSyntax pds:
                if (semanticModel.GetDeclaredSymbol(pds) is ISymbol propSym) yield return propSym;
                if (pds.AccessorList != null)
                {
                    foreach (var accessor in pds.AccessorList.Accessors)
                        if (semanticModel.GetDeclaredSymbol(accessor) is ISymbol accSym) yield return accSym;
                }
                break;
            case FieldDeclarationSyntax fds:
                foreach (var variable in fds.Declaration.Variables)
                    if (semanticModel.GetDeclaredSymbol(variable) is ISymbol fieldSym) yield return fieldSym;
                break;
            case EventFieldDeclarationSyntax efds:
                foreach (var variable in efds.Declaration.Variables)
                    if (semanticModel.GetDeclaredSymbol(variable) is ISymbol eventSym) yield return eventSym;
                break;
            default:
                if (semanticModel.GetDeclaredSymbol(memberSyntax) is ISymbol otherSym) yield return otherSym;
                break;
        }
    }

    private static T AddExplanationTrivia<T>(T node,
                                            string symbolDisplayKey,
                                            Dictionary<string, MemberInfo> typeMembersInfo,
                                            ExplainMode explain) where T : SyntaxNode
    {
        if (explain == ExplainMode.None) return node;
        if (!typeMembersInfo.TryGetValue(symbolDisplayKey, out var memberSpecificInfo) || memberSpecificInfo.Paths.Count == 0)
        {
            return node;
        }

        var originalLeadingTrivia = node.GetLeadingTrivia();
        var indentWhitespace = originalLeadingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
        var indentString = indentWhitespace.ToFullString();

        var commentLines = memberSpecificInfo.Paths.Select(p => $"{indentString}// path: {p}");
        var commentBlockText = string.Join(Environment.NewLine, commentLines);

        string completeCommentToAdd = commentBlockText;
        if (originalLeadingTrivia.Any() || !string.IsNullOrWhiteSpace(node.ToFullString()))
        {
            completeCommentToAdd += Environment.NewLine;
        }

        var newTrivia = SyntaxFactory.ParseLeadingTrivia(completeCommentToAdd);
        return node.WithLeadingTrivia(newTrivia.Concat(originalLeadingTrivia));
    }
    #endregion

    #region SYMBOL HELPERS
    private async Task<IEnumerable<ISymbol>> CollectReferencedSymbolsAsync(ISymbol sym, Solution solution)
    {
        if (sym is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
            return Enumerable.Empty<ISymbol>();

        var syntaxRef = sym.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return Enumerable.Empty<ISymbol>();

        var node = await syntaxRef.GetSyntaxAsync();
        var doc = solution.GetDocument(node.SyntaxTree);
        if (doc == null) return Enumerable.Empty<ISymbol>();

        var model = await doc.GetSemanticModelAsync();
        if (model == null) return Enumerable.Empty<ISymbol>();

        IOperation? operationToScan = null;
        if (node is BaseMethodDeclarationSyntax methodDecl)
            operationToScan = methodDecl.Body != null ? model.GetOperation(methodDecl.Body) : (methodDecl.ExpressionBody != null ? model.GetOperation(methodDecl.ExpressionBody) : null);
        else if (node is PropertyDeclarationSyntax propDecl)
            operationToScan = propDecl.Initializer != null ? model.GetOperation(propDecl.Initializer.Value) : (propDecl.ExpressionBody != null ? model.GetOperation(propDecl.ExpressionBody) : null);
        else if (node is AccessorDeclarationSyntax accessorDecl)
            operationToScan = accessorDecl.Body != null ? model.GetOperation(accessorDecl.Body) : (accessorDecl.ExpressionBody != null ? model.GetOperation(accessorDecl.ExpressionBody) : null);
        operationToScan ??= model.GetOperation(node);


        if (operationToScan == null)
        {
            return Enumerable.Empty<ISymbol>();
        }

        var collector = new ReferencedSymbolCollector();
        collector.Visit(operationToScan);
        return collector.Collected.Select(s => s.OriginalDefinition).Distinct(SymbolEqualityComparer.Default);
    }

    private sealed class ReferencedSymbolCollector : OperationWalker
    {
        public readonly HashSet<ISymbol> Collected = new(SymbolEqualityComparer.Default);
        public override void Visit(IOperation? op)
        {
            if (op is null)
            {
                base.Visit(op);
                return;
            }
            ISymbol? referencedSymbol = null;
            switch (op)
            {
                case IInvocationOperation inv: referencedSymbol = inv.TargetMethod; break;
                case IMemberReferenceOperation mem: referencedSymbol = mem.Member; break;
                case IObjectCreationOperation obj: referencedSymbol = obj.Constructor; break;
            }

            if (referencedSymbol != null)
            {
                Collected.Add(referencedSymbol.OriginalDefinition);
            }
            base.Visit(op);
        }
    }

    private static async IAsyncEnumerable<(ISymbol Sym, string Path)> EnumerateUseSiteSymbolsAsync(Document doc)
    {
        var model = await doc.GetSemanticModelAsync();
        if (model is null) yield break;

        var root = await model.SyntaxTree.GetRootAsync();
        var text = await doc.GetTextAsync();

        foreach (var node in root.DescendantNodes())
        {
            if (model.GetDeclaredSymbol(node) != null) continue;

            var symbolInfo = model.GetSymbolInfo(node);
            var sym = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

            if (sym == null || sym is IErrorTypeSymbol || IsExcludedSymbolKindForUseSite(sym)) continue;

            var lineSpan = node.GetLocation().GetLineSpan();
            if (!lineSpan.IsValid || string.IsNullOrEmpty(doc.FilePath) || doc.FilePath.Contains("[metadata]")) continue;

            var startLine = lineSpan.StartLinePosition.Line;
            if (startLine < 0 || startLine >= text.Lines.Count) continue;

            var fileName = Path.GetFileName(doc.FilePath); // Requires using System.IO;
            yield return (sym.OriginalDefinition, $"({fileName}:{startLine + 1}) use: {text.Lines[startLine].ToString().Trim()}");
        }
    }

    private static bool IsExcludedSymbolKindForUseSite(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.Parameter => true,
            SymbolKind.Local => true,
            SymbolKind.RangeVariable => true,
            SymbolKind.Label => true,
            SymbolKind.TypeParameter => true,
            _ => false,
        };
    }

    private static string SigWithLine(ISymbol s)
    {
        var symbol = s.OriginalDefinition;
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree != null && !string.IsNullOrEmpty(l.SourceTree.FilePath));

        if (loc == null) return $"[meta] {symbol.Name}";

        string filePath = Path.GetFileName(loc.SourceTree!.FilePath); // Requires using System.IO;
        int lineNumber = loc.GetLineSpan().StartLinePosition.Line + 1;
        return $"({filePath}:{lineNumber}) {symbol.Name}";
    }
    #endregion

    #region UTILS
    private static async Task<INamedTypeSymbol?> LocateClassSymbolAsync(
        Solution solution, string className)
    {
        foreach (var proj in solution.Projects)
        {
            var compilation = await proj.GetCompilationAsync();
            if (compilation == null) continue;

            var byMetadataName = compilation.GetTypeByMetadataName(className);
            if (byMetadataName != null && byMetadataName.TypeKind != TypeKind.Error) return byMetadataName.OriginalDefinition as INamedTypeSymbol;

            var decls = await SymbolFinder.FindDeclarationsAsync(proj, className, ignoreCase: false, SymbolFilter.Type);
            var hit = decls.OfType<INamedTypeSymbol>()
                            .FirstOrDefault(s => s.Name.Equals(className, StringComparison.Ordinal) && s.TypeKind != TypeKind.Error);
            if (hit != null) return hit.OriginalDefinition as INamedTypeSymbol;
        }
        return null;
    }

    private static async IAsyncEnumerable<INamedTypeSymbol> GetDeclaredTypesAsync(Document doc)
    {
        var model = await doc.GetSemanticModelAsync();
        if (model is null) yield break;

        var root = await model.SyntaxTree.GetRootAsync();
        foreach (var typeNode in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(typeNode) is INamedTypeSymbol sym && sym.TypeKind != TypeKind.Error)
                yield return sym.OriginalDefinition as INamedTypeSymbol ?? sym;
        }
        foreach (var delegateNode in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(delegateNode) is INamedTypeSymbol sym && sym.TypeKind != TypeKind.Error)
                yield return sym.OriginalDefinition as INamedTypeSymbol ?? sym;
        }
    }
    #endregion
}