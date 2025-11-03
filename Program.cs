using ConsensusAI.Models;
using ConsensusAI.Services;
using Microsoft.SemanticKernel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Kernel>(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder()
        .AddOpenAIChatCompletion("gpt-4", builder.Configuration["OpenAI"] ?? string.Empty);
    return kernelBuilder.Build();
});

builder.Services.AddSingleton<StockAnalysisOrchestrator>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "ConsensusAI API",
        Version = "v1",
        Description = "Multi-agent AI system for stock analysis"
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapPost("/analyze-stock", async (StockRequest request, StockAnalysisOrchestrator orchestrator) =>
{
    var result = await orchestrator.AnalyzeStock(request);
    return Results.Ok(result);
})
.WithName("AnalyzeStock")
.WithDescription("Analyzes one or more stocks using multiple AI agents who discuss and debate before reaching a consensus")
.Produces<AnalysisResult>(200);

app.Run();