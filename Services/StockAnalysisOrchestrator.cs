using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ConsensusAI.Models;
using System.Text.Json;

namespace ConsensusAI.Services;

public class StockAnalysisOrchestrator
{
    private readonly Kernel _kernel;
    private readonly List<Agent> _agents;
    private readonly ILogger<StockAnalysisOrchestrator> _logger;

    public StockAnalysisOrchestrator(Kernel kernel, ILogger<StockAnalysisOrchestrator> logger)
    {
        _kernel = kernel;
        _logger = logger;
        _agents = new List<Agent>
        {
            new Agent("Bull", "You are an optimistic analyst who looks for growth opportunities and positive signals. Focus on upside potential."),
            new Agent("Bear", "You are a risk-focused analyst who identifies potential problems and downside risks. Be skeptical and cautious."),
            new Agent("Technicals", "You are a technical analyst who focuses on price patterns, momentum, and chart signals."),
            new Agent("Fundamentals", "You are a fundamental analyst who examines financial metrics, valuation, and business quality.")
        };
    }

    public async Task<AnalysisResult> AnalyzeStock(StockRequest request)
    {
        var tickers = string.Join(", ", request.Tickers);
        _logger.LogDebug("Starting {Mode} analysis for {Tickers} with {Rounds} discussion rounds",
            request.Mode, tickers, request.DiscussionRounds);

        var conversation = new List<AgentMessage>();
        var chatHistory = new ChatHistory();

        var initialPrompt = GetInitialPrompt(request);

        conversation.Add(new AgentMessage("System", initialPrompt, DateTime.UtcNow));
        chatHistory.AddSystemMessage(initialPrompt);

        _logger.LogDebug("ROUND 1: Initial Analysis");

        // Round 1: Each agent gives their initial analysis
        foreach (var agent in _agents)
        {
            _logger.LogDebug("{Agent} analyzing...", agent.Name);

            var response = await agent.GetResponse(_kernel, tickers, chatHistory, request.Mode);

            _logger.LogDebug("{Agent}: {Response}", agent.Name, response);

            conversation.Add(new AgentMessage(agent.Name, response, DateTime.UtcNow));
            chatHistory.AddUserMessage($"{agent.Name} says: {response}");
        }

        // Additional discussion rounds
        for (int round = 2; round <= request.DiscussionRounds; round++)
        {
            _logger.LogDebug("ROUND {Round}: Discussion & Debate", round);

            foreach (var agent in _agents)
            {
                _logger.LogDebug("{Agent} responding...", agent.Name);

                var followUpPrompt = GetFollowUpPrompt(request, tickers);
                chatHistory.AddUserMessage(followUpPrompt);

                var response = await agent.GetFollowUp(_kernel, chatHistory);

                _logger.LogDebug("{Agent}: {Response}", agent.Name, response);

                conversation.Add(new AgentMessage(agent.Name, response, DateTime.UtcNow));
                chatHistory.AddAssistantMessage(response);
            }

            // Summarize every 2 rounds to prevent history from growing too large
            if (round % 2 == 0 && round < request.DiscussionRounds)
            {
                _logger.LogDebug("Summarizing discussion to prevent history overflow...");
                await SummarizeHistory(chatHistory, tickers);
            }
        }

        _logger.LogDebug("FINAL DECISION: Synthesizing...");

        // Final decision by moderator with mode-specific prompt
        var moderatorPrompt = GetModeratorPrompt(request, tickers);

        chatHistory.AddUserMessage(moderatorPrompt);
        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();
        var finalDecision = await chatCompletion.GetChatMessageContentAsync(chatHistory);

        var decisionText = finalDecision.Content ?? "Unable to reach decision";

        // Parse response based on mode
        var (recommendations, summary) = ParseDecision(decisionText, request);

        _logger.LogDebug("Analysis complete. Recommendations: {Count}", recommendations.Count);

        return new AnalysisResult(
            request.Tickers,
            request.Mode,
            request.Context,
            conversation,
            recommendations,
            summary
        );
    }

