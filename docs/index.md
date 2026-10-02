# CasaZen — Project Documentation

> Full-stack vacation rental management platform for the Italian market. Backend: ASP.NET Core 10 REST API. Frontend: React 19 SPA.

---

## Backend (ASP.NET Core 10)

| Document | Purpose | Audience |
|---|---|---|
| [BUSINESS.md](BUSINESS.md) | Domain entities, business processes, rules, glossary | Product Owner, Business Analyst, stakeholders |
| [TECHNICAL.md](TECHNICAL.md) | Architecture, API reference, data model, design patterns, infrastructure | Backend developers |
| [PROJECT.md](PROJECT.md) | Compressed AI context — stack, layout, conventions, gotchas | AI agents, onboarding developers |
| [INFRA.md](INFRA.md) | Hosting setup: Supabase + Railway + Vercel, multi-env release, release bundles. **Hosting in revisione (2026-10-02): Railway cancellato dal PO, la parte Railway è storica; vedi [runbooks/free-hosting-analysis.md](runbooks/free-hosting-analysis.md) (task HOSTING)** | DevOps, backend developers |
| [runbooks/index.md](runbooks/index.md) | Index of the 44 runbooks created by the 2026 remediation plan (auth, billing, storage, compliance, LTR, SEO, CI, E2E), by area and task | DevOps, backend developers, Product Owner |
| [../Sessions/risanamento/RIEPILOGO-FINALE.md](../Sessions/risanamento/RIEPILOGO-FINALE.md) | Final summary of the remediation (what was done per area, what stays open and why, decisions, how to bring it to `develop`) | Product Owner, all developers |
| [runbooks/deploy-checklist.md](runbooks/deploy-checklist.md) | Every deploy variable of backend, frontend and mobile (where, required or not, effect when missing), ordered pre-deploy blockers, checked against the code | Product Owner, DevOps |
| [AI-STRATEGY.md](AI-STRATEGY.md) | AI positioning, AI roadmap for the PMS core, and the AI-powered supplier & services marketplace vision | Product Owner, founders, architects |
| [integrations/ai-assistants-plan.md](integrations/ai-assistants-plan.md) | Plan (no code yet) to integrate CasaZen with ChatGPT and Claude through a remote MCP server, MCP Apps UI and plugins: documentation research with sources, architecture, security/privacy, publication path, tasks AI-01..AI-12, open questions (Italian) | Product Owner, architects, backend developers |

### Backend quick links

