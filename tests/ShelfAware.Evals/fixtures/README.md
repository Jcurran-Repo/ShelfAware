# Eval fixtures

Each fixture is a receipt image plus its hand-labelled ground truth:

```
walmart-paper.jpg            # the receipt image (png/jpg/jpeg/webp/gif/pdf)
walmart-paper.expected.json  # the lines it should extract
```

`<name>.expected.json` is an array of expected lines (non-product lines like
subtotal/tax are omitted — they shouldn't be extracted):

```json
[
  { "NormalizedName": "Great Value Whole Milk", "Quantity": 1, "Category": "Dairy" },
  { "NormalizedName": "Bananas", "Quantity": 2.31, "Category": "Produce" }
]
```

Run from the repo root (writes results where the Accuracy page reads them):

```
set Llm__ApiKey=sk-ant-...
dotnet run --project tests/ShelfAware.Evals -- tests/ShelfAware.Evals/fixtures src/ShelfAware.Web/wwwroot/eval-results.json
```

Scoring (DESIGN.md §9): line recall = matched/expected, precision =
matched/found, field accuracy = matched lines with correct quantity + category.
Names are matched fuzzily by the token **containment coefficient** (|A∩B| /
min(|A|,|B|)) ≥ 0.6 — robust to descriptor-word differences in product names.
Targets: ≥90% recall, ≥90% precision, ≥85% field accuracy. Set `EVAL_VERBOSE=1`
to print every matched pair + unmatched line (handy for spotting wobble).
