# LLM coach backend (2026-10-03)

First slice of the owner's priority #3 ("more guidance and structure, an LLM coach on my OpenAI
credits"). It builds the plan §5.3 provider abstraction and a weekly coach that turns LTFI's
deterministic weekly data plus check-in answers into a structured coaching card. It is
**optional and display-only**: with no key the app starts normally and shows a setup hint, and
nothing the coach says is written to the DB (plan rules 8/9). The weekly check-in form/gate is a
separate slice built by another agent. This slice only defines the input DTOs that slice will fill.

## What shipped

- **Core abstractions** (`LTFI.Core/Abstractions`)
  - `ILlmProvider` (`IsConfigured`, `Model`, `CompleteAsync(LlmRequest)`), `LlmRequest` (system
    prompt, user content, optional `LlmJsonSchema`), `LlmResponse` (text, model, `LlmUsage`), and
    `LlmException` with an `LlmFailureKind` (NotConfigured / Transport / Refused / Incomplete /
    MalformedOutput). All of them are soft failures.
  - `IApiKeyStore` (get/set/clear + `KeySource`).
  - `ICoachService.AnalyzeWeekAsync(CoachInput)` → `CoachReport`. `CoachInput` = week range +
    `WeeklyReview` + `DayActivity[]` + `EvidenceLine[]` + `CheckInAnswer(Question, Answer)[]` +
    last week's commitments + streak. `CoachReport` mirrors the `ltfi_weekly_coach` schema (headline,
    wins, patterns, risks, last-week review, ≤3 commitments with first step / when-where / if-then,
    drop-or-pause, question to sit with, data caveat). It also carries `Model`, `Usage` and `RawJson`
    (ready for `ReflectionEntry.StructuredSummaryJson` once the user confirms the card).
- **Infrastructure** (`LTFI.Infrastructure/Llm`)
  - `OpenAiProvider`: OpenAI **Responses API** (`POST {BaseUrl}/responses`) over plain
    `HttpClient` + System.Text.Json, with no SDK. Sends `store:false`, strict `json_schema` output and
    `reasoning.effort`. It walks `output[] → message → content[] → output_text`, maps
    refusal / `status:"incomplete"` / non-JSON to `LlmException`, and retries once on 429/5xx.
    The key is read per call, so a newly saved key works without a restart. Error messages are
    scrubbed of the key.
  - `DpapiApiKeyStore`: `%AppData%/LTFI/openai.key`, encrypted with DPAPI (CurrentUser,
    entropy `LTFI.v1`). The `OPENAI_API_KEY` env var wins when set. The key is never stored in
    SQLite and never logged.
  - `LlmSettings`: optional `%AppData%/LTFI/llm-settings.json` (`enabled`, `baseUrl`, `model`,
    `reasoningEffort`, `timeoutSeconds`, `maxOutputTokens`). A missing or corrupt file falls back to
    the defaults (`gpt-6.1-sol`, effort `low`, 60s), so there is no migration.
  - `CoachPrompts`: the pinned system prompt (verbatim from research) and the schema, versioned
    `weekly-coach-v1`.
  - `CoachService`: builds `<checkin_data>{json}</checkin_data><answers>Q/A…</answers>`, with
    snake_case fields, ISO week key, hours rounded to 0.1 and evidence capped at 40 lines. It
    parses leniently: strips a code fence, tolerates missing fields, coerces bad enums to safe
    defaults, clamps list sizes and clamps `first_step_minutes` to 1-10.
  - DI: settings, key store, provider and coach are registered unconditionally (singletons).
- **Review page hook**: a **COACH** section with an `ASK COACH` button. It builds a `CoachInput` from
  the page's weekly review + last-7-day evidence/daily activity + focus streak (no answers yet) and
  renders the card: wins, patterns, last week, suggested commitments, risks, pause/drop, the
  question, and model/token meta labelled "suggestions only". Without a key it shows a clear
  "OpenAI key not configured — set OPENAI_API_KEY" panel with a paste-and-save box (DPAPI).
- Package: `System.Security.Cryptography.ProtectedData` 9.0.9 (Infrastructure).

## Key decisions / gotchas

- **Raw HTTP over the OpenAI NuGet**: it's one POST, needs zero dependencies, and any
  OpenAI-compatible base URL (Ollama/LM Studio-style proxies) works by changing `baseUrl`.
- **Integer `minimum`/`maximum` removed from the schema**. Not every strict-mode endpoint accepts
  them, so `first_step_minutes` is clamped in C# instead. `minItems`/`maxItems` are kept.
- **Week key = ISO week of the window's *end***: a trailing 7-day review ending today closes out
  the current week (a Sun→Sat window would otherwise be labelled with the previous week).
- **MockLlmProvider and FakeHandler live in the test project**, not shipped. No test touches
  the network.
- Model ids and prices move fast. The default (`gpt-6.1-sol`, roughly $0.05 per weekly call per
  the research) is only a default, and `llm-settings.json` overrides it. Re-check pricing before
  quoting it.

## Verification

- `dotnet build LTFI.sln`: 0 warnings, 0 errors.
- `dotnet test LTFI.sln`: **40 passed** (27 existing + 13 new in `CoachTests`). The new tests cover:
  the prompt carries the numbers/answers/schema and the card is parsed; empty answers →
  "data alone" note; malformed output (prose / array / truncated JSON) → `MalformedOutput`;
  lenient parse (fence, missing fields, bad enum, clamp); unconfigured fails fast with no call;
  provider request shape (URL, bearer, `store:false`, strict schema, reasoning) and the output
  walk + usage; one retry on 5xx with no key leak; no key → NotConfigured; refusal/incomplete
  mapping; DPAPI round-trip + env-var precedence + encrypted at rest; settings defaults /
  partial / corrupt.
- Smoke-launched the app: DI resolves and it starts with no key configured.
- **Not verified live**: no real OpenAI call was made (by design). The first real call should
  confirm that strict mode accepts the schema as written.

## Deferred / follow-ups

- Weekly check-in flow (separate slice): fill `CoachInput.Answers` / `LastWeekCommitments`, and
  add Accept/Edit/Drop per commitment, then persist the confirmed card into
  `ReflectionEntry.StructuredSummaryJson` + emit `ReflectionSubmitted`.
- "Send titles to coach" redaction toggle and a first-use privacy notice.
- Regenerate cap (≈3/week) and a cost readout. Daily nudges (cheap model) are out of scope here.
- Once iCloud Reminders sync lands, add reminder stats to the payload (the schema/prompt already
  mention reminders).
- A Settings page for model/base URL. Today it's the JSON file plus the paste-key box on Review.
