using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ConsensusAI.Models;

namespace ConsensusAI.Services;

public class Agent
{
    public string Name { get; }
    public string SystemPrompt { get; }
    public string Role { get; }

    public Agent(string name, string systemPrompt, string role = "Analyst")
    {
        Name = name;
        SystemPrompt = systemPrompt;
        Role = role;
    }

    public async Task<string> GetResponse(
        Kernel kernel,
        string tickers,
        ChatHistory sharedHistory,
        AnalysisMode mode,
        string agentSpecificData,
        bool enableWebSearch = false,
        CancellationToken cancellationToken = default)
    {
        var agentHistory = new ChatHistory(SystemPrompt);

        // Add web search context to system prompt if enabled
        if (enableWebSearch && kernel.Plugins.Contains("WebSearch"))
        {
            agentHistory.AddSystemMessage(@"You have access to web search via the WebSearch plugin. 
Use it to find recent news, analyst reports, earnings data, or market trends for the stocks you're analyzing. 
To search, include in your reasoning: 'I should search for [query]' and the system will provide results.");
        }

        foreach (var message in sharedHistory) agentHistory.Add(message);

        var modeGuidance = mode switch
        {
            AnalysisMode.Compare => " Compare them to find the best investment.",
            AnalysisMode.Rank => " Rank these from best to worst investment.",
            AnalysisMode.PickOne => " Which ONE is the best investment?",
            AnalysisMode.PortfolioReview => " Should I hold or sell each position?",
            AnalysisMode.BuyOrSell => " For each: BUY, SELL, or HOLD?",
            AnalysisMode.Diversify => " Which combination provides best diversification?",
            _ => " Should I buy these?"
        };

        var dataSection = !string.IsNullOrWhiteSpace(agentSpecificData)
            ? $"\n\nYour specific data to analyze:\n{agentSpecificData}"
            : "";

        var webSearchNote = enableWebSearch && kernel.Plugins.Contains("WebSearch")
            ? "\n\nNote: You can search the web for recent information if needed."
            : "";

        var prompt = $@"As {Name}, provide your view on {tickers}.{modeGuidance}{dataSection}{webSearchNote}

IMPORTANT: Base your analysis on the ACTUAL DATA PROVIDED. Reference specific metrics, prices, and indicators.
Be specific and data-driven. Keep response to 3-5 sentences focusing on key points.";

        agentHistory.AddUserMessage(prompt);

        var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();

        // Enable auto function calling if web search is available
        var executionSettings = enableWebSearch && kernel.Plugins.Contains("WebSearch")
            ? new OpenAIPromptExecutionSettings { ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions }
            : null;

        var response = await chatCompletion.GetChatMessageContentAsync(
            agentHistory,
            executionSettings,
            kernel,
            cancellationToken);

        return response.Content ?? "No response";
    }

    public async Task<string> GetFollowUp(
        Kernel kernel,
        ChatHistory sharedHistory,
        bool enableWebSearch = false,
        CancellationToken cancellationToken = default)
    {
        var agentHistory = new ChatHistory(SystemPrompt);

        if (enableWebSearch && kernel.Plugins.Contains("WebSearch"))
        {
            agentHistory.AddSystemMessage("You can use web search to verify claims or find additional data.");
        }

        foreach (var message in sharedHistory) agentHistory.Add(message);

        var prompt = @"Based on other agents' views, respond briefly:
1. What points do you agree/disagree with?
2. Any critical risks or opportunities others missed?
3. Has your view changed based on their insights?
Keep response to 2-3 sentences, focusing on DATA-DRIVEN insights.";

        agentHistory.AddUserMessage(prompt);

        var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();

        var executionSettings = enableWebSearch && kernel.Plugins.Contains("WebSearch")
            ? new OpenAIPromptExecutionSettings { ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions }
            : null;

        var response = await chatCompletion.GetChatMessageContentAsync(
            agentHistory,
            executionSettings,
            kernel,
            cancellationToken);

        return response.Content ?? "No response";
    }

    public async Task<AnalystReport> AnalyzeAsync(
        Kernel kernel,
        Dictionary<string, StockData> stockData,
        AnalysisMode mode,
        bool enableWebSearch = false,
        CancellationToken cancellationToken = default)
    {
        var analyses = new Dictionary<string, AgentAnalysis>();

        foreach (var (ticker, data) in stockData)
        {
            if (!data.DataAvailable)
            {
                analyses[ticker] = new AgentAnalysis(
                    ticker,
                    new Dictionary<string, object>(),
                    new List<string>(),
                    new List<string> { "Data unavailable" },
                    "Insufficient data for analysis");
                continue;
            }

            var (metrics, strengths, concerns) = AnalyzeStock(data);
            var summary = await GenerateSummary(kernel, ticker, data, strengths, concerns, mode, enableWebSearch, cancellationToken);

            analyses[ticker] = new AgentAnalysis(ticker, metrics, strengths, concerns, summary);
        }

        return new AnalystReport(Name, analyses, DateTime.UtcNow);
    }

    private (Dictionary<string, object> metrics, List<string> strengths, List<string> concerns) AnalyzeStock(StockData data)
    {
        var metrics = new Dictionary<string, object>();
        var strengths = new List<string>();
        var concerns = new List<string>();

        // Role-specific analysis
        switch (Role)
        {
            case "Technical":
                metrics["current_price"] = data.CurrentPrice;
                metrics["ma50"] = data.MovingAverage50;
                metrics["ma200"] = data.MovingAverage200;
                metrics["rsi"] = data.RSI;
                metrics["volume_ratio"] = data.AvgVolume > 0 ? (decimal)data.Volume / data.AvgVolume : 0;
                metrics["range_position"] = CalculateRangePosition(data);

                if (data.CurrentPrice > data.MovingAverage50 && data.CurrentPrice > data.MovingAverage200)
                    strengths.Add($"Strong uptrend: Price ${data.CurrentPrice:F2} above both 50-MA (${data.MovingAverage50:F2}) and 200-MA (${data.MovingAverage200:F2})");
                else if (data.CurrentPrice < data.MovingAverage50 && data.CurrentPrice < data.MovingAverage200)
                    concerns.Add($"Downtrend: Price ${data.CurrentPrice:F2} below both moving averages");

                if (data.RSI < 30)
                    strengths.Add($"Oversold RSI: {data.RSI:F1} suggests potential bounce");
                else if (data.RSI > 70)
                    concerns.Add($"Overbought RSI: {data.RSI:F1} suggests pullback risk");

                if (data.Volume > data.AvgVolume * 1.5m)
                    strengths.Add($"Strong volume: {data.Volume:N0} ({data.Volume / (decimal)data.AvgVolume:P0} of average)");
                break;

            case "Fundamental":
                metrics["ytd_return"] = data.YTDReturn;
                metrics["1y_return"] = data.OneYearReturn;
                metrics["price_change"] = (data.CurrentPrice - data.PreviousClose) / data.PreviousClose;

                if (data.YTDReturn > 0.1m)
                    strengths.Add($"Strong YTD performance: {data.YTDReturn:P1}");
                else if (data.YTDReturn < -0.1m)
                    concerns.Add($"Negative YTD return: {data.YTDReturn:P1}");

                if (data.OneYearReturn > 0.15m)
                    strengths.Add($"Solid 1-year returns: {data.OneYearReturn:P1}");
                else if (data.OneYearReturn < 0)
                    concerns.Add($"Negative 1-year performance: {data.OneYearReturn:P1}");
                break;

            case "Sentiment":
                metrics["sentiment"] = data.SentimentRating;
                metrics["volume_signal"] = data.Volume > data.AvgVolume ? "High" : "Low";
                metrics["momentum"] = (data.CurrentPrice - data.PreviousClose) / data.PreviousClose;

                if (data.SentimentRating.Contains("Positive"))
                    strengths.Add($"Positive market sentiment: {data.SentimentRating}");
                else if (data.SentimentRating.Contains("Negative"))
                    concerns.Add($"Negative market sentiment: {data.SentimentRating}");

                if (data.Volume > data.AvgVolume * 1.5m && data.CurrentPrice > data.PreviousClose)
                    strengths.Add("Strong buying pressure with high volume");
                break;

            case "News":
                metrics["news_sentiment"] = data.SentimentRating;
                metrics["recent_performance"] = data.YTDReturn;

                if (data.SentimentRating.Contains("Strong Positive"))
                    strengths.Add($"Strong positive news flow: {data.SentimentRating}");
                else if (data.SentimentRating.Contains("Strong Negative"))
                    concerns.Add($"Strong negative news flow: {data.SentimentRating}");
                break;

            case "Bull":
                if (data.CurrentPrice > data.MovingAverage50)
                    strengths.Add($"Above 50-MA: ${data.CurrentPrice:F2} > ${data.MovingAverage50:F2} indicates bullish momentum");
                if (data.YTDReturn > 0)
                    strengths.Add($"Positive YTD: {data.YTDReturn:P1}");
                if (data.SentimentRating.Contains("Positive"))
                    strengths.Add($"Bullish sentiment: {data.SentimentRating}");
                break;

            case "Bear":
                if (data.CurrentPrice < data.MovingAverage200)
                    concerns.Add($"Below 200-MA: ${data.CurrentPrice:F2} < ${data.MovingAverage200:F2} indicates bearish trend");

                var fromHigh = data.High52Week > 0 ? (data.High52Week - data.CurrentPrice) / data.High52Week : 0;
                if (fromHigh > 0.1m)
                    concerns.Add($"Down {fromHigh:P0} from 52-week high of ${data.High52Week:F2}");

                if (data.YTDReturn < 0)
                    concerns.Add($"Negative YTD: {data.YTDReturn:P1}");
                break;
        }

        return (metrics, strengths, concerns);
    }

    private async Task<string> GenerateSummary(
        Kernel kernel,
        string ticker,
        StockData data,
        List<string> strengths,
        List<string> concerns,
        AnalysisMode mode,
        bool enableWebSearch,
        CancellationToken cancellationToken)
    {
        var webSearchNote = enableWebSearch && kernel.Plugins.Contains("WebSearch")
            ? " You may search for recent news if helpful."
            : "";

        var prompt = $@"{Role} analysis for {ticker}:
Price: ${data.CurrentPrice:F2}
Strengths: {string.Join("; ", strengths)}
Concerns: {string.Join("; ", concerns)}

Based on mode '{mode}', provide a 2-3 sentence {Role.ToLower()} summary.{webSearchNote}";

        var chatHistory = new ChatHistory(SystemPrompt);
        chatHistory.AddUserMessage(prompt);

        var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();

        var executionSettings = enableWebSearch && kernel.Plugins.Contains("WebSearch")
            ? new OpenAIPromptExecutionSettings { ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions }
            : null;

        var response = await chatCompletion.GetChatMessageContentAsync(
            chatHistory,
            executionSettings,
            kernel,
            cancellationToken);

        return response.Content ?? "No analysis available";
    }

    private decimal CalculateRangePosition(StockData data)
    {
        if (data.High52Week == data.Low52Week) return 0.5m;
        return (data.CurrentPrice - data.Low52Week) / (data.High52Week - data.Low52Week);
    }
}

public record AnalystReport(
    string AgentName,
    Dictionary<string, AgentAnalysis> StockAnalyses,
    DateTime Timestamp
);

public record AgentAnalysis(
    string Ticker,
    Dictionary<string, object> KeyMetrics,
    List<string> Strengths,
    List<string> Concerns,
    string Summary
);