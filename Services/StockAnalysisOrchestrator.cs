using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ConsensusAI.Models;
using System.Text.Json;
using System.Text;

namespace ConsensusAI.Services;

public class StockAnalysisOrchestrator
{
    private readonly Kernel _kernel;
    private readonly List<Agent> _analysts;
    private readonly List<Agent> _researchers;
    private readonly ILogger<StockAnalysisOrchestrator> _logger;
    private readonly IStockDataService _stockDataService;
    private readonly RiskManager _riskManager;
    private readonly PortfolioManager _portfolioManager;

    public StockAnalysisOrchestrator(
        Kernel kernel,
        ILogger<StockAnalysisOrchestrator> logger,
        IStockDataService stockDataService)
    {
        _kernel = kernel;
        _logger = logger;
        _stockDataService = stockDataService;
        _riskManager = new RiskManager();
        _portfolioManager = new PortfolioManager();

        // Analyst Team - run in parallel
        _analysts = new List<Agent>
        {
            new Agent("Technical_Analyst",
                "You are a technical analyst. Analyze price trends, momentum indicators (RSI, MACD, moving averages), volume patterns, support/resistance levels, and chart formations. Provide specific entry/exit signals based on technical data.",
                "Technical"),
            new Agent("Fundamental_Analyst",
                "You are a fundamental analyst. Evaluate financial metrics, valuation ratios, earnings quality, growth rates, and business fundamentals. Assess intrinsic value vs market price.",
                "Fundamental"),
            new Agent("Sentiment_Analyst",
                "You are a sentiment analyst. Analyze market sentiment from news, social media, analyst ratings, and investor behavior. Gauge bullish/bearish sentiment and contrarian indicators.",
                "Sentiment"),
            new Agent("News_Analyst",
                "You are a news analyst. Evaluate recent news, earnings reports, analyst upgrades/downgrades, and macroeconomic events. Assess impact on stock price and sector trends.",
                "News")
        };

        // Researcher Team - debate structure
        _researchers = new List<Agent>
        {
            new Agent("Bull_Researcher",
                "You are a bullish researcher. Review analyst reports and build the strongest BULL case. Highlight growth catalysts, undervaluation, competitive advantages, and positive trends. Challenge bearish concerns with data.",
                "Bull"),
            new Agent("Bear_Researcher",
                "You are a bearish researcher. Review analyst reports and build the strongest BEAR case. Identify overvaluation, risks, competitive threats, and negative trends. Challenge bullish assumptions with data.",
                "Bear")
        };
    }

