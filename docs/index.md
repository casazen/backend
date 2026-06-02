# CasaZen — Project Documentation

> Full-stack vacation rental management platform for the Italian market. Backend: ASP.NET Core 10 REST API. Frontend: React 19 SPA.

---

## Backend (ASP.NET Core 10)

| Document | Purpose | Audience |
|---|---|---|
| [BUSINESS.md](BUSINESS.md) | Domain entities, business processes, rules, glossary | Product Owner, Business Analyst, stakeholders |
| [TECHNICAL.md](TECHNICAL.md) | Architecture, API reference, data model, design patterns, infrastructure | Backend developers |
| [PROJECT.md](PROJECT.md) | Compressed AI context — stack, layout, conventions, gotchas | AI agents, onboarding developers |

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
| [TECHNICAL.md](./TECHNICAL.md) | Layered .NET 10 architecture (Web → Core → Infrastructure), full API endpoint catalog, EF Core data model, design patterns (Repository, Adapter, Mediator), Auth0 + Stripe + SendGrid + OTA integrations, Polly resilience. | architecture, api, dotnet, ef-core, auth0, stripe, ota, docker |
| [PROJECT.md](./PROJECT.md) | Compressed AI context for the backend: stack snapshot, annotated repo layout, key conventions (async, DateTime.UtcNow, scoped DbContext), where-to-find-things table, gotchas (OTA webhook 3s limit, TaxRate entity, CIN format). | ai-context, backend, conventions, gotchas, navigation |
| [FRONTEND-TECHNICAL.md](./FRONTEND-TECHNICAL.md) | React 19 + Vite 8 architecture: 22-route table, Axios API client with JWT interceptor, TanStack Query v5 hooks, Zustand v5 stores, Auth0 + demo mode, Vitest + Playwright test setup. | architecture, react, frontend, routing, api-client, tanstack-query, zustand, testing |
| [FRONTEND-PROJECT.md](./FRONTEND-PROJECT.md) | Compressed AI context for the frontend: stack snapshot, annotated repo layout, key conventions (ApiClient.unwrap(), PascalCase BookingStatus, nightlyRate field names), 10 gotchas (VITE_API_BASE_URL port 3000, demo mode, duplicate pricing file). | ai-context, frontend, conventions, gotchas, navigation |
| [AUTH0_SETUP.md](./AUTH0_SETUP.md) | Step-by-step Auth0 tenant configuration: application setup, API audience, RBAC roles, JWT validation in ASP.NET Core, M2M tokens. | auth0, setup, jwt, rbac, configuration |