- [Domain entities](BUSINESS.md#domain-entities)
- [Business processes](BUSINESS.md#business-processes)
- [Glossary](BUSINESS.md#glossary)
- [Architecture diagram](TECHNICAL.md#architecture)
- [API reference](TECHNICAL.md#api-reference)
- [Data model](TECHNICAL.md#data-model)
- [Design patterns](TECHNICAL.md#design-patterns)
- [Background jobs](TECHNICAL.md#background-jobs)
- [Where to find things (backend)](PROJECT.md#where-to-find-things)
- [Gotchas (backend)](PROJECT.md#non-obvious-rules--gotchas)

---

## Frontend (React 19 + TypeScript)

| Document | Purpose | Audience |
|---|---|---|
| [FRONTEND-TECHNICAL.md](FRONTEND-TECHNICAL.md) | Architecture, routing, API layer, state management, component patterns | Frontend developers |
| [FRONTEND-PROJECT.md](FRONTEND-PROJECT.md) | Compressed AI context — stack, layout, conventions, gotchas | AI agents, onboarding developers |

### Frontend quick links

- [Routing table](FRONTEND-TECHNICAL.md#routing)
- [API layer](FRONTEND-TECHNICAL.md#api-layer)
- [State management](FRONTEND-TECHNICAL.md#state-management)
- [Authentication flow](FRONTEND-TECHNICAL.md#authentication)
- [Component architecture](FRONTEND-TECHNICAL.md#component-architecture)
- [Where to find things (frontend)](FRONTEND-PROJECT.md#where-to-find-things)
- [Gotchas (frontend)](FRONTEND-PROJECT.md#non-obvious-rules--gotchas)

---

## Auth0 Setup

- [AUTH0_SETUP.md](AUTH0_SETUP.md) — step-by-step Auth0 tenant and application configuration for both backend and frontend

---

## AI Agent Scanning Index

| File | Summary | Tags |
|---|---|---|
| [BUSINESS.md](./BUSINESS.md) | Domain entities (Property, Booking, Guest, Payment, OtaIntegration), business rules (CIN validation, tourist tax, GDPR erasure), and business processes (booking lifecycle, OTA sync, Alloggiati Web reporting). | domain, entities, business-rules, compliance, processes |
| [TECHNICAL.md](./TECHNICAL.md) | Layered .NET 10 architecture (Web → Core → Infrastructure), full API endpoint catalog, EF Core data model, design patterns (Repository, Adapter, Mediator), Auth0 + Stripe + Resend email + OTA integrations, Polly resilience. | architecture, api, dotnet, ef-core, auth0, stripe, ota, docker |
| [PROJECT.md](./PROJECT.md) | Compressed AI context for the backend: stack snapshot, annotated repo layout, key conventions (async, DateTime.UtcNow, scoped DbContext), where-to-find-things table, gotchas (OTA webhook 3s limit, TaxRate entity, CIN format). | ai-context, backend, conventions, gotchas, navigation |
| [FRONTEND-TECHNICAL.md](./FRONTEND-TECHNICAL.md) | React 19 + Vite 8 architecture: 22-route table, Axios API client with JWT interceptor, TanStack Query v5 hooks, Zustand v5 stores, Auth0 + demo mode, Vitest + Playwright test setup. | architecture, react, frontend, routing, api-client, tanstack-query, zustand, testing |
| [FRONTEND-PROJECT.md](./FRONTEND-PROJECT.md) | Compressed AI context for the frontend: stack snapshot, annotated repo layout, key conventions (ApiClient.unwrap(), PascalCase BookingStatus, nightlyRate field names), 10 gotchas (VITE_API_BASE_URL port 3000, demo mode, duplicate pricing file). | ai-context, frontend, conventions, gotchas, navigation |
| [runbooks/index.md](./runbooks/index.md) | Index of all runbooks (one per topic: auth0, stripe, billing-tax, storage, email, hangfire, feature-flags, alloggiati, gdpr, ical, seo, golden-journey-l3, mobile-e2e, deploy-checklist, …) with the tasks that created them. | runbooks, operations, configuration, deploy |
| [AUTH0_SETUP.md](./AUTH0_SETUP.md) | Step-by-step Auth0 tenant configuration: application setup, API audience, RBAC roles, JWT validation in ASP.NET Core, M2M tokens. | auth0, setup, jwt, rbac, configuration |
| [AI-STRATEGY.md](./AI-STRATEGY.md) | AI reality check (current pricing is rule-based, not AI), AI roadmap for the PMS core (predictive pricing, compliance assistant, guest comms), the net-new AI-powered supplier & services marketplace (matching, acquisition CRM, reputation ranking, plus marketplace-specific AI: supplier-SEO microsites, instant quotes, concierge dispatcher, document verification, fraud monitoring), AI as a go-to-market growth engine for a zero-base launch (programmatic compliance SEO, free lead-magnet tools, one-click migration, WhatsApp agent, compliance badge), and competitive differentiation vs. Lodgify, Smoobu, Guesty, Hostaway, Hospitable, Annette. | ai-strategy, vision, marketplace, suppliers, llm, roadmap, positioning, growth, go-to-market, seo, competitors |
| [integrations/ai-assistants-plan.md](./integrations/ai-assistants-plan.md) | Plan for the ChatGPT and Claude integration (Wave 8, AI-01..AI-12): remote MCP server in the .NET backend, MCP Apps widgets, Claude plugin bundle and ChatGPT/Codex plugin, Auth0 OAuth (CIMD/DCR/resource), PII minimisation and GDPR impact, directory submission checklists, open questions. Documentation research dated 2026-10-01 with [V]/[E]/[S] verification levels. | mcp, mcp-apps, chatgpt, claude, plugins, auth0, oauth, privacy, gdpr, submission, wave-8 |
