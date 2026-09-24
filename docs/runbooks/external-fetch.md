# Runbook: downloading user-chosen URLs (iCal import) without SSRF

Task FD-16 (audit defects A2-21, A4-10, A9-32). The code is in place; nothing has to be configured for the
defaults. This page says what the backend does and which Railway variables can change the limits. What happens after
the download (valid or empty feed, cancelled and recurring events, per-feed isolation) is in [ical.md](ical.md) (PC-10).

## What the backend does

Property and supplier iCal feeds are URLs typed by users and downloaded by the server. Every such download goes
through `ISafeExternalHttpClient` (`Casazen.Infrastructure/Http/SafeExternalHttpClient.cs`); no other HTTP client
may be used for a user-chosen URL.

| Concern | Behaviour | Code |
|---|---|---|
| URL check (on save and before each request) | `https` only, port in the allowed list (443), no `user:password@`, no internal host names (`localhost`, single-label names, `*.local`, `*.internal`, `*.localhost`, `*.localdomain`, `*.arpa`), no literal IP of a blocked range (also `2130706433`, `0x7f.1` and similar spellings) | `Http/ExternalUrlPolicy.cs` |
| Blocked addresses | Private (10/8, 172.16/12, 192.168/16), loopback, link-local 169.254/16 (cloud metadata `169.254.169.254`), CGNAT 100.64/10, 0/8, multicast, reserved, documentation and benchmarking ranges; IPv6 outside global unicast `2000::/3` (so `::1`, ULA `fc00::/7` used by the Railway private network, link-local `fe80::/10`, multicast, IPv4-mapped `::ffff:…`), 6to4, Teredo and NAT64 of a blocked IPv4 | `ExternalUrlPolicy.IsBlockedAddress` |
| DNS rebinding | The host is resolved inside `SocketsHttpHandler.ConnectCallback`; if **any** resolved address is blocked the connection is refused, otherwise the socket connects to the checked address and the connected peer is checked again. There is no other lookup that could return a different answer | `SafeExternalHttpClient.ConnectAsync` |
| Proxy | Not used (`UseProxy = false`), so the check always sees the real destination | `SafeExternalHttpClient.CreateHandler` |
| Redirects | Followed by hand, at most 3; each target passes the URL check again and then the connect check | `SafeExternalHttpClient.GetStringAsync` |
| Time | One budget of 15 s for the whole download (connection, redirects, body) | same |
| Size | Body read as a stream and aborted past 5 MB (a larger `Content-Length` is refused before reading) | same |
| Property import | `POST /api/properties/{id}/ical/import-url` validates and saves the URL, sets status `Syncing` and answers **202** at once; the first download runs in the Hangfire job `PropertyICalSyncJob.SyncFeedAsync` (one per property at a time). The recurring `property-ical-sync` job (every 15 min) keeps syncing it | `PropertiesController`, `BackgroundJobs/PropertyICalSyncJob.cs` |
| Supplier import | `PUT /api/supplier/calendar/ical` validates the URL (`SupplierService.UpdateCalendarSyncAsync`); the download uses the same client (`CalendarSyncService`) | `SupplierProfileController` |
| Errors shown to users | Stable codes, never exception messages: `ical_invalid_url` (400 on save), `ical_unreachable`, `ical_too_large`, `ical_invalid_format` (not a readable iCalendar; a valid calendar with no events is a success, PC-10), `ical_sync_failed`. The sync stores the code in `PropertyICalFeed.LastError` / `SupplierProfile.CalendarSyncError`; the status APIs return it as `lastErrorCode` / `calendarSyncErrorCode` plus the localized text (`SharedResources*.resx`, keys `ICal*`) in `lastError` / `calendarSyncError`. A blocked destination is reported as `ical_unreachable`, like a DNS failure, so the error does not reveal which internal names exist. Errors saved before FD-16 are shown as `ical_sync_failed` | `Services/ICalErrorCodes.cs`, `Web/Infrastructure/ICalErrorMessages.cs` |
| Logs | The reason (blocked address, redirect, HTTP status, size) is logged with the host name and the property / supplier org id. The full URL is **not** logged: iCal export links carry secret tokens | same files |

## Configuration (Railway variables, optional)

| Variable | Meaning | Default |
|---|---|---|
| `SafeExternalHttp__AllowedPorts__0`, `__1`, … | Ports a URL may use (array form: one variable per port) | `443` only |
| `SafeExternalHttp__TimeoutSeconds` | Time budget of one download (1–120) | `15` |
| `SafeExternalHttp__MaxResponseBytes` | Largest body read | `5242880` (5 MB) |
| `SafeExternalHttp__MaxRedirects` | Redirects followed (0–10) | `3` |

Setting the ports replaces the default list, so include `443` when adding another port.

## Checks after a deploy

1. As a host, save `https://169.254.169.254/latest/meta-data/` as iCal import URL of a property: the API answers
   400 with `code: "ical_invalid_url"`.
2. Save a real Airbnb/Booking export URL: the API answers 202 with `lastImportStatus: "Syncing"`; within a few
   seconds `GET /api/properties/{id}/ical/status` shows `Success` (or a `lastErrorCode`) and the Hangfire dashboard
   shows the `PropertyICalSyncJob.SyncFeedAsync` job.
3. As a supplier, `PUT /api/supplier/calendar/ical` with `http://169.254.169.254/` answers 400 `ical_invalid_url`.
