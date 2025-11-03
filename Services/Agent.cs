using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ConsensusAI.Models;

namespace ConsensusAI.Services;

public class Agent
{
    public string Name { get; }
    public string SystemPrompt { get; }

    public Agent(string name, string systemPrompt)
    {
        Name = name;
        SystemPrompt = systemPrompt;
    }

    public async Task<string> GetResponse(Kernel kernel, string tickers, ChatHistory sharedHistory, AnalysisMode mode)
    {
        var agentHistory = new ChatHistory(SystemPrompt);

        // Add shared conversation context
        foreach (var message in sharedHistory)
        {
            agentHistory.Add(message);
        }

        var modeGuidance = mode switch
        {
            AnalysisMode.Compare => " Focus on comparing these stocks against each other.",
            AnalysisMode.Rank => " Consider how you would rank these stocks.",
            AnalysisMode.PickOne => " Argue for which ONE stock is the best choice.",
            AnalysisMode.PortfolioReview => " Evaluate whether to hold or sell each position.",
            AnalysisMode.BuyOrSell => " Decide whether to buy, sell, or hold each stock.",
            AnalysisMode.Diversify => " Consider which stocks work well together for diversification.",
            _ => ""
        };

        agentHistory.AddUserMessage($"Provide your analysis of these stocks: {tickers}.{modeGuidance} Keep it concise (3-4 sentences).");

        var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();
        var response = await chatCompletion.GetChatMessageContentAsync(agentHistory);

        return response.Content ?? "No response";
    }

    public async Task<string> GetFollowUp(Kernel kernel, ChatHistory sharedHistory)
    {
        var agentHistory = new ChatHistory(SystemPrompt);

        foreach (var message in sharedHistory)
        {
            agentHistory.Add(message);
        }

        var chatCompletion = kernel.GetRequiredService<IChatCompletionService>();
        var response = await chatCompletion.GetChatMessageContentAsync(agentHistory);

        return response.Content ?? "No response";
    }
}