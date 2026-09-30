# MoynaPay — build plan

Read AGENTS.md first. Phases marked [DONE] are finished and must not be redone; read their code
before building on them. [PARTIAL] means ported code already exists: extend it, do not rewrite it.

**Mark a phase [DONE] only after reading the code it claims to have written.** On 29 September
phases 2 and 3 were marked [DONE] in this file while neither existed in the repository - no
`Persistence/` folder, no `PackageReference` to EF Core or Npgsql anywhere in the solution, and
`MoynaPay.Infrastructure.csproj` still carrying the comment "Phase 2 adds Npgsql here. Until
then...". Phases 4 to 17 were then built on top of that claim. They work, and they hold every
merchant, order, API key and token in memory, so a restart loses all of it.

A status line that is ahead of the code does not save time. It spends it, later, on somebody
else.

## Phase 1: Domain, order lifecycle, shop API, signing [DONE]
Done: order lifecycle (62 checks), Msisdn normalisation, idempotent POST /v1/orders, GET /v1/orders/{ref},
POST /v1/orders/{ref}/cancel, signing middleware (replay, tamper, stale, unsigned), outbox rows written,
YoPay signing vectors verified from tests/MoynaPay.Check/fixtures/yopay-vectors.json.
Known gaps handled later: secret protection (phase 5), merchant/key API (phase 4), rate limit (phase 12).

## Phase 2: EF Core + Postgres [NOT DONE]
Nothing of this exists in the repository. There is no `src/MoynaPay.Infrastructure/Persistence/`,
no `MoynaPayDbContext`, no configuration, no migration, and no `PackageReference` to EF Core or
Npgsql in any project. `DependencyInjection.AddMoynaPay` throws if a connection string is set.

Everything runs on `MemoryDatabase`, a singleton of dictionaries: merchants, orders, API
credentials, app tokens, OTP challenges, workflow sessions, rate limits. A restart loses all of
it, and two processes do not see each other's data at all.

Scope is now larger than it was when this line was first written. Phases 4-17 added
`IInvoiceStore`, `IWorkflowSessionStore`, `IWorkflowActionStore`, `IAppAuthStore` and
`IRateLimitStore`; every one of them needs an EF implementation, a configuration and a place in
the migration. Do not copy a phase-2 design that only knew about phase 1.

Do the transition race (see below) BEFORE this lands: the fix differs by store, and doing it
after means writing it twice.

## Phase 3: Outbox delivery [NOT DONE]
`Worker.Outbox` still logs "Delivery arrives in phase 2" and its loop is empty. There is no
dispatcher, no retry ladder, no dead-letter, and no 410 handling. `OutboxMessage` has
`Attempts`, `NextAttemptAt`, `DeliveredAt` and `LastFailureReason` but no `ClaimedBy`,
`ClaimedUntil` or `IsDead`, so nothing can lease a row.

Outbox rows ARE written by `OrderTransitionService`. They are simply never read by anyone, so
no shop has ever been told anything.

`HttpWebhookSender` exists, but it sends one `webhook.test` on demand from phase 6 - it is not
the dispatcher.

## Open defects, found 29 September

Fixed in this pass, each with a check that fails without the fix (174 checks):
- `/v1/merchants/{id}/api-keys` was unsigned and ungated - a merchant id, which appears in
  logs, URLs and webhook bodies, was enough to be issued that shop's signing secret. Both
  onboarding routes now need `X-MoynaPay-Operator`; the path list is `OperatorRoutes`.
- `AesGcmSecretProtector` fell back to a key printed in this repository, in every environment.
  It now refuses to start outside Development with no key ring configured.
- Webhook delivery followed redirects and never checked the address a host name resolves to.
  `WebhookGuard` closes both, and failure reasons are described rather than quoted back, so a
  merchant cannot map an internal network one webhook test at a time.
- A review claim had no upper bound; `claimSeconds: 2000000000` took an order out of the queue
  until 2090. Clamped to 30 minutes.
- Merchant creation did not check the owner phone for uniqueness. That phone IS the app login,
  so a second merchant on the same number takes over the first one's account.

