# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

NTG Agent is a multi-service chatbot platform built on **.NET 10 + .NET Aspire + Blazor + Microsoft Agent Framework + Kernel Memory**. SQL Server is the primary datastore; Elasticsearch backs Kernel Memory for RAG. Multiple LLM providers are supported (GitHub Models, OpenAI, Azure OpenAI, Google Gemini, Anthropic).

## Running locally

**Everything is orchestrated by the Aspire AppHost** — no local SQL Server or Elasticsearch required; Aspire spins them up as containers and runs EF migrations before starting services. Prerequisites: .NET 10 SDK, Docker, and `dotnet-ef` global tool.

```bash
# One-time: seed user-secrets (GitHub token, SA password, KM key, Google CSE, etc.)
./scripts/init-apphost-user-secrets.sh

# Run everything (5 services + SQL Server + Elasticsearch + 2 migration jobs)
dotnet run --project NTG.Agent.AppHost
```

Resources Aspire provisions (see `NTG.Agent.AppHost/Program.cs`):
- `sqlserver` (with `ntg-agent-local-dev-sqlserver-data` volume) + `NTGAgent` DB
- `elasticsearch` 8.15.0 (with persistent data volume)
- `db-migrate-admin`, `db-migrate-orchestrator` — one-shot EF migration jobs (Admin runs first, Orchestrator waits on it)
- `ntg-agent-mcp-server`, `ntg-agent-knowledge`, `ntg-agent-orchestrator`, `ntg-agent-webclient`, `ntg-agent-admin`

Default seeded admin: `admin@ntgagent.com` / `Ntg@123`. Default agent ID: `31cf1546-e9c9-4d95-a8e5-3c7c7570fec5` (`DefaultAgentId` in `AgentFactory.cs`, seeded in `AgentDbContext.OnModelCreating`).

## Common commands

```bash
# Build the whole solution
dotnet build NTG.Agent.sln

# Run all tests (with coverage, matching CI)
dotnet test --configuration Release

# Run a single test project
dotnet test tests/NTG.Agent.Orchestrator.Tests/NTG.Agent.Orchestrator.Tests.csproj

# Run a single test by name
dotnet test --filter "FullyQualifiedName~UserQuotaServiceTests"
dotnet test --filter "FullyQualifiedName~UserQuotaServiceTests.MethodName"

# EF migrations (run from repo root; startup-project must equal project)
dotnet ef migrations add <Name> --project NTG.Agent.Orchestrator
dotnet ef database update --project NTG.Agent.Orchestrator

# Admin project migrations use a different csproj:
dotnet ef migrations add <Name> \
  --project NTG.Agent.Admin/NTG.Agent.Admin/NTG.Agent.Admin.csproj \
  --startup-project NTG.Agent.Admin/NTG.Agent.Admin/NTG.Agent.Admin.csproj
```

CI (`.github/workflows/ntg-agent-ci.yml`) runs `dotnet restore → build Release → test with OpenCover coverage → SonarCloud`. `Directory.Build.props` enables `EnforceCodeStyleInBuild=true` with `AnalysisMode=Recommended`, so analyzer warnings break the build.

## Architecture

### Service layout (driven by `NTG.Agent.sln` and `AppHost/Program.cs`)

