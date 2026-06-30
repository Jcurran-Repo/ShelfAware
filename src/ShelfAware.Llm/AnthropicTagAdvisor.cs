using Anthropic;
using Anthropic.Models.Messages;
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

    public AnthropicTagAdvisor(IOptions<LlmOptions> options)
    {
        _options = options.Value;
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
        catch
        {
            return null; // fail open — never block tag creation on an API hiccup
        }
    }
}
