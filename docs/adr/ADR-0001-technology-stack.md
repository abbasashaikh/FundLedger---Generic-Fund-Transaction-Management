# ADR-0001 — Technology stack and modular monolith

- **Status:** Accepted (stack mandated by PRD v1.1 §26.1)
- **Date:** 06-Oct-2026

## Context

PRD §26 specifies the stack:
- React + TypeScript + Vite PWA
- ASP.NET Core Web API with EF Core
- PostgreSQL

Load is small: 50 active users per organization. There is one deployment for V1, and SaaS growth is a future possibility.

## Decision

- **Backend:** one ASP.NET Core API on .NET 10 LTS, structured as a **modular monolith**. It has Api, Application, Domain and Infrastructure projects, with module boundaries per TRD §3.3. A separate Worker process is built from the same solution and handles exports and cleanup.
- **Frontend:** React 19 + TS + Vite, Tailwind v4 + shadcn/ui, TanStack Query, React Hook Form + Zod, Dexie for IndexedDB, and vite-plugin-pwa (Workbox).
- **Database:** PostgreSQL 16+. The rules that protect financial correctness are enforced *in the database* as constraints, triggers and RLS, not only in C#.

## Consequences

Benefits:
- One deployable, one database and simple operations, which suits a solo developer.
- Module boundaries let a module (for example Reporting) be extracted later if needed.

Costs:
- Two languages (C# and TS). The validation rules are duplicated, which is mitigated by generating the TS client and Zod types from OpenAPI.

## Alternatives considered

- **Node/NestJS or Next.js full-stack.** One language, but it contradicts the PRD. Rejected.
- **Microservices.** No benefit at this scale. Rejected.
