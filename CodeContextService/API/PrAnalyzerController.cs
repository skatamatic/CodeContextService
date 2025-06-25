using CodeContextService.Services;

namespace CodeContextService.API;

public record AnalysisRequest(
    SourceControlConnectionInfo sourceControlConnectionString,
    int PrNumber,
    int Depth,
    string Mode
);

public static class PrAnalyzerEndpoints
{
    public static void MapPrAnalyzerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/analyze", Analyze)
           .WithName("RunPrAnalysis")
           .Produces(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> Analyze(
        PRAnalyzerService analyzer,
        AnalysisRequest req
    )
    {
        var logs = new List<string>();
        try
        {
            var mode = string.IsNullOrEmpty(req.Mode) ? DefinitionAnalysisMode.Full : Enum.Parse<DefinitionAnalysisMode>(req.Mode);
            int depth = Math.Clamp(req.Depth, 1, 10);
            var result = await analyzer.RunAnalysis(
                req.sourceControlConnectionString, req.PrNumber, req.Depth, mode, msg => logs.Add(msg)
            );

            return Results.Ok(new { Logs = logs, Result = result });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}