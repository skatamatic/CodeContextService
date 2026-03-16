using CodeContextService.Services;
using Microsoft.AspNetCore.Http;
using RoslynTools.Analyzer;

namespace CodeContextService.API;

public static class UploadAnalyzerEndpoints
{
    public static void MapUploadAnalyzerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/analyze-upload", AnalyzeUpload)
           .RequireRateLimiting("analyze")
           .WithName("AnalyzeUpload")
           .Produces(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status500InternalServerError)
           .Produces(StatusCodes.Status429TooManyRequests)
           .DisableAntiforgery(); // multipart from Blazor may not send antiforgery by default
    }

    private static async Task<IResult> AnalyzeUpload(
        HttpRequest request,
        UploadAnalyzerService uploadAnalyzer,
        ILogger<UploadAnalyzerService> logger)
    {
        logger.LogInformation("[analyze-upload] Request received (Content-Type: {ContentType})", request.ContentType ?? "(null)");

        if (!request.HasFormContentType)
        {
            logger.LogWarning("[analyze-upload] Rejected: not multipart/form-data");
            return Results.BadRequest("Expect multipart/form-data.");
        }

        logger.LogInformation("[analyze-upload] Reading form...");
        var form = await request.ReadFormAsync();
        var zipFile = form.Files.GetFile("zip");
        if (zipFile == null || zipFile.Length == 0)
        {
            logger.LogWarning("[analyze-upload] Rejected: missing or empty 'zip' file (Files count: {Count})", form.Files.Count);
            return Results.BadRequest("Missing or empty 'zip' file.");
        }

        var targetFile = form["targetFile"].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(targetFile) || !targetFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("[analyze-upload] Rejected: invalid targetFile '{TargetFile}'", targetFile ?? "(null)");
            return Results.BadRequest("'targetFile' must be the relative path to a .cs file inside the zip (e.g. RoslynTools/Analyzer/DefinitionFinderService.cs).");
        }

        var solutionFile = form["solutionFile"].FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(solutionFile) || !solutionFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("[analyze-upload] Rejected: invalid solutionFile '{SolutionFile}'", solutionFile ?? "(null)");
            return Results.BadRequest("'solutionFile' must be the relative path to a .sln file inside the zip. The folder must contain a solution file.");
        }

        var depth = 1;
        var depthStr = form["depth"].FirstOrDefault();
        if (!string.IsNullOrEmpty(depthStr) && int.TryParse(depthStr, out var d) && d >= 0)
            depth = d;

        logger.LogInformation("[analyze-upload] Form OK: zip size={Size} bytes, targetFile={TargetFile}, solutionFile={SolutionFile}, depth={Depth}",
            zipFile.Length, targetFile, solutionFile, depth);

        try
        {
            await using var stream = zipFile.OpenReadStream();
            logger.LogInformation("[analyze-upload] Calling AnalyzeZipAsync...");
            var (results, logLines) = await uploadAnalyzer.AnalyzeZipAsync(stream, targetFile, solutionFile, depth);
            logger.LogInformation("[analyze-upload] Success: {Count} result(s), {LogCount} log line(s)", results.Count, logLines.Count);
            return Results.Ok(new { Logs = logLines, Results = results });
        }
        catch (FileNotFoundException ex)
        {
            logger.LogWarning(ex, "[analyze-upload] File not found: {Message}", ex.Message);
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[analyze-upload] Unhandled exception: {Message}", ex.Message);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
