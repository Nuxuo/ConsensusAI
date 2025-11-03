namespace ConsensusAI.Models;

public class StockData
{
    public string Ticker { get; set; } = string.Empty;
    public bool DataAvailable { get; set; } = true;

    // Price Data
    public decimal CurrentPrice { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal PreviousClose { get; set; }
    public decimal High52Week { get; set; }
    public decimal Low52Week { get; set; }

    // Technical Indicators
    public decimal MovingAverage50 { get; set; }
    public decimal MovingAverage200 { get; set; }
    public decimal RSI { get; set; }
    public long Volume { get; set; }
    public long AvgVolume { get; set; }

    // Performance
    public decimal YTDReturn { get; set; }
    public decimal OneYearReturn { get; set; }

    // Sentiment (from news analysis)
    public string SentimentRating { get; set; } = "N/A";

    // Metadata
    public DateTime LastUpdated { get; set; }
    public string DataSource { get; set; } = string.Empty;
}

public record ApiError(string Message, string? Detail = null, Dictionary<string, object>? Metadata = null);