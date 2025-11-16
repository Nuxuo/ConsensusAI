namespace ConsensusAI.Models;

/// <summary>
/// Configuration options for the analysis cache
/// </summary>
public class CacheOptions
{
    /// <summary>
    /// Gets or sets the cache duration in minutes
    /// </summary>
    public int AnalysisCacheDurationMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets whether the analysis cache is enabled
    /// </summary>
    public bool EnableAnalysisCache { get; set; } = true;
}

public record StockRequest(
    List<string> Tickers,
    AnalysisMode Mode = AnalysisMode.Evaluate,
    int DiscussionRounds = 2,
    string? Context = null,
    decimal PortfolioValue = 100000m,
    bool EnableWebSearch = false
);

public record AnalysisResult(
    List<string> Tickers,
    AnalysisMode Mode,
    string? Context,
    List<AgentMessage> Conversation,
    Dictionary<string, StockRecommendation> Recommendations,
    string Summary
);

public record StockRecommendation(
    string Ticker,
    StockAction Action,
    string Reasoning,
    int? Rank = null
);

public record AgentMessage(string AgentName, string Message, DateTime Timestamp);

public enum AnalysisMode
{
    Evaluate,        // Should I buy each of these stocks?
    Compare,         // Which stock is best?
    Rank,            // Rank all stocks from best to worst
    PickOne,         // Choose the single best stock to buy
    PortfolioReview, // I own these - should I hold or sell?
    BuyOrSell,       // For each: BUY, SELL, or HOLD?
    Diversify        // Which combination makes a good portfolio?
}

public enum StockAction
{
    StrongBuy,
    Buy,
    Hold,
    Sell,
    StrongSell,
    Avoid
}