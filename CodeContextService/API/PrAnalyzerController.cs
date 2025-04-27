using CodeContextService.Services;
using Microsoft.AspNetCore.Http;
using CodeContextService.Model;

namespace CodeContextService.API;

public record AnalysisRequest(
    string Token,
    string Owner,
    string Repo,
    int PrNumber
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
            var result = await analyzer.RunAnalysis(
                req.Token, req.Owner, req.Repo, req.PrNumber, msg => logs.Add(msg)
            );
            return Results.Ok(new { Logs = logs, Result = result });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}