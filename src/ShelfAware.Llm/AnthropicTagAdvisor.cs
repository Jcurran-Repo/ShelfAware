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
            // ⚠️ The loose pass is TagVocabulary's, not ours. "Which existing tag does this name mean?"
            // is the question that file says it is the one place for, and it knows things a local helper
            // does not: collapsed whitespace, a trailing plural "s", one character of typo. A private
            // reading lived here for one commit and knew only about a trailing period, so a reply of
            // "Soft Drinks" against a household's "Soft Drink" returned null and coined the duplicate —
            // which Upload.razor's plain-code stage, eight lines before the call that charged for this,
            // would have caught. Two readings of one question is the repo's most expensive defect shape.
            //
            // The exact spelling still wins where there is one, so a household holding both "Etc" and
            // "Etc." gets back the one the model actually named rather than the first near-duplicate.
            return existing.FirstOrDefault(t => string.Equals(t, reply, StringComparison.OrdinalIgnoreCase))
                ?? TagVocabulary.FindNearDuplicate(reply, existing);
        }
        // ⚠️ WHOSE cancellation, and the unconditional version of this line was wrong in every
        // reachable case. A caller that cancelled is not this advisor's to absorb and is rethrown. A
        // provider TIMEOUT arrives as the same type and is a degraded provider — the case the catch
        // below exists for, whose log line is the operator's only signal that the dedup is silently
        // failing open. Not one call site passes a token today (Upload.razor, ProductDetail.razor,
        // Recipes.razor all take the CancellationToken.None default), so an unconditional rethrow
        // reclassifies 100% of real cancellations as caller intent — and since those sites are a
        // try/finally with no catch and the app has no ErrorBoundary, it escapes an @onclick handler
        // and tears down the Blazor circuit, losing an in-progress receipt review. The token decides.
        //
        // Billing is the same either way: nothing above has settled, so the act refunds.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Fail open — never block tag creation on an API hiccup — but leave a trail so a
            // silently-degraded dedup (e.g. a bad key or rate limit) is visible in the logs.
            _logger.LogWarning(ex, "Tag synonym check failed for \"{Candidate}\"; failing open.", candidate.Trim());
            return null;
        }
    }
}
