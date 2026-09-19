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
        // ⚠️ Refused HERE, above the scope, and not only on the screen that calls it. The commit that
        // added the cap filtered the vocabulary on the next line and left the candidate — the thing the
        // household actually types — unbounded, and Upload.AddTag reaches this method with no length
        // check of its own: FindNearDuplicate answers null for an over-long candidate, AddTag reads null
        // as "genuinely new", and control falls straight through to this call. A four-megabyte tag box
        // (the SignalR message limit, since maxlength is a client-side hint) therefore became roughly a
        // million input tokens on an act priced at one flat credit, per click, repeatable — a cost
        // amplifier on a box that pays for its own tokens. A guard on the screen alone is one edit away
        // from being gone; this one no caller can reopen.
        if (TagVocabulary.IsOverLength(candidate)) return null;
        await using var action = AiActionScope.Begin(ServiceAction.TagSuggest);
        try
        {
            var prompt =
                $"A grocery app tags products. The user is creating a new tag: \"{candidate.Trim()}\".\n" +
                // Entries past the cap are not tags (see TagVocabulary.IsOverLength) and a stored one
                // would otherwise ride into every prompt untruncated, on an act priced at a flat credit.
                // ⚠️ The shared predicate. This line was written as `e.Length <= MaxLength` — untrimmed,
                // where every other site measures the trimmed form — in the same commit whose comment in
                // TagVocabulary said a third arithmetic here would be the same defect again.
                "Existing tags:\n- " + string.Join("\n- ", existing.Where(e => !TagVocabulary.IsOverLength(e))) + "\n\n" +
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } // whose cancellation: see ProviderCancellationSiteTests
        catch (Exception ex)
        {
            // Fail open — never block tag creation on an API hiccup — but leave a trail so a
            // silently-degraded dedup (e.g. a bad key or rate limit) is visible in the logs.
            _logger.LogWarning(ex, "Tag synonym check failed for \"{Candidate}\"; failing open.", candidate.Trim());
            return null;
        }
    }
}
