using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Bing;

namespace ConsensusAI.Services;

public class WebSearchService
{
    private readonly ILogger<WebSearchService> _logger;
    private readonly string? _bingApiKey;

    public WebSearchService(IConfiguration configuration, ILogger<WebSearchService> logger)
    {
        _logger = logger;
        _bingApiKey = configuration["Bing:ApiKey"];
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_bingApiKey);

    public void AddWebSearchToKernel(Kernel kernel)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Bing API key not configured. Web search disabled.");
            return;
        }

        try
        {
            var bingConnector = new BingConnector(_bingApiKey);
            var webSearchPlugin = new WebSearchEnginePlugin(bingConnector);
            kernel.ImportPluginFromObject(webSearchPlugin, "WebSearch");
            _logger.LogInformation("Web search plugin enabled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize web search plugin");
        }
    }

    public async Task<string> SearchAsync(Kernel kernel, string query, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return "Web search not available - Bing API key not configured";
        }

        try
        {
            var function = kernel.Plugins["WebSearch"]["Search"];
            var result = await kernel.InvokeAsync(function, new() { ["query"] = query }, cancellationToken);
            return result.ToString() ?? "No results";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Web search failed for query: {Query}", query);
            return $"Search failed: {ex.Message}";
        }
    }
}