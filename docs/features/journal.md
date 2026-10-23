# The meal journal

Built 2026-10-08. Read this when working on `/journal`, the `log_meal` / `query_journal` chat tools,
`MealJournal` (Core/Journal), or anything per-member.

## What it is

Tell Reginald what you ate — typed on the Journal page, held-to-talk, or to the roaming voice agent
anywhere in the app — and it lands in your journal: one row per food (`JournalEntry`), filed under a
meal (the meal planner's `MealSlot`: Breakfast, Lunch, Dinner, Snack) on a day. `/journal` shows the
month as whole Sunday–Saturday weeks with a calorie total per day, per week (an eighth column) and for
the month, and the selected day's meals underneath, where any food can be corrected, removed (with a
one-step undo) or added by hand.

## The first per-person data in the app

Everything else in the pantry belongs to the household. A journal belongs to a **person** — two members
logging into one household see two journals. That is a structural guard, not a WHERE clause:

- `IMemberOwned : IHouseholdOwned` marks the table. `ShelfAwareDbContext.MemberId` is the signed-in
  person's Identity user id; `ApplyMember<T>` gives the table a query filter on household **and**
  member, and `EnforceMember` stamps the member on insert and refuses an insert, update or delete for
  any other member (or with no member at all) — the same stamp-and-refuse the household gets, one level
  down, for the same reason: EF builds updates and deletes from the primary key alone.
- `MemberId` on the entity is non-nullable (`""` default) so a context with no member folds the filter
  to FALSE and reads nothing — the `AppSetting` trick.
- `CurrentHousehold` now also implements `ICurrentMember`, resolving the person from the **same**
  principal as the household. A household pinned with `UseFixed` and no member pin names **nobody**
  (background work must not borrow an ambient sign-in), and an API token never names a person
  (its `NameIdentifier` records who minted it). `HouseholdInitializer` pins both, which is what lets
  the voice agent's detached loop log a meal.
- Deliberately **no activity-log entry**: History is household-wide, so it would put one member's meals
  on the other's screen. Removing a row on the page is its undo.
- Export and "delete all my data" act on the **caller's** journal only. A household reset by one member
  does not erase another member's private record; they have the same button for their own.
- ⚠️ Open: removing a member from a household (`HouseholdService.RemoveMemberAsync`) leaves their
  journal rows in that household's database, invisible to everyone. They reappear if the person rejoins.

## Calories — one story

- Reginald estimates calories for the portion described (`log_meal`'s `calories`), and the entry is
  marked `CaloriesEstimated`. A number the person **said** (`calories_stated`) or **typed** on the page
  is theirs and is not an estimate; editing an estimated number on the page makes it theirs.
- **A correction is remembered** (2026-10-23, after a 7 Brew drink came in at half its real calories).
  Any number that is the person's own — said, typed, or corrected on the page (`MealJournal.HasOwnNumber`,
  one expression for the database filter and the in-memory check) — becomes the number for that food: the
  next time the same food is logged without a number of its own (Reginald's estimate, a saved recipe's
  figure, or a blank typed by hand), the write path uses theirs and stores it as theirs
  (`MealJournal.Recall`, applied in `MealJournalService.LogAsync`, so the voice and the page's add form
  behave the same). The rule is checked on the number actually written, after a recall.
- "Same food" is `MealJournal.FoodKey`: case and spacing don't matter, anything else does — a medium and a
  large are different foods, and a saved recipe logged at other than one serving is logged as
  "Chicken Chili, 3 servings" so one bowl's number is never recalled for three.
- "Their latest number" is ordered by `JournalEntry.CaloriesGivenAt` — when the number was given, stamped
  when a write gives one (`MealJournal.GivesNumber`: a new own number, an estimate made theirs, or their
  number moved to another food; moving the meal or the day gives nothing). Correcting last month's entry
  today makes today's number the remembered one. Added by `AdditiveSchema` and backfilled from `LoggedAt`
  for own-number rows that predate it. The page says "next time you log X, it'll be N" only when the write
  path reports it gave a number (`JournalWrite.Remembered`), so the screen can't promise what the engine
  won't do.
