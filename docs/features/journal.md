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
- A food that is one of the household's saved recipes is logged at the recipe's own
  `EstimatedCaloriesPerServing` × servings — the figure the Reports tab's calories-cooked chart uses —
  rather than a fresh guess, so two screens never price the same dish differently.
- Every total comes from `MealJournal.Total`, which counts uncounted items separately (never as zero)
  and marks the total `~` when any counted part is an estimate. The page's day cells, week column and
  month header, the per-meal list, and Reginald's `query_journal` answer (`MealJournal.Describe`) all
  read it. "This week" is `MealCalendar.WeekStart` — the same Sunday the meal-plan grid uses.
- `MealJournal.Problem` is the one write rule (food 1–200 chars, calories 0–10,000 or blank, not a
  future day), asked by `MealJournalService` itself so no surface can skip it.

## Cost

No new billable action. Logging or asking by voice or the text box is an ordinary chat turn
(`ServiceAction.ChatTurn`); a turn that logged a meal has written something the person can see, so it
stays charged even if the provider drops afterwards (`TurnWrites.Mark`). Adding, editing and removing on
the page call no AI and cost nothing.

## Not in this version

- Eating a food does not touch pantry stock or signals (the "Ate it" recipe button still does that,
  and still records its household-level `MealEvent`). The prompt says so explicitly.
- No calorie goals or targets.
