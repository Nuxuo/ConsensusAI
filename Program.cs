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

// Configure cache options
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));

builder.Services.AddHttpClient<IStockDataService, EodhdStockDataService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client =>
    {
        // Increase timeout to 5 minutes for stock data operations
        client.Timeout = TimeSpan.FromMinutes(5);
    })
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));

builder.Services.AddSingleton<WebSearchService>();

builder.Services.AddSingleton<Kernel>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();

    var kernel = Kernel.CreateBuilder()
        .AddOpenAIChatCompletion(
            modelId: builder.Configuration["OpenAI:Model"] ?? "gpt-4",
            apiKey: openAiKey,
            httpClient: new HttpClient
            {
                // Increase OpenAI timeout to 5 minutes
                Timeout = TimeSpan.FromMinutes(5)
            })
        .Build();

    // Add web search if configured
    var webSearchService = sp.GetRequiredService<WebSearchService>();
    if (webSearchService.IsConfigured)
    {
        webSearchService.AddWebSearchToKernel(kernel);
        logger.LogInformation("Web search enabled");
    }

    return kernel;
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
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();

        if (exceptionFeature?.Error != null)
        {
            logger.LogError(exceptionFeature.Error, "Unhandled exception: {Message}", exceptionFeature.Error.Message);
        }

        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        var error = new ApiError(
            "An error occurred processing your request",
            app.Environment.IsDevelopment() ? exceptionFeature?.Error?.Message : null);
        await context.Response.WriteAsJsonAsync(error);
    });
});

app.UseResponseCompression();
app.UseCors("AllowWebApp");
app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");

// Main analysis endpoint with enhanced multi-agent system
app.MapPost("/api/v1/analyze", async (
    StockRequest request,
    StockAnalysisOrchestrator orchestrator,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        logger.LogInformation("═══════════════════════════════════════════════════════");
        logger.LogInformation("🚀 STARTING ANALYSIS");
        logger.LogInformation("   Tickers: {Tickers}", string.Join(", ", request.Tickers));
        logger.LogInformation("   Mode: {Mode}", request.Mode);
        logger.LogInformation("   Rounds: {Rounds}", request.DiscussionRounds);
        logger.LogInformation("   Web Search: {WebSearch}", request.EnableWebSearch ? "ENABLED" : "DISABLED");
        logger.LogInformation("═══════════════════════════════════════════════════════");

        var result = await orchestrator.AnalyzeStock(request, request.PortfolioValue, ct);

        logger.LogInformation("✅ ANALYSIS COMPLETE - Summary generated");
        logger.LogInformation("═══════════════════════════════════════════════════════");

        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        logger.LogWarning("Invalid request: {Message}", ex.Message);
        return Results.BadRequest(new ApiError("Invalid request", ex.Message));
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Analysis cancelled by user");
        return Results.Problem(detail: "Analysis was cancelled", statusCode: 499);
    }
    catch (StockAnalysisException ex)
    {
        logger.LogError(ex, "Analysis failed: {Message}", ex.Message);
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "Analysis failed");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected error during analysis: {Message}", ex.Message);
        return Results.Problem(detail: "An unexpected error occurred", statusCode: 500);
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
- Execution planning and risk-adjusted allocations
- Optional web search for real-time market data");

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

app.MapGet("/api/v1/stock-data/{ticker}", async (
    string ticker,
    IStockDataService stockDataService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        logger.LogInformation("Fetching stock data for {Ticker}", ticker);
        var data = await stockDataService.GetStockDataAsync(ticker, ct);
        logger.LogInformation("Stock data retrieved for {Ticker}", ticker);
        return Results.Ok(data);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to fetch stock data for {Ticker}: {Message}", ticker, ex.Message);
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
            "Optional Web Search (Bing)",
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
            AnalysisTime = "10-30s (depending on web search)",
            MaxTickers = 10
        },
        Timeouts = new
        {
            HttpClient = "5 minutes",
            OverallOperation = "10 minutes (recommended)"
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