Still open, in the order they should be taken:
1. **The transition race.** `OrderTransitionService.ApplyAsync` reads the order, checks the
   rules, mutates and saves, with no lock and no concurrency token, against singleton stores.
   Measured: a `Booked` order shipped and cancelled at once ends `Cancelled` 16 times in 30,000;
   two staff deciding a `NeedsHuman` order are BOTH told "Moved" 400 times out of 400, and the
   shop is queued `order.confirmed` and `order.rejected` for the same order. Fix with one port -
   `TryTransitionAsync(expected, to)` - that the memory store honours with a per-order lock and
   the EF store with `row_version`.
2. **`order.paid` can be sent with no `order.confirmed` before it.** `OrderLifecycle` line 41
   allows `Received → AwaitingPayment`, and `FirstStep` returns `AwaitingPayment` for a
   payments-without-calls merchant, so `Confirmed` is never on the path and `ConfirmedAt` is
   never stamped.
3. **Create is not idempotent for a padded reference.** `CreateOrderService` looks up the raw
   `Reference` and stores `Reference.Trim()`, so retrying `"ORD-9 "` throws
   `duplicate reference` - a 500, forever, on exactly the retry idempotency exists for.
4. **The review queue pages before it filters.** `ListAvailableAsync` takes the newest 50 and
   then drops claimed ones, so the oldest unreviewed orders are permanently invisible on a busy
   shop.
5. **`OrderWorkflowService.DecideAsync` returns `Moved` for a no-op** and re-records the event,
   which makes `ReviewDecisionOutcome.Unchanged` dead code. Its `session.Complete` set also
   omits `Rejected`, `Calling` and `Shipped`, so those sessions are re-leased forever once a
   worker hosts the runner.
6. **A workflow action that aborts before its side effect can never run again.** The `Started`
   row is written first and is never cleared on the `order-not-found` path or on a throw, so the
   courier is never booked and no retry fixes it.
7. **Refresh rotation is read-then-write**, so one refresh token can yield two valid families,
   and a reused token is not detected even though `ReplacedByHash` is recorded.
8. **Logout leaves the access token live** for up to 15 minutes.
9. **Nonces are kept for one tolerance window; a signature is valid for two.** A shop whose
   clock is a few minutes fast opens a replay gap.

## Phase 4: Merchant, subscription, API key issue and revoke [DONE]
Done: merchant onboarding creates merchant + subscription (calls/payments/courier flags), API credentials
are issued with public KeyId and one-time plaintext secret, stored through ISecretProtector as SecretCipher,
keys list without secrets, revoke makes that key fail lookup immediately while other keys keep working,
and `mp_dev_demo` is seeded only in Development. Checks cover create → issue → signed call works,
revoked key lookup fails, other key still works, and bootstrap issue is first-key only.
Pending with the EF/Postgres store: migration for the same rows.

## Phase 5: Key ring — AES-GCM secret protector [DONE]
Done: ISecretProtector now uses versioned AES-GCM ciphers with the key id in the cipher text,
new secrets use the active key, old keys remain decrypt-only for rotation, and plaintext
development secrets are migrated on first start. Checks cover round-trip, tamper detection,
old-key decrypt after rotation, and plaintext-to-AES migration.
Pending with the EF/Postgres store: database-backed migration for persisted plaintext rows.

## Phase 6: Webhook endpoint — register, test, rotate [DONE]
Done: signed API can register/update the shop webhook URL, reject local/private literal
URLs, send a signed `webhook.test` event through IWebhookSender, rotate the signing secret,
and update WebhookEndpoint.LastDeliveredAt / LastFailureReason from delivery results. Checks
cover register → signed test delivered, failure metadata, private URL rejection, and rotation
making old signatures fail.

## Phase 7: YoAIWorkflow integration — OrderConfirmation workflow [DONE]
Done: OrderConfirmation now runs through an in-memory workflow runner/session port:
new order starts a workflow session, confirmation advances to the next subscribed step
(payment/courier/end), unclear call outcomes move to human review, and illegal machine
decisions are refused by the workflow path. Shop API create/cancel routes now call the
workflow service instead of changing order state directly. Checks cover the main in-memory
paths. Pending for Phase 8: durable session storage and process-resume ownership.

## Phase 8: Durable runner + Postgres session store [DONE]
Done: workflow sessions now carry lease owner/until fields, the session store can atomically
lease one incomplete session to one runner, and OrderWorkflowRunner resumes interrupted
OrderConfirmation sessions exactly once. Checks cover lease contention, lease expiry, and
restart-style resume from a half-confirmed order into the next subscribed step.
Pending with the EF/Postgres store/package: database-backed session persistence and migration.