- There is no separate table: the journal is the memory, so export and delete already cover it, and
  removing every entry with a number forgets it. The page's undo uses `RestoreAsync`, which neither
  recalls nor re-stamps, so a removed entry comes back exactly as it was and can't displace a newer number.
- **No portion, no guess** (2026-10-23, Jordan: "if i forget to say the size of a portion it should ask
  me"). `log_meal` takes the amount in the person's own words — `portion`, or for a saved recipe that
  priced it, `servings` — and without one writes nothing and tells the model to ask
  (`AnthropicPantryChat.AskPortion`, which also tells it to log only that food once answered). Excused
  only when the amount is settled: they said the calories, or the food is one they've given their own
  number for (`MealJournal.Find`, the same match the recall uses). Servings with no recipe that priced
  them excuse nothing — they'd be ignored and the model's guess written. The portion joins the food's
  name (`MealJournal.WithPortion`) unless the name already says it, so "a small chiller" can't recall the
  large's number. The page's add-by-hand form is unaffected.
- **A bigger size never estimates lower** (same day: 32 oz came back below the 16 oz estimate — the
  model's own inconsistency; nothing in the app scales calories). `MealJournal.SizeConflict` compares an
  ESTIMATE's ounces (read off the name, `Ounces`) with every logged size of the same food
  (`SizelessKey` drops ounces and size words); a bigger cup priced lower than a smaller one, or the
  reverse, is refused on the write path with both numbers named so the model re-estimates. A number the
  person gave is never checked. Only names that state ounces are compared — "large" alone carries no
  size the code can rank.
- Reginald is given the person's remembered foods (the 40 most recent) in the prompt and told to reuse
  those names exactly, since recall only matches the same food. For a chain's drink or menu item he is
  told to price that chain's own size and put the size he assumed in the food name — the likely cause of
  the halved 7 Brew drink is a generic-cup guess against 7 Brew's much larger cups (unconfirmed: the
  logged row wasn't inspected).
- A food that is one of the household's saved recipes is logged at the recipe's own
  `EstimatedCaloriesPerServing` × servings — the figure the Reports tab's calories-cooked chart uses —
  rather than a fresh guess, so two screens never price the same dish differently.
- Every total comes from `MealJournal.Total`, which counts uncounted items separately (never as zero)
  and marks the total `~` when any counted part is an estimate. The page's day cells, week column and
  month header, the per-meal list, and Reginald's `query_journal` answer (`MealJournal.Describe`) all
  read it. "This week" is `MealCalendar.WeekStart` — the same Sunday the meal-plan grid uses.
- `MealJournal.Problem` is the one write rule (food 1–200 chars, calories 0–10,000 or blank, not a
  future day), asked by `MealJournalService` itself so no surface can skip it.

## Telling Reginald from the Journal page

- **Corrections by chat or voice** (2026-10-23): `edit_meal` changes one entry — size, food, calories,
  meal or day — named by the `[entry N]` that `MealJournal.EntryLine` puts on every entry Reginald is
  shown (the page's screen context, `log_meal`'s answer, `query_journal`'s day listing), so an id read in
  one is the id the tool takes. Only what was said changes; a different food with no new number takes
  the person's own number for it or must be re-estimated, never the old food's calories under a new
  name. The write is `MealJournalService.UpdateAsync`, the page's own edit, so the rule applies.
- **The page is the context**: `MealJournal.ScreenContext` tells Reginald the day open and its entries;
  food told without a "when" goes on that day. The typed box, hold-to-talk and the roaming voice agent
  (through `VoiceCoordinator.ScreenContext`, cleared when the page goes away) all get the same text.
- **"It" means what was just said**: the page's box keeps the last two exchanges (`RecentTurns`, shared
  by typing and hold-to-talk) and replays them with each turn. A failed turn is not kept.

## Cost

No new billable action. Logging or asking by voice or the text box is an ordinary chat turn
(`ServiceAction.ChatTurn`); a turn that logged a meal has written something the person can see, so it
stays charged even if the provider drops afterwards (`TurnWrites.Mark`). Adding, editing and removing on
the page call no AI and cost nothing.

## Not in this version

- Eating a food does not touch pantry stock or signals (the "Ate it" recipe button still does that,
  and still records its household-level `MealEvent`). The prompt says so explicitly.
- No calorie goals or targets.