    public async Task<AnalysisResult> AnalyzeStock(
        StockRequest request,
        decimal portfolioValue = 100000m,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var tickers = string.Join(", ", request.Tickers);
        _logger.LogInformation("Starting {Mode} analysis for {Tickers}", request.Mode, tickers);

        var conversation = new List<AgentMessage>();
        var startTime = DateTime.UtcNow;

        try
        {
            // PHASE 1: Data Collection
            _logger.LogInformation("Phase 1: Market Data Collection");
            var stockData = await _stockDataService.GetMultipleStocksAsync(request.Tickers, cancellationToken);
            conversation.Add(new AgentMessage("System",
                $"Market data collected for {tickers} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
                DateTime.UtcNow));

            // PHASE 2: Parallel Analyst Execution
            _logger.LogInformation("Phase 2: Analyst Team Analysis (Parallel)");
            var analystTasks = _analysts.Select(a => a.AnalyzeAsync(_kernel, stockData, request.Mode, cancellationToken));
            var analystReports = await Task.WhenAll(analystTasks);

            foreach (var report in analystReports)
            {
                var subsummary = FormatAnalystReport(report);
                conversation.Add(new AgentMessage(report.AgentName, subsummary, report.Timestamp));
            }

            // PHASE 3: Researcher Debate
            _logger.LogInformation("Phase 3: Researcher Team Debate");
            var debateResult = await ConductResearcherDebate(
                stockData,
                analystReports,
                request.Mode,
                request.DiscussionRounds,
                cancellationToken);

            conversation.AddRange(debateResult.Messages);

            // PHASE 4: Trader Decision
            _logger.LogInformation("Phase 4: Trader Decision-Making");
            var tradeDecisions = await MakeTraderDecisions(
                stockData,
                analystReports,
                debateResult.Conclusion,
                request.Mode,
                cancellationToken);

            foreach (var (ticker, decision) in tradeDecisions)
            {
                var msg = $"{ticker}: {decision.Action} ({decision.Confidence:P0} confidence)\n" +
                         $"Suggested Allocation: {decision.SuggestedAllocation:P1}\n" +
                         $"Rationale: {decision.Rationale}";
                conversation.Add(new AgentMessage("Trader", msg, DateTime.UtcNow));
            }

            // PHASE 5: Risk Management Review
            _logger.LogInformation("Phase 5: Risk Management Assessment");
            var riskAssessments = await _riskManager.AssessRisk(
                _kernel,
                stockData,
                tradeDecisions,
                portfolioValue,
                cancellationToken);

            foreach (var (ticker, assessment) in riskAssessments)
            {
                var msg = $"{ticker} Risk Assessment:\n" +
                         $"  VaR (95%): ${assessment.ValueAtRisk:N0}\n" +
                         $"  CVaR: ${assessment.ConditionalVaR:N0}\n" +
                         $"  Risk Level: {assessment.RiskLevel}\n" +
                         $"  Position Size: {assessment.SuggestedPositionSize:P1}\n" +
                         $"  Risk Factors: {string.Join(", ", assessment.RiskFactors)}";
                conversation.Add(new AgentMessage("Risk_Manager", msg, DateTime.UtcNow));
            }

            // PHASE 6: Portfolio Construction
            _logger.LogInformation("Phase 6: Portfolio Construction");
            var portfolioDecision = await _portfolioManager.ConstructPortfolio(
                _kernel,
                tradeDecisions,
                riskAssessments,
                request.Mode,
                portfolioValue,
                cancellationToken);

            var portfolioMsg = $"Portfolio Score: {portfolioDecision.PortfolioScore:F1}/100\n" +
                              $"Positions: {portfolioDecision.Positions.Count}\n\n" +
                              $"{portfolioDecision.ExecutionPlan}";
            conversation.Add(new AgentMessage("Portfolio_Manager", portfolioMsg, DateTime.UtcNow));

            // Generate final summary
            var summary = GenerateFinalSummary(
                request,
                analystReports,
                debateResult.Conclusion,
                tradeDecisions,
                riskAssessments,
                portfolioDecision);

            var recommendations = tradeDecisions.ToDictionary(
                kvp => kvp.Key,
                kvp => new StockRecommendation(
                    kvp.Key,
                    kvp.Value.Action,
                    kvp.Value.Rationale,
                    null));

            var executionTime = (DateTime.UtcNow - startTime).TotalSeconds;
            _logger.LogInformation("Analysis completed in {Time:F1}s", executionTime);

            return new AnalysisResult(
                request.Tickers,
                request.Mode,
                request.Context,
                conversation,
                recommendations,
                summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis failed for {Tickers}", tickers);
            throw new StockAnalysisException($"Failed to analyze: {ex.Message}", ex);
        }
    }

    private async Task<(List<AgentMessage> Messages, string Conclusion)> ConductResearcherDebate(
        Dictionary<string, StockData> stockData,
        AnalystReport[] analystReports,
        AnalysisMode mode,
        int rounds,
        CancellationToken ct)
    {
        var messages = new List<AgentMessage>();
        var bullHistory = new ChatHistory(_researchers[0].SystemPrompt);
        var bearHistory = new ChatHistory(_researchers[1].SystemPrompt);

        var analystSummary = FormatAnalystReportsForDebate(analystReports);
        bullHistory.AddSystemMessage(analystSummary);
        bearHistory.AddSystemMessage(analystSummary);

        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();

        for (int round = 1; round <= rounds; round++)
        {
            // Bull's turn
            var bullPrompt = round == 1
                ? "Present your BULLISH case based on analyst reports. Focus on opportunities and upside."
                : "Respond to the bear's concerns. What data supports your bullish view?";

            bullHistory.AddUserMessage(bullPrompt);
            var bullResponse = await chatCompletion.GetChatMessageContentAsync(bullHistory, cancellationToken: ct);
            var bullMessage = bullResponse.Content ?? "No response";

            messages.Add(new AgentMessage("Bull_Researcher", bullMessage, DateTime.UtcNow));
            bearHistory.AddUserMessage($"Bull argues: {bullMessage}");

            // Bear's turn
            var bearPrompt = round == 1
                ? "Present your BEARISH case based on analyst reports. Focus on risks and downside."
                : "Respond to the bull's arguments. What risks and concerns remain?";

            bearHistory.AddUserMessage(bearPrompt);
            var bearResponse = await chatCompletion.GetChatMessageContentAsync(bearHistory, cancellationToken: ct);
            var bearMessage = bearResponse.Content ?? "No response";

            messages.Add(new AgentMessage("Bear_Researcher", bearMessage, DateTime.UtcNow));
            bullHistory.AddUserMessage($"Bear argues: {bearMessage}");
        }

        // Synthesis
        var synthesisPrompt = "Synthesize the bull and bear debate into balanced conclusions for each stock.";
        bullHistory.AddUserMessage(synthesisPrompt);
        var synthesis = await chatCompletion.GetChatMessageContentAsync(bullHistory, cancellationToken: ct);

        messages.Add(new AgentMessage("Debate_Synthesis", synthesis.Content ?? "Balanced view", DateTime.UtcNow));

        return (messages, synthesis.Content ?? "Debate complete");
    }

    private async Task<Dictionary<string, TradeDecision>> MakeTraderDecisions(
        Dictionary<string, StockData> stockData,
        AnalystReport[] analystReports,
        string debateConclusion,
        AnalysisMode mode,
        CancellationToken ct)
    {
        var prompt = $@"You are an experienced trader. Based on:
1. Analyst reports (technical, fundamental, sentiment, news)
2. Bull/Bear researcher debate
3. Analysis mode: {mode}

Make trading decisions. Respond ONLY in JSON:
{{
  ""decisions"": [
    {{
      ""ticker"": ""SYMBOL"",
      ""action"": ""StrongBuy|Buy|Hold|Sell|StrongSell|Avoid"",
      ""confidence"": 0.0-1.0,
      ""suggestedAllocation"": 0.0-1.0,
      ""rationale"": ""Reason based on analyst data and debate"",
      ""keyFactors"": [""factor1"", ""factor2""]
    }}
  ]
}}

=== ANALYST SUMMARY ===
{string.Join("\n", analystReports.Select(r => $"{r.AgentName}: {string.Join("; ", r.StockAnalyses.Select(s => $"{s.Key}: {s.Value.Summary}"))}"))}

=== DEBATE CONCLUSION ===
{debateConclusion}

Make decisions for: {string.Join(", ", stockData.Keys)}";

        var chatHistory = new ChatHistory();
        chatHistory.AddUserMessage(prompt);

        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();
        var response = await chatCompletion.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);

        return ParseTradeDecisions(response.Content ?? "", stockData.Keys.ToList());
    }

