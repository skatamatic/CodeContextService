using CodeContextService.Services;
using Microsoft.AspNetCore.Http;
using CodeContextService.Model;
using System.IO;
using Microsoft.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace CodeContextService.API;

public record AnalysisRequest(
    string Token,
    string Owner,
    string Repo,
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
                req.Token, req.Owner, req.Repo, req.PrNumber, req.Depth, mode, msg => logs.Add(msg)
            );

            var jsonOptions = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            return Results.Json(new { Logs = logs, Result = result }, jsonOptions);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}