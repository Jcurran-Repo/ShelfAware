# Recipes, tags and substitutes

Extracted from `CLAUDE.md` on 2026-09-19. Read this when working on `/recipes`, `/cookbook`,
the tag vocabulary, `IngredientMatcher`, `SwapCloud`, or the adapt/swap flows.


**Two-layer categories** — one primary store-aisle `Category` (enum, unchanged, drives
grocery-list order) PLUS free-form `ProductTag`s (a many-per-product child table; `Product.Tags`).
The `category` was re-framed in the extraction prompt to STORE AISLE (first-aid→PersonalCare,
canned/condiment/shelf-stable→Pantry, cleaners/paper→Household); brand-defined items keep their
brand (the Velveeta over-strip fix). **Two-stage tag dedup:** plain-code
`TagVocabulary.FindNearDuplicate` (near-dup guard, Core, unit-tested) → then, only if that finds
nothing, `ITagAdvisor.FindSynonymAsync` (`AnthropicTagAdvisor`, Haiku synonym check, **fails open**).
Extraction is fed the **live tag vocabulary** (seed ∪ stored) via `ExtractAsync(…, knownTags)` so
the model reuses tags instead of coining near-dupes (dedup-at-source). UI: per-line tag editor on
Upload review (chips + datalist), tag chips on Product Detail linking to `/products?tag=`, and a
clickable **tag cloud** on Products that filters the grid (deep-linkable `[SupplyParameterFromQuery]
?tag=`) + per-row mini chips.

**Recipes (`/recipes`)** — an inventory-aware recipe helper (P1 `ff1fd83`, P2 `612fcbd`).
`IRecipeAdvisor`/`AnthropicRecipeAdvisor` (structured output, ChatModel/Haiku) takes an NL request
("what can I make?"), reasons over on-hand products (tracked & not-Overdue) and hard-excludes a
persistent **won't-eat** list (`ExcludedFood`). Suggestions list main ingredients vs. seasonings
separately and are saveable. **Key learning:** the LLM can't self-report availability, so the advisor
returns a grounded `matched_product` per ingredient (exact on-hand product name or null), captured
**once at save time** and persisted on `RecipeIngredient.MatchedProduct`. Makeability = **plain-code**
check that all MAIN ingredients' matched product is currently on-hand ("Ready to make"/"Missing items"
badges). Also: **"Ate it"** (`Recipe.TimesEaten`), **"Pick for me"** (random from saved + eaten +
makeable), and **"Add missing to list"** → the new `GroceryExtra` **Extras** section on `/list` (which
also filled a real gap — the list had no manual-add before). A label-check disclaimer is shown (not
allergy-safe medical advice). Entities: `ExcludedFood`, `Recipe`, `RecipeIngredient(IsMain,
MatchedProduct)`, `GroceryExtra`.

**Makeability by food family (v2.2).** Recipes stay SPECIFIC ("chicken breast", real cook times); the
flexibility lives on products. Each product has an **"Also works as"** list (`ProductSubstitute` child
rows, `Product.Substitutes`) — the recipe ingredients it can stand in for ("Chicken Breast Tenderloins"
also works as "chicken breast", "chicken cutlet"). `IngredientMatcher` (Core, unit-tested, replaces the
old exact-`MatchedProduct` check) covers a main ingredient when its core words (only trivial modifiers —
fresh/frozen/boneless/size/unit — stripped; cut/form words KEPT) appear in an on-hand product's **name OR
a substitute phrase** — so tenderloins cover "chicken breast" but "Whole Chicken" and "Chicken Broth" do
NOT. Recipe on-hand is **edible only** (excludes Household/PetCare/PersonalCare, so "Chicken Jerky Dog
Treats" can't count as chicken). Substitutes are **AI-seeded** (`IProductSubstituteAdvisor` →
`AnthropicProductSubstituteAdvisor`, Haiku, fails soft) + user-curated: an ✨ Suggest button on Product
Detail, and the **`suggest_substitutes` chat/voice tool** (`IPantryStore.AddSubstitutesAsync`) so the
assistant fills them in from anywhere. `ProductSubstitutes` is an additive table (CREATE TABLE IF NOT
EXISTS in Program.cs for existing DBs).

**Adapt to what you have (v2.2).** A saved recipe can be rewritten to use on-hand ingredients: the AI swaps
missing main(s) for ones you have and **rewrites the steps + cook times** (thighs cook longer than breast),
saved as a **variant** (`Recipe.ParentRecipeId`, additive column) grouped under the original on the Recipes
page. On-demand only (no AI calls on load). One orchestration path — `IRecipeAdapter` (Core) →
`RecipeAdapter` (Web, scoped; loads the recipe + on-hand + excluded, calls `IRecipeAdvisor.AdaptAsync`,
saves the variant) — drives the "🔀 Adapt to what I have" button, the **`adapt_recipe` chat/voice tool**,
AND the per-ingredient bubble cloud. Adapt prompt is `recipe-adapt-system.txt`. On-hand = the shared
`PantryOnHand.EdibleInStock` (Core; `CategoryExtensions.IsEdible` + not-overdue). **Robustness:** re-adapting
**dedupes by main-ingredient content signature** (not the AI's title) so it updates in place; variants are
saved only when valid, and the adapter logs + re-throws cancellation (no swallowed errors). **As of
2026-07-12:** the advisor receives each on-hand product's also-works-as list (item 11), and adapting a
VARIANT is allowed — it re-roots under the original (see item 11) instead of refusing.

**Bubble-cloud ingredient picker (v2.2).** Each main ingredient on a saved recipe (originals AND, since
2026-07-12, variants) has a **⇄ swap** that opens a cloud of interchangeable forms
(`IIngredientAlternativesAdvisor`, Haiku; generated once and **cached** on
`RecipeIngredient.AlternativesJson`), colored green/red via `IngredientMatcher`. Since 2026-07-12 the cloud
is `SwapCloud.Merge(curated, generated)` — the user's own stand-in products lead, AI forms dedupe behind
them (item 11). Clicking a bubble runs a **targeted adapt** — a typed
`IngredientSwap(IngredientName, ChosenForm)` the adapter turns
into the prompt preference AND **guards**: if the model ignores the pick, `IngredientMatcher.IsMentionedIn`
catches it and the adapt is rejected (retry) rather than saving a mislabeled variant.