| Project | Role |
| --- | --- |
| `NTG.Agent.AppHost` | Aspire host. Declares parameters, containers, migration jobs, and service references. Source of truth for wiring. |
| `NTG.Agent.Orchestrator` | REST API — chat, agents, conversations, documents, folders, tags, token usage, shared conversations, preferences, features. Owns `AgentDbContext` and EF migrations. |
| `NTG.Agent.Knowledge` | Kernel Memory web service (RAG pipeline). Backed by SQL Server + Elasticsearch. Exposes `/upload`, `/ask`, etc. |
| `NTG.Agent.MCP.Server` | MCP (Model Context Protocol) server exposing tools (e.g. Google CSE search) to agents via `WithMcpTools`. |
| `NTG.Agent.WebClient` (`+ .Client`) | End-user Blazor chat UI. Server + WebAssembly projects. |
| `NTG.Agent.Admin` (`+ .Client`) | Admin Blazor app. Also hosts **YARP reverse proxy** acting as BFF — forwards `/api/*` to Orchestrator so WASM clients can ride the shared cookie. |
| `NTG.Agent.Common` | Shared DTOs and helpers referenced by all services. DTOs are organized by feature (`Dtos/Chats`, `Dtos/Documents`, `Dtos/Agents`, …). |
| `NTG.Agent.ServiceDefaults` | Common Aspire service defaults + `IApplicationLogger`, `IMetricsCollector`, `GlobalExceptionHandler`. Every service calls `builder.AddServiceDefaults()`. |
| `AITools/NTG.Agent.AITools.*` | Reusable tool libraries (SimpleTools: DateTime; SearchOnlineTool: Google CSE + web scraping). |
| `tests/NTG.Agent.Orchestrator.Tests`, `tests/NTG.Agent.MCP.Server.Tests` | xUnit test projects. |

### Inter-service communication

- Service references in `AppHost/Program.cs` inject `services__<name>__<scheme>__<idx>` env vars. Example: Orchestrator resolves the Knowledge endpoint via `services__ntg-agent-knowledge__https__0` (see `Orchestrator/Program.cs` where it constructs `MemoryWebClient`).
- Kernel Memory auth uses a shared secret passed to both services as `KernelMemory__ApiKey` (Orchestrator side) and `KernelMemory__ServiceAuthorization__AccessKey1/2` (Knowledge side). They must match — user-secrets script handles this.
- Admin’s reverse proxy config in `appsettings.json` resolves destinations via Aspire service discovery (`AddServiceDiscoveryDestinationResolver`).

### Authentication

Shared-cookie model:
- ASP.NET Identity lives in **`NTG.Agent.Admin`** (owns `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles` tables — Orchestrator’s EF model marks these `ExcludeFromMigrations` so only Admin owns their schema).
- Data Protection keys are persisted to a **shared file-system directory** so cookies encrypted by Admin can be read by Orchestrator: Admin writes to `../../key/`, Orchestrator reads from `../key/`, both with `SetApplicationName("NTGAgent")`.
- Orchestrator authenticates with `AddAuthentication("Identity.Application").AddCookie(...)` using cookie name `.AspNetCore.Identity.Application`.
- Blazor Server components don’t forward cookies automatically — the BFF/YARP approach only works for WASM. Keep this in mind when adding server-rendered pages that call the Orchestrator API.
- Anonymous usage is supported via `AnonymousSession` (session ID + IP rate limiting); see `AgentService.ChatStreamingAsync` and `AnonymousSessionService`.

### Agent execution pipeline (Orchestrator)

`AgentService` is the central chat orchestrator. It:
1. Validates session/user (anonymous rate-limit check via `IAnonymousSessionService`).
2. Loads the agent config from DB via `AgentFactory.CreateAgent(agentId)`. The factory switches on `ProviderName` (`GitHubModel`, `GoogleGemini`, `OpenAI`, `AzureOpenAI`, `Anthropic`) and wires the correct SDK client (OpenAI, Azure OpenAI, Anthropic). **When adding a new provider, extend both `CreateAgent` and `CreateBasicAgent` switches.**
3. Pulls knowledge via `IKnowledgeService` (Kernel Memory), user long-term memory via `IUserMemoryService` (gated by `LongTermMemory:Enabled` config), and attached-document analysis via `IDocumentAnalysisService` (Azure Document Intelligence, gated by `Azure:DocumentIntelligence:IsEnabled`).
4. Streams the response, enforces token quotas (`IUserQuotaService`, `QuotaSettings`), and records usage via `ITokenTrackingService`.
5. `MAX_LATEST_MESSAGE_TO_KEEP_FULL = 5` — older messages are summarized to control token budget.