## Phase 9: Action handlers — notify shop, invoice, courier (idempotent) [DONE]
Done: workflow actions now have a stable action id and action store, with handlers for
notify shop (outbox row), create invoice, and book courier. A completed action is skipped
on repeat, so running a handler twice has the effect of once. Checks cover duplicate
execution for all three handlers.

## Phase 10: AI proposal gate [DONE]
Done: AI proposals now pass through AiProposalGate with confidence thresholds and lifecycle
checks. High-confidence confirm can execute through the workflow only when rules allow it;
low-confidence, reschedule/review, reject, or rule-breaking proposals all go to NeedsHuman.
Checks prove a high-confidence reject still goes to human review.

## Phase 11: Review queue — claim, expiry, outcome [DONE]
Done: orders in NeedsHuman now have a review queue service and signed API routes for list,
claim, release, and outcome. Claims are atomic with expiry, active claims hide from the
available queue, expired claims can be taken by another reviewer, and claim/release/outcome
events are audited. Review outcomes run through the workflow as a merchant/person action:
confirm, reject, or call again; machine rejection from review remains refused.

## Phase 12: App auth — OTP, token, refresh, logout, rate limit [DONE]
Done: `/app/v1/auth/otp`, `/app/v1/auth/token`, `/app/v1/auth/refresh`, and
`/app/v1/auth/logout` are wired. OTP requests and verification attempts are rate limited,
access/refresh tokens are opaque random values stored only as hashes, refresh rotates and
revokes the old token, logout invalidates refresh, and signed shop API calls now pass
through a per-key rate bucket after signature verification. Checks cover brute-force
limits, refresh replay, logout invalidation, access validation, and bucket reset.

## Phase 13: App bootstrap [DONE]
Done: `/app/v1/bootstrap` accepts the app bearer access token and returns the merchant
profile, enabled subscription services, settings summary (time zone, webhook, active API
key count), and current order status counters. The order store now has an exact
CountByStatus port so the bootstrap payload does not depend on a paged list. Checks cover
the bootstrap payload and invalid-token refusal.

## Phase 14: Home numbers [DONE]
Done: `/app/v1/home/numbers` accepts the app bearer token and returns today plus rolling
7-day counts for new orders, confirmed, needs human, paid, shipped, and failed calls.
Counts are read through a dedicated HomeMetrics order-store query over tenant + event time
+ event type/status, which is the index-friendly shape for the database store. Checks
cover today/7-day windows, failed calls, and tenant isolation.

## Phase 15: Orders list, detail, timeline [DONE]
Done: `/app/v1/orders` returns a paged app order list with status, date range, search,
take, and before-cursor filters. `/app/v1/orders/{reference}` returns order detail, and
`/app/v1/orders/{reference}/timeline` returns the full event timeline. All routes use
the app bearer token and stay scoped to the merchant. Checks cover filters, paging,
detail, timeline ordering, and tenant isolation.

## Phase 16: Order actions from the app [DONE]
Done: app routes can confirm/reject by merchant decision, recall a rejected order back
through confirmation, claim an order in review, and mark a booked order shipped. State
changes go through OrderWorkflowService, review claims reuse ReviewQueueService, and
rejects are recorded as merchant/person actions. Checks cover confirm with workflow
advance, reject actor, recall, claim contention, shipping, and shipping refusal.

## Phase 17: Settings [DONE]
Done: `/app/v1/settings` returns profile, subscription, webhook, API keys, and only the
subscribed module settings: calls, payments, and courier sections are omitted when the
merchant has not bought that module. `/app/v1/settings/profile` updates persisted profile
fields, and `/app/v1/settings/webhook` reuses the existing webhook URL guard. Checks cover
module visibility, profile updates, API key/webhook settings, and private webhook refusal.

## Phase 18: Devices — pair, heartbeat, push token [DONE]
Done: app-authenticated merchants can create a five-minute one-time pairing token;
the Android app consumes it once to register a device and receive a separate device
credential. Heartbeat and push-token updates authenticate with device id plus device
token, store health fields and push token without returning the push token itself, and
device listing stays merchant-scoped. Checks cover one-time pairing, expiry, heartbeat
auth/update, push token storage, and tenant isolation.

