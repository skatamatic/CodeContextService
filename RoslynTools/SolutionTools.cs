using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RoslynTools;

public interface ISolutionTools
{
    Task<string> SaveTestFile(string sourceFile, string testFileContent);
    string FindProjectFile(string sourceFile);
    Task WriteSourceFile(string path, string content);
    Task<string> ReadSourceFile(string path);
    string SuggestTestFileLocation(string sourceFile);
    bool HasTestsAlready(string sourceFile, out string existingTestPath);
    string FindSolutionFile(string sourceFile);
    string FindSolutionRoot(string sourceFile);
}

public class UnitySolutionTools : BaseSolutionTools
{
    public override bool IsTestProject(string projectFile)
    {
        var doc = XDocument.Load(projectFile);
        var references = doc.Descendants()
                            .Where(x => x.Name.LocalName == "Reference" || x.Name.LocalName == "PackageReference")
                            .Select(x => x.Attribute("Include")?.Value)
                            .ToList();
        return references.Any(r => r != null && r.Contains("nunit.framework"));
    }

    public override string SuggestTestFileLocation(string sourceFile)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFile);
        var testDirectory = Path.Combine(sourceDirectory, "Tests", "Editor");

        if (!Directory.Exists(testDirectory))
        {
            Directory.CreateDirectory(testDirectory);
        }

        var sourceFileName = Path.GetFileNameWithoutExtension(sourceFile);
        var testFileName = $"{sourceFileName}Tests.cs";

        return Path.Combine(testDirectory, testFileName);
    }

    public override async Task<string> SaveTestFile(string sourceFile, string testFileContent)
    {
        var testFilePath = SuggestTestFileLocation(sourceFile);
        if (testFilePath == null) throw new Exception("Unable to determine test file location.");

        var directory = Path.GetDirectoryName(testFilePath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(testFilePath, testFileContent);

        return testFilePath;
    }
}

public class BaseSolutionTools : ISolutionTools
{
    public string FindSolutionRoot(string sourceFile)
    {
        var solutionFile = FindSolutionFile(sourceFile);
        if (solutionFile == null) return null;

        return Path.GetDirectoryName(solutionFile);
    }