    private string GetInitialPrompt(StockRequest request)
    {
        var tickers = string.Join(", ", request.Tickers);
        var context = !string.IsNullOrEmpty(request.Context) ? $" Context: {request.Context}." : "";

        return request.Mode switch
        {
            AnalysisMode.Evaluate =>
                $"We are analyzing whether to buy the following stocks: {tickers}.{context} Each agent will share their perspective on each stock.",

            AnalysisMode.Compare =>
                $"We are comparing these stocks to determine which is the best investment: {tickers}.{context} Each agent will compare and contrast them.",

            AnalysisMode.Rank =>
                $"We are ranking these stocks from best to worst investment opportunity: {tickers}.{context} Each agent will provide their ranking perspective.",

            AnalysisMode.PickOne =>
                $"We need to pick ONE stock to buy from this list: {tickers}.{context} Each agent will argue for their top choice.",

            AnalysisMode.PortfolioReview =>
                $"We currently own these stocks and need to decide whether to hold or sell each: {tickers}.{context} Each agent will review the portfolio.",

            AnalysisMode.BuyOrSell =>
                $"For each of these stocks, we need to decide BUY, SELL, or HOLD: {tickers}.{context} Each agent will provide their action for each stock.",

            AnalysisMode.Diversify =>
                $"We want to build a diversified portfolio from these stocks: {tickers}.{context} Each agent will suggest which combination makes sense.",

            _ => $"We are analyzing these stocks: {tickers}.{context}"
        };
    }

    private string GetFollowUpPrompt(StockRequest request, string tickers)
    {
        return request.Mode switch
        {
            AnalysisMode.Compare => $"Respond to other analysts' comparisons of {tickers}. Which stock do you still think is best?",
            AnalysisMode.PickOne => $"Defend or reconsider your top pick from {tickers} based on others' arguments.",
            AnalysisMode.PortfolioReview => $"Respond to others' hold/sell recommendations for {tickers}.",
            _ => $"Respond to the other analysts' points about {tickers}. Do you agree or disagree? Keep it brief."
        };
    }

    private string GetModeratorPrompt(StockRequest request, string tickers)
    {
        return request.Mode switch
        {
            AnalysisMode.Evaluate =>
                $@"Based on the discussion, provide a recommendation for EACH stock in {tickers}.
                    Format as JSON:
                    {{
                      ""recommendations"": [
                        {{""ticker"": ""AAPL"", ""action"": ""Buy"", ""reasoning"": ""Strong fundamentals...""}},
                        {{""ticker"": ""MSFT"", ""action"": ""Hold"", ""reasoning"": ""Fair value...""}}
                      ],
                      ""summary"": ""Overall market outlook...""
                    }}
                    Actions: StrongBuy, Buy, Hold, Sell, StrongSell, Avoid",

            AnalysisMode.Compare =>
                $@"Based on the discussion, determine which stock is the BEST investment from {tickers}.
                    Format as JSON:
                    {{
                      ""recommendations"": [
                        {{""ticker"": ""WINNER"", ""action"": ""StrongBuy"", ""reasoning"": ""This is the best because...""}},
                        {{""ticker"": ""RUNNER_UP"", ""action"": ""Hold"", ""reasoning"": ""Good but not as strong...""}}
                      ],
                      ""summary"": ""The winner is X because...""
                    }}",

            AnalysisMode.Rank =>
                $@"Based on the discussion, rank all stocks in {tickers} from BEST to WORST.
                    Format as JSON:
                    {{
                      ""recommendations"": [
                        {{""ticker"": ""BEST"", ""action"": ""StrongBuy"", ""reasoning"": ""Top choice because..."", ""rank"": 1}},
                        {{""ticker"": ""SECOND"", ""action"": ""Buy"", ""reasoning"": ""Good option..."", ""rank"": 2}},
                        {{""ticker"": ""WORST"", ""action"": ""Avoid"", ""reasoning"": ""Too risky..."", ""rank"": 3}}
                      ],
                      ""summary"": ""Ranking rationale...""
                    }}",

            AnalysisMode.PickOne =>
                $@"Based on the discussion, pick ONE stock from {tickers} to buy.
                    Format as JSON:
                    {{
                      ""recommendations"": [
                        {{""ticker"": ""CHOSEN_ONE"", ""action"": ""StrongBuy"", ""reasoning"": ""This is the one because...""}}
                      ],
                      ""summary"": ""We choose X because...""
                    }}",

            AnalysisMode.PortfolioReview =>
                $@"Based on the discussion, decide whether to HOLD or SELL each stock in {tickers}.
                    Format as JSON:
                    {{
                      ""recommendations"": [
                        {{""ticker"": ""AAPL"", ""action"": ""Hold"", ""reasoning"": ""Still has potential...""}},
                        {{""ticker"": ""MSFT"", ""action"": ""Sell"", ""reasoning"": ""Time to take profits...""}}
                      ],
                      ""summary"": ""Portfolio action plan...""
                    }}
                    Actions: Hold, Sell, StrongSell",

            AnalysisMode.BuyOrSell =>
                $@"Based on the discussion, provide BUY/SELL/HOLD for EACH stock in {tickers}.
                    Format as JSON:
                    {{
                      ""recommendations"": [
                        {{""ticker"": ""AAPL"", ""action"": ""Buy"", ""reasoning"": ""Undervalued...""}},
                        {{""ticker"": ""MSFT"", ""action"": ""Sell"", ""reasoning"": ""Overextended...""}},
                        {{""ticker"": ""GOOGL"", ""action"": ""Hold"", ""reasoning"": ""Wait and see...""}}
                      ],
                      ""summary"": ""Trading strategy...""
                    }}
                    Actions: StrongBuy, Buy, Hold, Sell, StrongSell",

            AnalysisMode.Diversify =>
                $@"Based on the discussion, suggest which stocks from {tickers} to combine for a diversified portfolio.
                    Format as JSON:
                    {{
                      ""recommendations"": [
                        {{""ticker"": ""AAPL"", ""action"": ""Buy"", ""reasoning"": ""Core holding for tech exposure...""}},
                        {{""ticker"": ""JNJ"", ""action"": ""Buy"", ""reasoning"": ""Defensive healthcare balance...""}},
                        {{""ticker"": ""TSLA"", ""action"": ""Avoid"", ""reasoning"": ""Too volatile for this portfolio...""}}
                      ],
                      ""summary"": ""Suggested portfolio composition...""
                    }}",

            _ => $"Provide recommendations for {tickers} in JSON format."
        };
    }

