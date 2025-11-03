using ConsensusAI.Models;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ConsensusAI.Services;

public interface IStockDataService
{
    Task<StockData> GetStockDataAsync(string ticker, CancellationToken cancellationToken = default);
    Task<Dictionary<string, StockData>> GetMultipleStocksAsync(List<string> tickers, CancellationToken cancellationToken = default);
}

public class EodhdStockDataService : IStockDataService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<EodhdStockDataService> _logger;
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    public EodhdStockDataService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<EodhdStockDataService> logger,
        IMemoryCache cache)
    {
        _httpClient = httpClient;
        _apiKey = configuration["Eodhd:ApiKey"]
            ?? throw new InvalidOperationException("EODHD API key not configured");
        _logger = logger;
        _cache = cache;

        _httpClient.BaseAddress = new Uri("https://eodhd.com/api/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<StockData> GetStockDataAsync(string ticker, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"eodhd_{ticker}_{DateTime.UtcNow:yyyyMMddHH}";

        if (_cache.TryGetValue<StockData>(cacheKey, out var cachedData))
        {
            _logger.LogDebug("Cache hit for {Ticker}", ticker);
            return cachedData!;
        }

        try
        {
            _logger.LogInformation("Fetching EODHD data for {Ticker}", ticker);

            var stockData = new StockData
            {
                Ticker = ticker.ToUpperInvariant(),
                DataAvailable = true,
                LastUpdated = DateTime.UtcNow,
                DataSource = "EODHD"
            };

            var realtimeTask = GetRealtimeDataAsync(ticker, cancellationToken);
            var sma50Task = GetTechnicalIndicatorAsync(ticker, "sma", 50, cancellationToken);
            var sma200Task = GetTechnicalIndicatorAsync(ticker, "sma", 200, cancellationToken);
            var rsiTask = GetTechnicalIndicatorAsync(ticker, "rsi", 14, cancellationToken);
            var avgVolTask = GetTechnicalIndicatorAsync(ticker, "avgvol", 50, cancellationToken);
            var eodTask = GetEodDataAsync(ticker, cancellationToken);
            var sentimentTask = GetSentimentDataAsync(ticker, cancellationToken);

            await Task.WhenAll(realtimeTask, sma50Task, sma200Task, rsiTask, avgVolTask, eodTask, sentimentTask);

            MergeRealtimeData(stockData, await realtimeTask);
            MergeTechnicalIndicators(stockData, await sma50Task, await sma200Task, await rsiTask, await avgVolTask);
            MergeEodData(stockData, await eodTask);
            MergeSentimentData(stockData, await sentimentTask);

            _cache.Set(cacheKey, stockData, CacheDuration);
            return stockData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch data for {Ticker}", ticker);
            return new StockData
            {
                Ticker = ticker.ToUpperInvariant(),
                DataAvailable = false,
                LastUpdated = DateTime.UtcNow
            };
        }
    }

    public async Task<Dictionary<string, StockData>> GetMultipleStocksAsync(
        List<string> tickers,
        CancellationToken cancellationToken = default)
    {
        var tasks = tickers.Select(t => GetStockDataAsync(t, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.Ticker, r => r);
    }

    private async Task<JsonDocument?> GetRealtimeDataAsync(string ticker, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"real-time/{ticker}.US?api_token={_apiKey}&fmt=json";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Real-time data unavailable for {Ticker}: {Status}", ticker, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching real-time data for {Ticker}", ticker);
            return null;
        }
    }

    private async Task<JsonDocument?> GetTechnicalIndicatorAsync(string ticker, string function, int period, CancellationToken cancellationToken)
    {
        try
        {
            var from = DateTime.UtcNow.AddDays(-365).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var url = $"technical/{ticker}.US?function={function}&period={period}&from={from}&to={to}&api_token={_apiKey}&fmt=json&filter=last_{function}";

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Technical indicator {Function} unavailable for {Ticker}: {Status}", function, ticker, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching {Function} for {Ticker}", function, ticker);
            return null;
        }
    }

    private async Task<JsonDocument?> GetEodDataAsync(string ticker, CancellationToken cancellationToken)
    {
        try
        {
            var from = DateTime.UtcNow.AddDays(-365).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var url = $"eod/{ticker}.US?api_token={_apiKey}&fmt=json&from={from}&to={to}";

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("EOD data unavailable for {Ticker}: {Status}", ticker, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching EOD data for {Ticker}", ticker);
            return null;
        }
    }

    private async Task<JsonDocument?> GetSentimentDataAsync(string ticker, CancellationToken cancellationToken)
    {
        try
        {
            var from = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var url = $"sentiments?s={ticker}.US&from={from}&to={to}&api_token={_apiKey}&fmt=json";

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Sentiment data unavailable for {Ticker}: {Status}", ticker, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error fetching sentiment for {Ticker}", ticker);
            return null;
        }
    }

    private void MergeRealtimeData(StockData stockData, JsonDocument? data)
    {
        if (data == null) return;

        var root = data.RootElement;
        stockData.CurrentPrice = GetDecimal(root, "close");
        stockData.OpenPrice = GetDecimal(root, "open");
        stockData.PreviousClose = GetDecimal(root, "previousClose");
        stockData.Volume = GetLong(root, "volume");

        var change = GetDecimal(root, "change");
        if (stockData.PreviousClose > 0)
        {
            stockData.YTDReturn = change / stockData.PreviousClose;
        }
    }

    private void MergeTechnicalIndicators(StockData stockData, JsonDocument? sma50, JsonDocument? sma200, JsonDocument? rsi, JsonDocument? avgVol)
    {
        if (sma50 != null && sma50.RootElement.ValueKind == JsonValueKind.Number)
        {
            stockData.MovingAverage50 = sma50.RootElement.GetDecimal();
        }

        if (sma200 != null && sma200.RootElement.ValueKind == JsonValueKind.Number)
        {
            stockData.MovingAverage200 = sma200.RootElement.GetDecimal();
        }

        if (rsi != null && rsi.RootElement.ValueKind == JsonValueKind.Number)
        {
            stockData.RSI = rsi.RootElement.GetDecimal();
        }

        if (avgVol != null && avgVol.RootElement.ValueKind == JsonValueKind.Number)
        {
            stockData.AvgVolume = (long)avgVol.RootElement.GetDecimal();
        }
    }

    private void MergeEodData(StockData stockData, JsonDocument? data)
    {
        if (data == null) return;

        try
        {
            var priceData = data.RootElement.EnumerateArray()
                .Select(e => new
                {
                    Date = GetString(e, "date"),
                    Close = GetDecimal(e, "adjusted_close"),
                    High = GetDecimal(e, "high"),
                    Low = GetDecimal(e, "low")
                })
                .Where(p => p.Close > 0)
                .OrderByDescending(p => p.Date)
                .ToList();

            if (!priceData.Any()) return;

            var yearData = priceData.Take(252).ToList();
            stockData.High52Week = yearData.Any() ? yearData.Max(p => p.High) : 0;
            stockData.Low52Week = yearData.Where(p => p.Low > 0).Any() ? yearData.Where(p => p.Low > 0).Min(p => p.Low) : 0;

            if (priceData.Count > 1)
            {
                var currentPrice = priceData[0].Close;

                if (priceData.Count >= 252)
                {
                    var yearAgoPrice = priceData[251].Close;
                    if (yearAgoPrice > 0)
                        stockData.OneYearReturn = (currentPrice - yearAgoPrice) / yearAgoPrice;
                }

                var ytdData = priceData.Where(p => p.Date.StartsWith(DateTime.UtcNow.Year.ToString())).ToList();
                if (ytdData.Any())
                {
                    var ytdStartPrice = ytdData[^1].Close;
                    if (ytdStartPrice > 0)
                        stockData.YTDReturn = (currentPrice - ytdStartPrice) / ytdStartPrice;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing EOD data for {Ticker}", stockData.Ticker);
        }
    }

    private void MergeSentimentData(StockData stockData, JsonDocument? data)
    {
        if (data == null) return;

        try
        {
            if (data.RootElement.TryGetProperty($"{stockData.Ticker}.US", out var tickerData))
            {
                var sentiments = tickerData.EnumerateArray()
                    .Select(e => GetDecimal(e, "normalized"))
                    .Where(s => s != 0)
                    .ToList();

                if (sentiments.Any())
                {
                    var avgSentiment = sentiments.Average();

                    stockData.SentimentRating = avgSentiment switch
                    {
                        > 0.3m => "Strong Positive News",
                        > 0.1m => "Positive News",
                        > -0.1m => "Neutral News",
                        > -0.3m => "Negative News",
                        _ => "Strong Negative News"
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing sentiment for {Ticker}", stockData.Ticker);
        }
    }

    private decimal GetDecimal(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetDecimal();
            if (prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString()?.Replace("%", "").Replace("$", "");
                if (decimal.TryParse(value, out var result))
                    return result;
            }
        }
        return 0;
    }

    private long GetLong(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetInt64();
            if (prop.ValueKind == JsonValueKind.String &&
                long.TryParse(prop.GetString(), out var result))
                return result;
        }
        return 0;
    }

    private string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) &&
               prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;
    }
}