    public string FindSolutionFile(string sourceFile)
    {
        var directory = Path.GetDirectoryName(sourceFile);
        while (!string.IsNullOrEmpty(directory))
        {
            var solutionFile = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly);
            if (solutionFile.Length > 0)
            {
                return solutionFile[0];
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        return null;
    }

    public string FindProjectFile(string sourceFile)
    {
        var directory = Path.GetDirectoryName(sourceFile);
        while (!string.IsNullOrEmpty(directory))
        {
            var projectFile = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
            if (projectFile.Length > 0)
            {
                return projectFile[0];
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        return null;
    }

    public List<string> FindAllProjectsInSolution(string solutionFile)
    {
        var projects = new List<string>();
        var solutionDirectory = Path.GetDirectoryName(solutionFile);
        var lines = File.ReadAllLines(solutionFile);
        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("Project("))
            {
                var parts = line.Split(',');
                if (parts.Length > 1)
                {
                    var projectPath = parts[1].Trim().Trim('"');
                    var fullPath = Path.Combine(solutionDirectory, projectPath);
                    if (File.Exists(fullPath))
                    {
                        projects.Add(fullPath);
                    }
                }
            }
        }
        return projects;
    }

    public virtual bool IsTestProject(string projectFile)
    {
        var doc = XDocument.Load(projectFile);
        var references = doc.Descendants("PackageReference")
                            .Select(x => x.Attribute("Include")?.Value)
                            .ToList();
        return references.Any(r => r != null && (r.Contains("NUnit") || r.Contains("xUnit") || r.Contains("MSTest")));
    }

    public string FindTestProjectForSourceFile(string sourceFile)
    {
        var solutionFile = FindSolutionFile(sourceFile);
        if (solutionFile == null) return null;

        var projects = FindAllProjectsInSolution(solutionFile);
        var testProjects = projects.Where(IsTestProject).ToList();

        if (testProjects.Count == 0)
        {
            testProjects = projects.Where(x => Path.GetFileNameWithoutExtension(x).EndsWith("Tests")).ToList();
        }

        if (testProjects.Count == 0) return null;

        var sourceProject = FindProjectFile(sourceFile);
        if (sourceProject == null) return null;

        var sourceProjectName = Path.GetFileNameWithoutExtension(sourceProject);
        var testProject = testProjects.FirstOrDefault(tp => Path.GetFileNameWithoutExtension(tp).Contains(sourceProjectName));
        return testProject ?? testProjects.FirstOrDefault();
    }

    public string GetNamespace(string sourceFile)
    {
        var lines = File.ReadAllLines(sourceFile);
        var namespaceLine = lines.FirstOrDefault(line => line.Trim().StartsWith("namespace"));
        if (namespaceLine == null) return null;

        var match = Regex.Match(namespaceLine, @"namespace\s+([^\s]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    public virtual string SuggestTestFileLocation(string sourceFile)
    {
        var testProject = FindTestProjectForSourceFile(sourceFile);
        if (testProject == null) return null;

        var namespaceToExclude = GetCommonNamespacePrefix(testProject);
        var testProjectDirectory = Path.GetDirectoryName(testProject);
        var sourceNamespace = GetNamespace(sourceFile);

        if (sourceNamespace == null || namespaceToExclude == null) return null;

        var relativeNamespace = sourceNamespace.StartsWith(namespaceToExclude)
            ? sourceNamespace.Substring(namespaceToExclude.Length).Trim('.')
            : sourceNamespace;

        relativeNamespace = relativeNamespace.TrimEnd(';');

        var relativePath = relativeNamespace.Replace('.', Path.DirectorySeparatorChar);
        var sourceFileName = Path.GetFileNameWithoutExtension(sourceFile);
        var testFileName = $"{sourceFileName}Tests.cs";

        return Path.Combine(testProjectDirectory, relativePath, testFileName);
    }

    public string GetCommonNamespacePrefix(string testProject)
    {
        var testFiles = Directory.GetFiles(Path.GetDirectoryName(testProject), "*.cs", SearchOption.AllDirectories);
        var namespaces = testFiles.Select(GetNamespace).Where(ns => !string.IsNullOrEmpty(ns)).ToList();

        if (namespaces.Count == 0) return null;

        var commonPrefix = namespaces.First().Split('.').ToList();
        foreach (var ns in namespaces.Skip(1))
        {
            var parts = ns.Split('.').ToList();
            for (int i = 0; i < commonPrefix.Count && i < parts.Count; i++)
            {
                if (commonPrefix[i] != parts[i])
                {
                    commonPrefix = commonPrefix.Take(i).ToList();
                    break;
                }
            }
        }

        return string.Join('.', commonPrefix);
    }

    public virtual async Task<string> SaveTestFile(string sourceFile, string testFileContent)
    {
        var testFilePath = SuggestTestFileLocation(sourceFile);
        if (testFilePath == null) throw new Exception("Unable to determine test file location.");

        var directory = Path.GetDirectoryName(testFilePath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(testFilePath, testFileContent);

        return testFilePath;
    }

    public async Task WriteSourceFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content);
    }

    public async Task<string> ReadSourceFile(string path)
    {
        return await File.ReadAllTextAsync(path);
    }

    public bool HasTestsAlready(string sourceFile, out string existingTestPath)
    {
        existingTestPath = SuggestTestFileLocation(sourceFile);
        if (File.Exists(existingTestPath))
        {
            return true;
        }

        var solutionFile = FindSolutionFile(sourceFile);
        if (solutionFile == null)
        {
            return false;
        }

        var allProjects = FindAllProjectsInSolution(solutionFile);
        var testProjects = allProjects.Where(IsTestProject).ToList();
        var sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourceFile);
        var testFileNamePattern = $"{sourceFileNameWithoutExtension}Test";

        foreach (var testProject in testProjects)
        {
            var testFiles = Directory.GetFiles(Path.GetDirectoryName(testProject), "*.cs", SearchOption.AllDirectories);
            foreach (var testFile in testFiles)
            {
                if (Path.GetFileNameWithoutExtension(testFile).Contains(testFileNamePattern))
                {
                    existingTestPath = testFile;
                    return true;
                }
            }
        }

        existingTestPath = string.Empty;
        return false;
    }
}
