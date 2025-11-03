using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ConsensusAI.Models;

namespace ConsensusAI.Services;

public class RiskManager
{
    private readonly string _systemPrompt = @"You are an expert risk manager. Your role is to:
1. Calculate risk metrics (VaR, CVaR, max drawdown potential)
2. Assess position sizing based on volatility and correlation
3. Identify risk factors: technical, fundamental, market, sentiment
4. Recommend risk mitigation strategies

CRITICAL: You can VETO trades that exceed risk thresholds.";

    public async Task<Dictionary<string, RiskAssessment>> AssessRisk(
        Kernel kernel,
        Dictionary<string, StockData> stockData,
        Dictionary<string, TradeDecision> tradeDecisions,
        decimal portfolioValue,
        CancellationToken cancellationToken = default)
    {
        var assessments = new Dictionary<string, RiskAssessment>();

        foreach (var (ticker, decision) in tradeDecisions)
        {
            if (!stockData.TryGetValue(ticker, out var data) || !data.DataAvailable)
            {
                assessments[ticker] = new RiskAssessment(
                    ticker, 0, 0, 0, "UNKNOWN",
                    new List<string> { "Insufficient data" },
                    0);
                continue;
            }

            var volatility = CalculateVolatility(data);
            var var95 = CalculateVaR(data, decision.SuggestedAllocation * portfolioValue, 0.95m);
            var cvar95 = var95 * 1.3m;
            var maxDrawdown = CalculateMaxDrawdown(data);

            var riskFactors = new List<string>();

            if (volatility > 0.3m)
                riskFactors.Add($"High volatility: {volatility:P0}");

            if (data.RSI > 70)
                riskFactors.Add($"Overbought: RSI {data.RSI:F1}");
            else if (data.RSI < 30)
                riskFactors.Add($"High reversal risk: RSI {data.RSI:F1}");

            if (data.CurrentPrice < data.MovingAverage200)
                riskFactors.Add("Below 200-day MA: downtrend risk");

            if (data.SentimentRating.Contains("Negative"))
                riskFactors.Add($"Negative sentiment: {data.SentimentRating}");

            if (maxDrawdown < -0.2m)
                riskFactors.Add($"High drawdown potential: {maxDrawdown:P0}");

            var riskScore = riskFactors.Count;
            if (volatility > 0.4m) riskScore += 2;
            if (var95 > portfolioValue * 0.05m) riskScore += 2;

            var riskLevel = riskScore switch
            {
                >= 5 => "EXTREME",
                >= 3 => "HIGH",
                >= 1 => "MODERATE",
                _ => "LOW"
            };

            var suggestedSize = CalculatePositionSize(
                decision.Confidence,
                volatility,
                riskLevel,
                portfolioValue);

            var prompt = $@"Risk assessment for {ticker}:
Trade Decision: {decision.Action} ({decision.Confidence:P0} confidence)
Suggested Allocation: {decision.SuggestedAllocation:P0} of ${portfolioValue:N0}

Risk Metrics:
- Volatility: {volatility:P0}
- 95% VaR: ${var95:N0}
- CVaR: ${cvar95:N0}
- Max Drawdown: {maxDrawdown:P0}
- Risk Level: {riskLevel}

Risk Factors: {string.Join(", ", riskFactors)}

Provide: 1) Should this trade proceed? (APPROVE/MODIFY/VETO)
2) Recommended position size adjustment
3) Key risk mitigation strategies";

            var chatHistory = new ChatHistory(_systemPrompt);
            chatHistory.AddUserMessage(prompt);
            var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();
            var response = await chatCompletion.GetChatMessageContentAsync(chatHistory, cancellationToken: cancellationToken);

            if (response.Content?.Contains("VETO") == true)
                suggestedSize = 0;
            else if (response.Content?.Contains("MODIFY") == true)
                suggestedSize *= 0.5m;

            assessments[ticker] = new RiskAssessment(
                ticker,
                var95,
                cvar95,
                maxDrawdown,
                riskLevel,
                riskFactors,
                suggestedSize);
        }

        return assessments;
    }

    private decimal CalculateVolatility(StockData data)
    {
        if (data.High52Week == 0) return 0;
        return (data.High52Week - data.Low52Week) / data.High52Week;
    }

    private decimal CalculateVaR(StockData data, decimal positionSize, decimal confidence)
    {
        var volatility = CalculateVolatility(data);
        var zScore = confidence switch
        {
            >= 0.99m => 2.33m,
            >= 0.95m => 1.65m,
            _ => 1.28m
        };
        return positionSize * volatility * zScore;
    }

    private decimal CalculateMaxDrawdown(StockData data)
    {
        if (data.High52Week == 0) return 0;
        var rangePosition = (data.CurrentPrice - data.Low52Week) / (data.High52Week - data.Low52Week);
        return -(1 - rangePosition);
    }

    private decimal CalculatePositionSize(
        decimal confidence,
        decimal volatility,
        string riskLevel,
        decimal portfolioValue)
    {
        var baseSize = 0.2m;
        var confidenceAdjusted = baseSize * confidence;
        var volatilityPenalty = volatility > 0.3m ? 0.5m : 1m;
        var riskPenalty = riskLevel switch
        {
            "EXTREME" => 0.25m,
            "HIGH" => 0.5m,
            "MODERATE" => 0.75m,
            _ => 1m
        };

        var finalSize = confidenceAdjusted * volatilityPenalty * riskPenalty;
        return Math.Max(0.01m, Math.Min(0.15m, finalSize));
    }
}

public record RiskAssessment(
    string Ticker,
    decimal ValueAtRisk,
    decimal ConditionalVaR,
    decimal MaxDrawdown,
    string RiskLevel,
    List<string> RiskFactors,
    decimal SuggestedPositionSize
);

public record TradeDecision(
    string Ticker,
    StockAction Action,
    decimal Confidence,
    decimal SuggestedAllocation,
    string Rationale,
    List<string> KeyFactors
);