using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Tagging;

namespace ShelfAware.Llm;

/// <summary>
/// LLM-backed second-stage tag dedup (<see cref="ITagAdvisor"/>): asks the model whether a new tag is a
/// synonym of an existing one. Cheap, pinned Haiku, single short call. Fails open (returns null) so a
/// flaky API never blocks tag creation.
/// </summary>
public class AnthropicTagAdvisor : ITagAdvisor
{
    private readonly AnthropicClient _client;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicTagAdvisor> _logger;

    public AnthropicTagAdvisor(IOptions<LlmOptions> options, ILogger<AnthropicTagAdvisor> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient { ApiKey = _options.ApiKey };
    }

    public async Task<string?> FindSynonymAsync(string candidate, IReadOnlyList<string> existing, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(candidate) || existing.Count == 0) return null;
        try
        {
            var prompt =
                $"A grocery app tags products. The user is creating a new tag: \"{candidate.Trim()}\".\n" +
                "Existing tags:\n- " + string.Join("\n- ", existing) + "\n\n" +
                "If the new tag means essentially the SAME thing as one of the existing tags (a synonym — " +
                "e.g. \"Soda\" and \"Soft Drink\", \"Cleaner\" and \"Detergent\"), reply with that existing " +
                "tag EXACTLY as written above and nothing else. If it is genuinely different, reply with only: NONE";

            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _options.ExtractionModel,
                MaxTokens = 32,
                Messages = [new() { Role = Role.User, Content = prompt }],
            }, cancellationToken: cancellationToken);

            var reply = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
            return existing.FirstOrDefault(t => string.Equals(t, reply, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            // Fail open — never block tag creation on an API hiccup — but leave a trail so a
            // silently-degraded dedup (e.g. a bad key or rate limit) is visible in the logs.
            _logger.LogWarning(ex, "Tag synonym check failed for \"{Candidate}\"; failing open.", candidate.Trim());
            return null;
        }
    }
}