`CreateBasicAgent` uses the **default agent’s** provider/model for cheap utility calls (conversation naming, summarization).

### Data model (`AgentDbContext`)

Key aggregates: `Agent` / `AgentTools`, `Conversation` / `PChatMessage`, `SharedConversation` / `SharedChatMessage`, `Document` / `Folder`, `Tag` / `TagRole` / `DocumentTag`, `User` / `Role` / `UserRole` (Identity tables — excluded from migrations here), `UserPreference`, `TokenUsage`, `AnonymousSession`. Noteworthy:
- `Guid` IDs on Identity tables are stored as `nvarchar(450)` via a `ValueConverter<Guid, string>` so they match ASP.NET Identity’s string keys.
- `ChatRole` is persisted through `ChatRoleValueConverter` to `nvarchar(50)`.
- `UserPreference` and `TokenUsage` enforce a `CK_..._UserIdOrSessionId` check constraint: exactly one of `UserId` or `SessionId` must be set (authenticated vs anonymous).
- Default `Agent`, default `Folder`s, and default `Tag`/`TagRole` rows are seeded in `OnModelCreating` — do not change these GUIDs; they are referenced from code (e.g. `DefaultAgentId` in `AgentFactory`).

### Observability

All services call `AddServiceDefaults()` (`NTG.Agent.ServiceDefaults/Extensions.cs`) which wires OpenTelemetry tracing/metrics/logging, OTLP export, HTTP & ASP.NET Core instrumentation, health checks (`/health`, `/alive` in Development), and the `GlobalExceptionHandler`. The Orchestrator adds custom sources/meters: `NTG.Agent.Orchestrator`, `*Microsoft.Extensions.AI*`, `*Microsoft.Agents.AI*`, plus counters/histograms `agent_interactions_total` and `agent_response_time_seconds`. See `docs/monitoring-setup.md` for details.

## Project conventions

Follow `.github/copilot-instructions.md` — it’s the authoritative style guide. Highlights:
- **C# 12**, file-scoped namespaces, nullable reference types enabled, async/await throughout.
- DTOs live in `NTG.Agent.Common/Dtos/<Feature>/`; never expose EF entities over the API.
- Orchestrator models live under `Models/<Domain>/`; services under `Services/<Domain>/` with `IFooService` + `FooService` pairs registered in `Program.cs`.
- Controllers: `[ApiController]` + `[Route("api/[controller]")]`, constructor DI, async.
- Blazor components: pick a render mode explicitly (`@rendermode InteractiveServer` or `InteractiveWebAssembly`), handle `isLoading` / `errorMessage`, log via injected `ILogger<T>`.
- `NTG.Agent.AITools.SimpleTools` and `SearchOnlineTool` are the pattern for new reusable agent tools — register via the `AiToolRegistrationExtensions` style extension method.

When adding a new entity or changing schema, generate an EF migration in the Orchestrator project (`Migrations/` contains ~15 historical migrations). Remember Identity tables are owned by Admin — mark them `ExcludeFromMigrations` in Orchestrator if you touch them.

## Things that commonly trip people up

- **Don’t run services individually in dev** — they rely on Aspire for service discovery env vars and shared secrets. Always go through `NTG.Agent.AppHost`.
- **Data Protection keys**: if you delete the `key/` folder, all existing cookies become unreadable and users are signed out.
- **Kernel Memory API key must be ≥ 32 chars** (KM requirement); the init script generates one if missing.
- **`ProviderEndpoint` is optional for standard OpenAI, but required for GitHub Models and Google Gemini.** See `AgentFactory.CreateBasicOpenAIAgent`.
- **Google CSE parameters** are required by the MCP server at startup even if unused — the `.env.example` ships placeholders for this reason.
- **Changing seeded GUIDs** (default agent, default folder, anonymous role, public tag) will break both existing databases and hard-coded references in `AgentFactory`, `AgentDbContext`, and `Constants`.
