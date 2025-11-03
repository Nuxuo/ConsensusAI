using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ConsensusAI.Models;

namespace ConsensusAI.Services;

public class PortfolioManager
{
    private readonly string _systemPrompt = @"You are an expert portfolio manager. Your role is to:
1. Construct diversified portfolios from trade recommendations
2. Optimize position sizing for risk-adjusted returns
3. Consider correlation and sector exposure
4. Execute trades with appropriate timing

Balance risk-reward while maximizing Sharpe ratio.";

    public async Task<PortfolioDecision> ConstructPortfolio(
        Kernel kernel,
        Dictionary<string, TradeDecision> tradeDecisions,
        Dictionary<string, RiskAssessment> riskAssessments,
        AnalysisMode mode,
        decimal portfolioValue,
        CancellationToken cancellationToken = default)
    {
        var approvedTrades = tradeDecisions
            .Where(t => t.Value.Action != StockAction.Avoid && riskAssessments[t.Key].SuggestedPositionSize > 0)
            .ToList();

        if (!approvedTrades.Any())
        {
            return new PortfolioDecision(
                new Dictionary<string, PositionSize>(),
                "No trades approved by risk management",
                0);
        }

        var totalWeight = approvedTrades.Sum(t =>
            t.Value.Confidence * riskAssessments[t.Key].SuggestedPositionSize);

        var positions = new Dictionary<string, PositionSize>();

        foreach (var (ticker, decision) in approvedTrades)
        {
            var risk = riskAssessments[ticker];
            var rawWeight = decision.Confidence * risk.SuggestedPositionSize;
            var normalizedAllocation = rawWeight / totalWeight;

            if (mode == AnalysisMode.PickOne)
            {
                var bestTrade = approvedTrades.MaxBy(t => t.Value.Confidence);
                if (ticker != bestTrade.Key)
                    continue;
                normalizedAllocation = 0.95m;
            }
            else if (mode == AnalysisMode.Diversify)
            {
                normalizedAllocation = Math.Min(normalizedAllocation, 0.25m);
            }

            var dollarAmount = normalizedAllocation * portfolioValue;

            positions[ticker] = new PositionSize(
                ticker,
                normalizedAllocation,
                dollarAmount,
                decision.Action,
                risk.RiskLevel);
        }

        var totalAllocation = positions.Values.Sum(p => p.PercentAllocation);
        if (totalAllocation > 1m)
        {
            foreach (var ticker in positions.Keys.ToList())
            {
                var pos = positions[ticker];
                var adjusted = pos.PercentAllocation / totalAllocation;
                positions[ticker] = pos with
                {
                    PercentAllocation = adjusted,
                    DollarAmount = adjusted * portfolioValue
                };
            }
        }

        var prompt = $@"Portfolio construction for {positions.Count} positions:
{string.Join("\n", positions.Select(p =>
    $"- {p.Key}: {p.Value.PercentAllocation:P1} (${p.Value.DollarAmount:N0}) - {p.Value.Action} - Risk: {p.Value.RiskLevel}"))}

Total Allocation: {positions.Values.Sum(p => p.PercentAllocation):P1}
Mode: {mode}

Provide: 1) Is this portfolio well-constructed? 2) Diversification assessment 3) Execution priority order";

        var chatHistory = new ChatHistory(_systemPrompt);
        chatHistory.AddUserMessage(prompt);
        var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();
        var response = await chatCompletion.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);

        var portfolioScore = CalculatePortfolioScore(positions, riskAssessments);

        return new PortfolioDecision(
            positions,
            response.Content ?? "Portfolio constructed",
            portfolioScore);
    }

    private decimal CalculatePortfolioScore(
        Dictionary<string, PositionSize> positions,
        Dictionary<string, RiskAssessment> risks)
    {
        var diversificationScore = Math.Min(positions.Count / 5m, 1m);
        var riskScore = 1m - positions.Average(p => risks[p.Key].RiskLevel switch
        {
            "EXTREME" => 1m,
            "HIGH" => 0.7m,
            "MODERATE" => 0.4m,
            _ => 0.1m
        });
        var balanceScore = 1m - Math.Abs(0.5m - positions.Values.Max(p => p.PercentAllocation));

        return (diversificationScore + riskScore + balanceScore) / 3m * 100m;
    }
}

public record PositionSize(
    string Ticker,
    decimal PercentAllocation,
    decimal DollarAmount,
    StockAction Action,
    string RiskLevel
);

public record PortfolioDecision(
    Dictionary<string, PositionSize> Positions,
    string ExecutionPlan,
    decimal PortfolioScore
);