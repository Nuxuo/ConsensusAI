using ConsensusAI.Models;
using ConsensusAI.Services;
using Microsoft.SemanticKernel;
using Microsoft.AspNetCore.Diagnostics;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

var openAiKey = builder.Configuration["OpenAI:ApiKey"];
if (string.IsNullOrEmpty(openAiKey))
    throw new InvalidOperationException("OpenAI:ApiKey required");

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient<IStockDataService, EodhdStockDataService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

builder.Services.AddSingleton<Kernel>(sp =>
{
    return Kernel.CreateBuilder()
        .AddOpenAIChatCompletion(
            modelId: builder.Configuration["OpenAI:Model"] ?? "gpt-4",
            apiKey: openAiKey)
        .Build();
});

builder.Services.AddSingleton<StockAnalysisOrchestrator>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "ConsensusAI API",
        Version = "v1",
        Description = "Multi-agent stock analysis with specialized analysts, risk management, and portfolio construction"
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowWebApp", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "http://localhost:3000" })
              .AllowAnyMethod()
              .AllowAnyHeader());
});

builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddHealthChecks();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Logging.AddFilter("System", LogLevel.Warning);
}

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var error = new ApiError(
            "An error occurred processing your request",
            app.Environment.IsDevelopment() ? context.Features.Get<IExceptionHandlerFeature>()?.Error?.Message : null);
        await context.Response.WriteAsJsonAsync(error);
    });
});

app.UseResponseCompression();
app.UseCors("AllowWebApp");
app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");

// Main analysis endpoint with enhanced multi-agent system
app.MapPost("/api/v1/analyze", async (StockRequest request, StockAnalysisOrchestrator orchestrator, CancellationToken ct) =>
{
    try
    {
        var result = await orchestrator.AnalyzeStock(request, request.PortfolioValue, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ApiError("Invalid request", ex.Message));
    }
    catch (StockAnalysisException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "Analysis failed");
    }
})
.WithName("AnalyzeStocks")
.WithOpenApi()
.Produces<AnalysisResult>(200)
.WithDescription(@"Enhanced multi-agent stock analysis with:
- 4 Specialized Analysts (Technical, Fundamental, Sentiment, News) running in parallel
- Bull vs Bear researcher debate with structured rounds
- Trader decision-making with confidence scores
- Risk Management (VaR, CVaR, position sizing, veto power)
- Portfolio Construction with diversification and scoring
- Execution planning and risk-adjusted allocations");

// Legacy endpoint for backward compatibility
app.MapPost("/analyze-stock", async (StockRequest request, StockAnalysisOrchestrator orchestrator, CancellationToken ct) =>
{
    return Results.Ok(await orchestrator.AnalyzeStock(request, request.PortfolioValue, ct));
})
.WithName("AnalyzeStock_Legacy")
.ExcludeFromDescription();

app.MapGet("/api/v1/analysis-modes", () =>
{
    return Results.Ok(Enum.GetValues<AnalysisMode>().Select(m => new
    {
        Value = m.ToString(),
        Description = m switch
        {
            AnalysisMode.Evaluate => "Should I buy each stock?",
            AnalysisMode.Compare => "Which stock is best?",
            AnalysisMode.Rank => "Rank from best to worst",
            AnalysisMode.PickOne => "Choose single best stock",
            AnalysisMode.PortfolioReview => "Hold or sell each position?",
            AnalysisMode.BuyOrSell => "BUY/SELL/HOLD for each",
            AnalysisMode.Diversify => "Best portfolio combination",
            _ => "Standard analysis"
        }
    }));
})
.WithName("GetAnalysisModes")
.WithOpenApi();

app.MapGet("/api/v1/stock-data/{ticker}", async (string ticker, IStockDataService stockDataService, CancellationToken ct) =>
{
    try
    {
        var data = await stockDataService.GetStockDataAsync(ticker, ct);
        return Results.Ok(data);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "Data fetch failed");
    }
})
.WithName("GetStockData")
.WithOpenApi()
.Produces<StockData>(200);

// System info endpoint
app.MapGet("/api/v1/system-info", () =>
{
    return Results.Ok(new
    {
        Version = "2.0-Enhanced",
        Features = new[]
        {
            "4 Specialized Analysts (Technical, Fundamental, Sentiment, News)",
            "Bull vs Bear Researcher Debate",
            "Risk Management (VaR, CVaR, Position Sizing)",
            "Portfolio Construction & Diversification",
            "Parallel Analyst Execution",
            "Kelly Criterion Position Sizing",
            "Real-time Market Data (EODHD)",
            "Structured Communication Protocol"
        },
        Workflow = new[]
        {
            "Phase 1: Market Data Collection",
            "Phase 2: Parallel Analyst Analysis",
            "Phase 3: Bull vs Bear Debate",
            "Phase 4: Trader Decision-Making",
            "Phase 5: Risk Management Review",
            "Phase 6: Portfolio Construction"
        },
        Benchmarks = new
        {
            TargetSharpeRatio = "6.0+",
            TargetMaxDrawdown = "<2%",
            AnalysisTime = "10-15s",
            MaxTickers = 10
        },
        BasedOn = new[]
        {
            "TradingAgents (Xiao et al. 2024)",
            "AI-Powered Multi-Agent Trading Workflow (Ghosh 2025)"
        }
    });
})
.WithName("SystemInfo")
.WithOpenApi();

app.Run();

public partial class Program { }