    private Dictionary<string, TradeDecision> ParseTradeDecisions(string json, List<string> tickers)
    {
        var decisions = new Dictionary<string, TradeDecision>();

        try
        {
            var cleanJson = ExtractJson(json);
            using var doc = JsonDocument.Parse(cleanJson);

            if (doc.RootElement.TryGetProperty("decisions", out var decisionsArray))
            {
                foreach (var item in decisionsArray.EnumerateArray())
                {
                    var ticker = item.GetProperty("ticker").GetString()?.ToUpperInvariant();
                    if (string.IsNullOrEmpty(ticker)) continue;

                    var actionStr = item.GetProperty("action").GetString() ?? "Hold";
                    var action = Enum.TryParse<StockAction>(actionStr, true, out var a) ? a : StockAction.Hold;

                    var confidence = item.TryGetProperty("confidence", out var c) ? c.GetDecimal() : 0.5m;
                    var allocation = item.TryGetProperty("suggestedAllocation", out var sa) ? sa.GetDecimal() : 0.2m;
                    var rationale = item.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "";

                    var factors = new List<string>();
                    if (item.TryGetProperty("keyFactors", out var kf))
                    {
                        factors.AddRange(kf.EnumerateArray().Select(f => f.GetString() ?? ""));
                    }

                    decisions[ticker] = new TradeDecision(ticker, action, confidence, allocation, rationale, factors);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse trade decisions");
        }

        foreach (var ticker in tickers)
        {
            if (!decisions.ContainsKey(ticker.ToUpperInvariant()))
            {
                decisions[ticker.ToUpperInvariant()] = new TradeDecision(
                    ticker.ToUpperInvariant(),
                    StockAction.Hold,
                    0.5m,
                    0.1m,
                    "Default decision - insufficient data",
                    new List<string>());
            }
        }

        return decisions;
    }

    private string FormatAnalystReport(AnalystReport report)
    {
        var sb = new StringBuilder($"{report.AgentName} Analysis:\n");
        foreach (var (ticker, analysis) in report.StockAnalyses)
        {
            sb.AppendLine($"\n{ticker}:");
            if (analysis.Strengths.Any())
                sb.AppendLine($"  Strengths: {string.Join("; ", analysis.Strengths)}");
            if (analysis.Concerns.Any())
                sb.AppendLine($"  Concerns: {string.Join("; ", analysis.Concerns)}");
            sb.AppendLine($"  Summary: {analysis.Summary}");
        }
        return sb.ToString();
    }

    private string FormatAnalystReportsForDebate(AnalystReport[] reports)
    {
        var sb = new StringBuilder("=== ANALYST REPORTS ===\n");
        foreach (var report in reports)
        {
            sb.AppendLine($"\n{report.AgentName}:");
            foreach (var (ticker, analysis) in report.StockAnalyses)
            {
                sb.AppendLine($"  {ticker}:");
                sb.AppendLine($"    Strengths: {string.Join("; ", analysis.Strengths)}");
                sb.AppendLine($"    Concerns: {string.Join("; ", analysis.Concerns)}");
                sb.AppendLine($"    {analysis.Summary}");
            }
        }
        return sb.ToString();
    }

    private string GenerateFinalSummary(
        StockRequest request,
        AnalystReport[] analystReports,
        string debateConclusion,
        Dictionary<string, TradeDecision> tradeDecisions,
        Dictionary<string, RiskAssessment> riskAssessments,
        PortfolioDecision portfolioDecision)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== FINAL RECOMMENDATION ({request.Mode}) ===\n");

        sb.AppendLine("PORTFOLIO OVERVIEW:");
        sb.AppendLine($"Portfolio Score: {portfolioDecision.PortfolioScore:F1}/100");
        sb.AppendLine($"Total Positions: {portfolioDecision.Positions.Count}\n");

        sb.AppendLine("POSITIONS:");
        foreach (var (ticker, position) in portfolioDecision.Positions.OrderByDescending(p => p.Value.PercentAllocation))
        {
            var decision = tradeDecisions[ticker];
            var risk = riskAssessments[ticker];

            sb.AppendLine($"\n{ticker}:");
            sb.AppendLine($"  Action: {position.Action} ({decision.Confidence:P0} confidence)");
            sb.AppendLine($"  Allocation: {position.PercentAllocation:P1} (${position.DollarAmount:N0})");
            sb.AppendLine($"  Risk Level: {risk.RiskLevel}");
            sb.AppendLine($"  VaR (95%): ${risk.ValueAtRisk:N0}");
            sb.AppendLine($"  Rationale: {decision.Rationale}");

            if (decision.KeyFactors.Any())
                sb.AppendLine($"  Key Factors: {string.Join(", ", decision.KeyFactors)}");
        }

        sb.AppendLine($"\nEXECUTION PLAN:\n{portfolioDecision.ExecutionPlan}");

        return sb.ToString();
    }

    private string ExtractJson(string text)
    {
        text = text.Trim();
        if (text.Contains("```json"))
        {
            var start = text.IndexOf("```json") + 7;
            var end = text.IndexOf("```", start);
            if (end > start) text = text.Substring(start, end - start).Trim();
        }
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        return jsonStart >= 0 && jsonEnd > jsonStart
            ? text.Substring(jsonStart, jsonEnd - jsonStart + 1)
            : text;
    }

    private void ValidateRequest(StockRequest request)
    {
        if (request.Tickers == null || !request.Tickers.Any())
            throw new ArgumentException("At least one ticker required");
        if (request.Tickers.Count > 10)
            throw new ArgumentException("Maximum 10 tickers");
        if (request.DiscussionRounds < 1 || request.DiscussionRounds > 5)
            throw new ArgumentException("Discussion rounds must be 1-5");
    }
}

public class StockAnalysisException : Exception
{
    public StockAnalysisException(string message) : base(message) { }
    public StockAnalysisException(string message, Exception innerException) : base(message, innerException) { }
}