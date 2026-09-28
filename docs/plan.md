# MoynaPay — build plan

Read AGENTS.md first. Phases marked [DONE] are finished and must not be redone; read their code
before building on them. [PARTIAL] means ported code already exists: extend it, do not rewrite it.

## Phase 1: Domain, order lifecycle, shop API, signing [DONE]
Done: order lifecycle (62 checks), Msisdn normalisation, idempotent POST /v1/orders, GET /v1/orders/{ref},
POST /v1/orders/{ref}/cancel, signing middleware (replay, tamper, stale, unsigned), outbox rows written,
YoPay signing vectors verified from tests/MoynaPay.Check/fixtures/yopay-vectors.json.
Known gaps handled later: secret protection (phase 5), merchant/key API (phase 4), rate limit (phase 12).

## Phase 2: EF Core + Postgres [DONE]
Done: MoynaPayDbContext, reflection-applied tenant filter, configurations, enums as names, row_version
concurrency token, nonces in the database, unique (tenant_id, reference) where not deleted, migration Initial.

## Phase 3: Outbox delivery [DONE]
Done: dispatcher with per-order ordering, retry ladder 10s/1m/5m/30m/2h/6h/24h (8 attempts), dead-letter,
410 stops / 404 retries, lease-based claim (claimed_by, claimed_until), X-MoynaPay-Delivery id,
webhook URL checked as string and at connect time (private ranges blocked), 148 checks.
Pending on the developer machine: migration OutboxDelivery.

## Phase 4: Merchant, subscription, API key issue and revoke [DONE]
Done: merchant onboarding creates merchant + subscription (calls/payments/courier flags), API credentials
are issued with public KeyId and one-time plaintext secret, stored through ISecretProtector as SecretCipher,
keys list without secrets, revoke makes that key fail lookup immediately while other keys keep working,
and `mp_dev_demo` is seeded only in Development. Checks cover create → issue → signed call works,
revoked key lookup fails, other key still works, and bootstrap issue is first-key only.
Pending with the EF/Postgres store: migration for the same rows.

## Phase 5: Key ring — AES-GCM secret protector
- Port YoPay's key ring: versioned keys, AES-GCM, key id in the cipher text, rotate without re-encrypting everything.
- Implement ISecretProtector with it; migrate plaintext secrets from phase 4 on first start.
- Done when: round-trip, tamper detection, old-key decrypt after rotation are checked; no plaintext secret in DB or logs.

## Phase 6: Webhook endpoint — register, test, rotate
- API to register/update the shop webhook URL (validated with WebhookUrl rules), send a signed test event, rotate the signing secret.
- Write WebhookEndpoint.LastDeliveredAt and LastFailureReason from the dispatcher.
- Done when: register → test event delivered → rotate → old secret rejected after grace period; checks added.

## Phase 7: YoAIWorkflow integration — OrderConfirmation workflow
- Bring in YoAIWorkflow (Shohag's own SDK) and model order confirmation as a workflow: new order → call/pay steps → outcome.
- Every decision passes through the workflow; controllers never change order state directly.
- Done when: an order goes through the workflow on the in-memory runner; checks cover the main paths.

## Phase 8: Durable runner + Postgres session store
- Persist workflow sessions so a restart continues where it stopped; one runner owns a session at a time (lease).
- Done when: kill the process mid-workflow, restart, the order continues exactly once; migration added.

## Phase 9: Action handlers — notify shop, invoice, courier (idempotent)
- Handlers for "notify shop" (outbox), "create invoice", "book courier", each with a stable action id.
- Running a handler twice must have the effect of once.
- Done when: duplicate execution checks pass for all three.

## Phase 10: AI proposal gate
- AI may only PROPOSE (confirm / needs human / reschedule) with a confidence; business rules decide.
- Below threshold or against a rule → review queue. AI never executes an action itself and never rejects.
- Done when: checks prove a high-confidence "reject" still goes to a human.

## Phase 11: Review queue — claim, expiry, outcome
- Queue of orders needing a person: claim with expiry, release, outcome (confirm / reject / call again), audit.
- Done when: two reviewers cannot claim the same item; expired claims return; only a person can reject.

## Phase 12: App auth — OTP, token, refresh, logout, rate limit
- /app/v1 login by phone OTP, access + refresh tokens, logout revokes refresh, rate limits on OTP and shop API.
- Done when: brute force is limited, refresh rotation works, logout invalidates; checks added.

## Phase 13: App bootstrap
- One call returning what the app needs at start: merchant, enabled services, settings summary, counters.

## Phase 14: Home numbers
- Today/7-day counts: new, confirmed, needs human, paid, shipped, failed calls; cheap queries with indexes.

## Phase 15: Orders list, detail, timeline
- Paged list with filters (status, date, search by phone/reference), detail, full event timeline.

## Phase 16: Order actions from the app
- Decision (confirm/reject by merchant), recall (undo a machine "no"), claim for review, mark shipped.
- All through the workflow; rejects only by a person.

## Phase 17: Settings
- Calls (hours, retries, script), payments (wallets, matching mode), courier, webhook, API keys, subscription, profile.
- Only the modules the merchant subscribed to are exposed.

## Phase 18: Devices — pair, heartbeat, push token
- Pair the Android app with a one-time token, heartbeat endpoint, store push token.

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

## Phase 23: Dialler loop + calling window [PARTIAL]
Existing: CallingHours in CallRules.cs.
- Voice worker picks due orders, respects each merchant's hours and time zone, limits concurrency.

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
