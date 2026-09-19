using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Tagging;
using ShelfAware.Core.Billing;

namespace ShelfAware.Llm;

/// <summary>
/// LLM-backed second-stage tag dedup (<see cref="ITagAdvisor"/>): asks the model whether a new tag is a
/// synonym of an existing one. Cheap, pinned Haiku, single short call. Fails open (returns null) so a
/// flaky API never blocks tag creation.
/// </summary>
public class AnthropicTagAdvisor : ITagAdvisor
{
    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicTagAdvisor> _logger;

    public AnthropicTagAdvisor(IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicTagAdvisor> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> FindSynonymAsync(string candidate, IReadOnlyList<string> existing, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(candidate) || existing.Count == 0) return null;
        await using var action = AiActionScope.Begin(ServiceAction.TagSuggest);
        try
        {
            var prompt =
                $"A grocery app tags products. The user is creating a new tag: \"{candidate.Trim()}\".\n" +
                "Existing tags:\n- " + string.Join("\n- ", existing) + "\n\n" +
                "If the new tag means essentially the SAME thing as one of the existing tags (a synonym — " +
                "e.g. \"Soda\" and \"Soft Drink\", \"Cleaner\" and \"Detergent\"), reply with that existing " +
                "tag EXACTLY as written above and nothing else. If it is genuinely different, reply with only: NONE";

            var options = new ChatOptions { ModelId = _options.ExtractionModel, MaxOutputTokens = 32 };
            var response = await _chat.GetResponseAsync(prompt, options, cancellationToken);

            var reply = response.Text.Trim();
            if (!ProviderReply.IsAnAnswer(reply)) return null;
            // ⚠️ Settled on the REPLY, before anything is made of it. "None of your tags mean this" is
            // the model's considered answer to what the household asked, and an answer is paid for; only a
            // provider that said nothing at all is refunded.
            action.Answered();
            if (ProviderReply.IsNothingFound(reply)) return null;

            // ⚠️ The sentinel is checked even though "NONE" matches no tag in almost every household —
            // almost. Nothing stops one naming a tag "None", and then the model's way of saying "these
            // are different" comes back as a synonym for whatever was typed.
            //
            // The exact spelling wins where there is one, so a household with both "Etc" and "Etc." gets
            // back the one the model actually named; ProviderReply.Names is the looser second pass, and
            // its remarks say why a single reading of the reply cannot serve here.
            return existing.FirstOrDefault(t => string.Equals(t, reply, StringComparison.OrdinalIgnoreCase))
                ?? existing.FirstOrDefault(t => ProviderReply.Names(reply, t));
        }
        // ⚠️ Cancellation is not a provider failure and is not this advisor's to absorb: a household
        // that closed the tab must not see it logged as a degraded API, and the act is refunded either
        // way because nothing above settled. Rethrown rather than caught so the caller's own
        // cancellation path runs — the fourth advisor in this set always did, and three did not.
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Fail open — never block tag creation on an API hiccup — but leave a trail so a
            // silently-degraded dedup (e.g. a bad key or rate limit) is visible in the logs.
            _logger.LogWarning(ex, "Tag synonym check failed for \"{Candidate}\"; failing open.", candidate.Trim());
            return null;
        }
    }
}
