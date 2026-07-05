# Changelog

All notable changes to the C# Playwright Agentic Scraper will be documented in this file.

The project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
