# Changelog

All notable changes to the C# Playwright Agentic Scraper will be documented in this file.

The project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.2.0] - 2026-07-20

### Added
- **MCP 2026-07-28 RC Spec Support**: Upgraded MCP protocol implementation to support `2026-07-28` RC spec, including stateless headers (`Mcp-Method`, `Mcp-Name`), `server/discover` capability endpoint, and `ttlMs`/`cacheScope` caching headers.
- **MCP Prompts (`prompts/list`, `prompts/get`)**: Exposed reusable prompt templates for e-commerce scraping, article summarization, and multi-retailer comparative pricing.
- **MCP Resources & Resource Templates (`resources/list`, `resources/templates/list`, `resources/read`)**: Exposed active scrape jobs (`scraper://jobs/{jobId}`), step execution logs (`scraper://jobs/{jobId}/logs`), and live step screenshots (`scraper://jobs/{jobId}/screenshots/{stepNumber}`) as queryable MCP resources.
- **MCP Tasks Extension (`tasks/get`, `tasks/cancel`)**: Integrated task handle management for long-running background scraping jobs.
- **MCP Argument Completion (`completion/complete`)**: Added argument autocomplete support for prompt templates and resource parameters.
- **Embedded Web Dashboard**: Built single-page web dashboard served directly from `wwwroot` for launching single/compare/discovery scrapes, inspecting real-time job execution logs, viewing live step screenshots, and exploring MCP capabilities interactively.

---

## [0.1.0] - 2026-07-04

### Added
- **Project Scaffold**: Brand new ASP.NET Core project targetting .NET 10.0.
- **Dual-Loop Orchestration**: Implemented `ScraperRunner` coordinating high-level plan verification (Outer Loop) with interactive browser page operations (Inner Loop).
- **Extensible Drivers & Agents**: Created `IExecutionDriver` and `IInnerLoopAgent` abstractions allowing multiple execution providers (Playwright) and logic agents (DOM structure selectors and screen coordinate visual estimators).
- **Stealth Chromium Browser**: Implemented custom sandboxing and automation-bypass options inside `PlaywrightBrowserDriver`.
- **Background Task Management**: Integrated `ScraperJobService` supporting queueing, non-blocking Hosted Services threads, and task cancellation propagation.
- **MSTest Testing Suite**: Added `CSharpScraper.Tests` project with 12 unit tests verifying DOM-to-XML serialization, LLM header generation, background job cancellations, and orchestrator transitions.
- **CI/CD Hardening**: Integrated `dotnet test` into the GitHub Actions workflow (.github/workflows/docker-publish.yml) to prevent container deployment on test failures.
- **Semantic Versioning**: Configured `Version` properties in `.csproj` and mapped `appVersion` into the `/health` API check dynamically.
