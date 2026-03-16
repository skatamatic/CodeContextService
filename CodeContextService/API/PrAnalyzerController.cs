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
        app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow }))
           .AllowAnonymous()
           .WithName("Health")
           .WithTags("Health");

        app.MapPost("/api/analyze", Analyze)
           .RequireRateLimiting("analyze")
           .WithName("RunPrAnalysis")
           .Produces(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status500InternalServerError)
           .Produces(StatusCodes.Status429TooManyRequests);
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
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                error = ex.Message,
                hint = "If calling from Azure DevOps, this endpoint expects a GitHub PAT plus GitHub owner/repo inputs."
            });
        }
        catch (HttpRequestException ex)
        {
            if (ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("authentication replay", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    hint = "Upstream HTTP auth/redirect loop. Verify GitHub token scope, owner/repo, and any proxy/SSO redirect behavior."
                });
            }
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}