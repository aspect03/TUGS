# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Development (incremental build + run)
dotnet run

# Hot-reload dev (preferred — rebuilds in 1-3s on code changes after first start)
dotnet watch run

# Build only
dotnet build

# Production publish (used by Render)
dotnet publish "IMAJINATION BACKUP.csproj" -c Release -o out
dotnet out/IMAJINATION\ BACKUP.dll
```

**Build output is redirected to `C:\IMAJINATIONbuild\` (outside OneDrive) via `Directory.Build.props`** — this is intentional to avoid OneDrive sync interference with the compiler. Do not delete `Directory.Build.props`. There are no automated tests in this project.

## Required Environment / Config

The app will not start without these. Supply them via a `.env` file in the project root or environment variables:

| Env var | Config key | Purpose |
|---|---|---|
| `ConnectionStrings__SupabaseConnection` | `ConnectionStrings:SupabaseConnection` | PostgreSQL connection string (Supabase) |
| `Auth__JwtSecret` | `Auth:JwtSecret` | JWT signing key (required outside Development) |
| `GoogleAuth__ClientId` | `GoogleAuth:ClientId` | Google OAuth client ID |
| `EmailSettings__SmtpServer` etc. | `EmailSettings:*` | SMTP for OTP/reset emails |

`appsettings.Development.json` enables `AppSecurity:ExposeDetailedErrors`. Google OAuth credentials go in `credentials.json` (see `credentials.example.json`).

## Architecture Overview

**Stack:** ASP.NET Core 9 (minimal hosting) + PostgreSQL via Npgsql directly (no ORM) + vanilla HTML/CSS/JS frontend. No React, no Blazor. The frontend is static files served from the project root by `UseStaticFiles`.

### Backend structure

**`Program.cs`** is the single composition root. It wires:
- JWT + session-token hybrid auth (`HybridAuth` policy scheme)
- CSRF antiforgery (skipped for same-origin fetch and certain public auth routes)
- Rate limiting (10 req/min for `/api/auth/*`, 120/min elsewhere)
- Static file serving directly from `ContentRootPath` (not `wwwroot`)
- URL rewrites for legacy paths (e.g. `/LandingPage.html` → `/pages/home/LandingPage.html`)
- Security headers middleware (CSP, X-Frame-Options, etc.)

**Controllers** are standard `[ApiController]` classes, all routes under `api/[controller]`. Each controller opens its own `NpgsqlConnection` per request — there is no shared DbContext. Schema is applied idempotently via `ALTER TABLE … ADD COLUMN IF NOT EXISTS` / `CREATE TABLE IF NOT EXISTS` called at request time (lazy schema migration pattern).

**Support classes** (not controllers, not services) live alongside controllers:
- `CommunitySupport` — community schema DDL, profile completion, verification status helpers
- `PlatformFeatureSupport` — shared business schema DDL, calendar blocks, booking conflict checks
- `NotificationSupport` — notifications table DDL + insert helpers
- `SecuritySupport` — input sanitisation, image data-URL validation, auth security state
- `ConfigurationFallbacks` — env-var-first config resolution with `__` → `:` key mapping

**Services** (DI singletons):
- `JwtTokenService` — issues/validates JWTs
- `SessionTokenAuthenticationHandler` — validates `X-Session-Token` header
- `MessageProtectionService` — encrypts booking messages using ASP.NET Data Protection
- `PaymentLedgerService` — `payment_records` table, idempotent `MarkPaidAsync`
- `PaymentRefundService` — refund workflow
- `TotpService` — TOTP/MFA
- `UploadScanningService` — image safety checks
- `AutomatedVerificationAssessmentService` — background artist verification scoring
- `BookingMessageStreamService` — long-poll message streaming
- `EmailService` — HTML email templates (booking lifecycle, ticket receipts, reminders, waitlist alerts, transfers)
- `TicketPdfService` — QuestPDF + QRCoder A5 landscape PDF ticket generation
- `EventReminderService` — `IHostedService` background job, hourly check for 24/48h pre-event reminders

**SignalR hubs:**
- `Hubs/EventScanHub.cs` — real-time ticket scan/sale broadcasts to admin scanner dashboard

### Auth flow

Two parallel schemes under the `HybridAuth` policy:
1. **JWT** (`IMAJINATION-ACCESS` cookie or `Authorization: Bearer`) — standard claims-based, 120-min expiry
2. **Session token** (`X-Session-Token` header or `IMAJINATION-SESSION` cookie) — validated against DB via `SessionTokenAuthenticationHandler`

CSRF validation applies to all non-GET API routes except: public auth POSTs, same-origin fetch requests (detected via `Sec-Fetch-Site: same-origin` + `X-Requested-With: fetch`), and booking message streaming.

### Database conventions

- All tables use `uuid` primary keys generated client-side (Guid.NewGuid()).
- Timestamps are `timestamptz` (UTC).
- No migrations framework — schema is always evolved with `IF NOT EXISTS` DDL in the relevant controller/support class.
- Booking notes/messages are encrypted at rest via `MessageProtectionService` before DB insert.
- Payments use idempotency keys via `checkout_reference_hash` in `payment_records`.

**Notable tables (beyond core):** `event_tiers`, `event_waitlist`, `ticket_transfers`, `booking_disputes`, `event_reminder_log`.

**Critical Npgsql gotcha:** Never pass `Array.Empty<T>()` or an empty array as an Npgsql parameter — Npgsql cannot infer the PostgreSQL type and throws `InvalidCastException` at runtime. Instead, restructure the query to avoid the array parameter entirely (e.g. delete without an `= ANY(@ids)` clause, then re-insert individually).

### User roles

`users.role` is one of: `Customer`, `Artist`, `Sessionist`, `Organizer`, `Admin`. Role-specific profile fields (`stagename`, `genres`, `spotify_link`, `talent_category`, etc.) are on the shared `users` table. The `Admin` role maps to the `ProfileAdmin.html` dashboard and has elevated access in all controllers.

**Sessionist/Artist legacy issue:** Early registrations stored `Sessionist` role as `Artist` in the DB. `SessionistDashboard.html` accepts both `Artist` and `Sessionist` in its auth check. `navbar-profile-menu.js` detects Sessionists via `localStorage.getItem('sessionistDashboard')` flag set on login/registration. New registrations correctly store `Sessionist`.

### Frontend structure

```
pages/
  auth/          — login, signup (per role), forgot-password
  browse/        — public browse pages (Artists, Sessionists, Events, Community)
  dashboards/    — per-role dashboards (Artist, Customer, Organizer, Sessionist, ProfileAdmin)
  details/       — detail pages (ArtistDetails, EventDetailPage, etc.)
  bookings/      — Checkout, messages
  home/          — LandingPage
  tools/         — dashboardscanner (QR ticket scanner)
assets/js/
  system-dialogs.js      — shared alert/confirm/prompt modal system
  performance-helpers.js — shared perf utilities (skips prod fallback URLs on localhost)
  navbar-notifications.js / navbar-profile-menu.js — shared nav components
  theme.js               — light/dark mode toggle (ImajiTheme.toggle()), FOUC prevention
  mfa-settings.js        — MFA UI component
sw.js                    — PWA service worker (network-first nav, cache-first assets)
manifest.json            — PWA manifest (app name: "Tugs!")
```

Pages are self-contained HTML files that call the API directly via `fetch`. Authentication state is read from `localStorage` (`userId`, `userRole`, `userEmail`). All user-facing strings passed into innerHTML must go through `escapeHtml()` (defined inline in each page) to prevent XSS.

Light/dark mode is driven by the `html.light-mode` CSS class. `theme.js` manages persistence (`localStorage`) and FOUC prevention. Light-mode overrides live at the bottom of `assets/css/style.css`.

### Admin dashboard (`pages/dashboards/ProfileAdmin.html`)

Single-page admin panel with section-based navigation (`switchAdminSection`). Sections: Overview, Verification, Moderation, Analytics, Bookings, Payments, Reports, Security, Events, Scan Tickets. The Events section includes a Create/Edit modal with:
- Canvas-based image compression (auto-compresses any uploaded poster)
- Lineup management (registered artist/sessionist search with role + genre filters, plus external artist support with `Guid.Empty` id)
- Per-event analytics modal that fetches from `GET /api/event/organizer/{orgId}/analytics/{eventId}` and renders Chart.js charts (donut, bar+line timeline, tier breakdown)
- QR ticket scanner module (uses `html5-qrcode`, calls `POST /api/ticket/scan`)

### Event lineup

Artist/sessionist lineups are stored as JSON columns (`artist_lineup`, `sessionist_lineup`) on the `events` table and also in junction tables (`event_artists`, `event_sessionists`). External performers (not registered on the platform) use `Guid.Empty` as their id — `NormalizeLineup()` in `EventController` deduplicates them by `external:{role}:{name}` key.

### Ticket tiers

Per-event tiers live in the `event_tiers` table. `SyncTiersAsync` in `EventController` owns all writes: it deletes zero-sales tiers then re-upserts. The once-flag `_tierSchemaReady` (SemaphoreSlim-guarded) prevents redundant DDL. `FetchTiersAsync` reads them back. The `GET /api/event/{id}/tiers` endpoint is a standalone fallback used by the frontend if the main event response returns empty tiers (e.g. legacy events).

### Payment flow

Bookings and tickets both use PayMongo for checkout. The ledger (`payment_records`) tracks every payment with idempotency. `PaymentLedgerService.MarkPaidAsync` is the single write path. Escrow-style booking payments are held until both parties confirm completion; `PaymentRefundService` handles cancellation.

## Deployment

Deployed on **Render** (free tier). Config in `render.yaml`. Environment variables are set in the Render dashboard — use `__` as the hierarchy separator (e.g. `Auth__JwtSecret`). The app handles `PORT` env var from Render and binds to `0.0.0.0:{PORT}`.