## Phase 19: ARI client merge + outbound correlation fix [PARTIAL]
Existing: src/MoynaPay.Infrastructure/Voice/AriClient.cs (ported).
- Originate outbound calls and keep the channel id ↔ order/call attempt link through every event.
- Done when: events for a call always land on the right attempt (checked with a fake ARI).

## Phase 20: DTMF — capture keypress [PARTIAL]
Existing: IvrDecision in Application/Voice/CallRules.cs.
- Receive DTMF events from ARI and feed IvrDecision; silence and wrong keys never become a rejection.

## Phase 21: Script engine + template rendering [PARTIAL]
Existing: CallScript and ScriptRenderer in CallRules.cs.
- Per-merchant scripts with order fields (name, amount, items), Bangla and English.

## Phase 22: TTS (Gemini) + cache + 8 kHz resample
- Gemini TTS behind the existing AI abstractions; cache rendered prompts; resample to 8 kHz slin for Asterisk.

## Phase 23: Dialler loop + calling window [DONE]
Done: voice worker now runs a dialler loop that atomically claims due call orders with a
lease, respects the existing 09:00-21:00 Dhaka calling window even on a UTC server,
registers the channel correlation before ARI originate, and lets expired claims be taken
by another worker. Checks cover outside-hours refusal, two diallers not taking the same
order, lease expiry after a dead dialler, and Confirmed/Rejected orders never being
dialled.

## Phase 24: Retry policy + attempt accounting [PARTIAL]
Existing: RedialPolicy in CallRules.cs.
- Record every attempt; retry per policy; after the last attempt → NeedsHuman, never rejected.

## Phase 25: Call outcome → OrderTransitionService
- Map call outcomes to order transitions through the workflow; idempotent per attempt.

## Phase 26: STT (Gemini) — listening path
- Transcribe customer speech with Gemini; keep keypress as the primary signal.

## Phase 27: LLM decision through the phase 10 gate
- LLM proposes from the transcript; the gate decides; low confidence → human.

## Phase 28: Human handoff + context summary
- Transfer to a person with a short summary of the call and order.

## Phase 29: Recording storage + playback URL
- Store recordings after the call (never blocking), signed time-limited playback URL.

## Phase 30: Trunk resolution per merchant
- Choose the SIP trunk/caller id per merchant.

## Phase 31: Quota circuit breaker + fallback
- When the speech API quota fails, stop calling it for a while and fall back to keypress-only prompts.

## Phase 32: bKash parser + corpus merge [PARTIAL]
Existing: Application/Payments/Parsing (MessageParser, BkashTemplates, BkashFieldReader) and fixtures/bkash/corpus.tsv.
- Merge the full YoPay corpus; every corpus line must parse to the expected fields.

## Phase 33: Ingest + device auth + pairing
- Endpoint for the Android app to post raw SMS/notification events, authenticated per paired device; dedupe raw events.

## Phase 34: Invoice + AmountAllocator [PARTIAL]
Existing: AmountAllocator, InvoiceWindow, ReferenceCode, TrxIdInput.
- Create invoices for orders with unique payable amounts inside the window.

## Phase 35: Matcher — lock, window, dedupe
- Match parsed transactions to invoices with FOR UPDATE SKIP LOCKED / advisory locks; one transaction pays one invoice.

## Phase 36: Settle → order Paid
- A matched payment moves the order to Paid through the workflow and notifies the shop once.

## Phase 37: Review queue for unmatched payments
- Unmatched or ambiguous payments go to a person; manual match with audit.

## Phase 38: Device heartbeat + offline alert
- Alert the merchant when a paired device stops reporting.

## Phase 39: Courier provider abstraction
- One interface for create consignment, cancel, status; idempotent by order.

## Phase 40: Steadfast
## Phase 41: Pathao + RedX
## Phase 42: Courier status tracking
- Poll or receive status updates and reflect them on the order timeline.

## Phase 43: Deploy — VPS, docker compose
- Compose for API, workers, Postgres; health checks, backups, rollback steps in README.

## Phase 44: Merchant onboarding end to end
## Phase 45: WooCommerce plugin
## Phase 46: SDKs — PHP, Node, .NET (share the YoPay signing vectors)
## Phase 47: Billing + usage metering