    private (Dictionary<string, StockRecommendation> recommendations, string summary) ParseDecision(string decisionText, StockRequest request)
    {
        var recommendations = new Dictionary<string, StockRecommendation>();
        var summary = "";

        try
        {
            // Try to parse JSON response
            var jsonStart = decisionText.IndexOf('{');
            var jsonEnd = decisionText.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonText = decisionText.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(jsonText);

                if (doc.RootElement.TryGetProperty("summary", out var summaryElement))
                {
                    summary = summaryElement.GetString() ?? "";
                }

                if (doc.RootElement.TryGetProperty("recommendations", out var recsElement))
                {
                    foreach (var rec in recsElement.EnumerateArray())
                    {
                        var ticker = rec.GetProperty("ticker").GetString() ?? "";
                        var actionStr = rec.GetProperty("action").GetString() ?? "Hold";
                        var reasoning = rec.GetProperty("reasoning").GetString() ?? "";
                        int? rank = rec.TryGetProperty("rank", out var rankProp) ? rankProp.GetInt32() : null;

                        var action = Enum.TryParse<StockAction>(actionStr, true, out var parsedAction)
                            ? parsedAction
                            : StockAction.Hold;

                        recommendations[ticker] = new StockRecommendation(ticker, action, reasoning, rank);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse JSON response: {Error}", ex.Message);
            // Fallback: create basic recommendations
            foreach (var ticker in request.Tickers)
            {
                recommendations[ticker] = new StockRecommendation(
                    ticker,
                    StockAction.Hold,
                    "Analysis completed - see conversation for details"
                );
            }
            summary = decisionText;
        }

        return (recommendations, summary);
    }

    private async Task SummarizeHistory(ChatHistory chatHistory, string tickers)
    {
        // Keep only system message and create a summary of the discussion
        var summaryPrompt = $@"Summarize the key points from the discussion about {tickers} so far. 
            Include the main bullish arguments, bearish concerns, technical signals, and fundamental observations.
            Keep it concise (4-5 sentences max).";

        chatHistory.AddUserMessage(summaryPrompt);
        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();
        var summary = await chatCompletion.GetChatMessageContentAsync(chatHistory);

        // Clear history and start fresh with summary
        var systemMessage = chatHistory.First(m => m.Role == AuthorRole.System);
        chatHistory.Clear();
        chatHistory.Add(systemMessage);
        chatHistory.AddAssistantMessage($"Summary of discussion so far: {summary.Content}");

        _logger.LogDebug("History summarized to prevent overflow");
    }
}