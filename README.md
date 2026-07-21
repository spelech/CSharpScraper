# C# Playwright Agentic Scraper

A standalone, containerized, REST-API-driven web scraper microservice written in C# targeting **.NET 10**. 

The microservice automates browser interaction and extracts structured data from dynamic websites using a **Single-Loop Agentic Architecture**. It leverages Playwright for headless automation and LLM completions to decide real-time browser actions (clicking, typing, scrolling, waiting) to accomplish a user's goal.

### 🌟 Key Features (v0.2.0)
- **Embedded Web Dashboard**: Access `http://localhost:8428` in your browser to launch jobs, inspect real-time progress bars, view step-by-step reasoning logs, preview live page screenshots, and test MCP capabilities.
- **MCP Server Protocol (2026-07-28 RC Spec)**: Complete Model Context Protocol implementation supporting stateless requests (`Mcp-Method`), `server/discover`, **Tools**, **Prompts** (`prompts/list`, `prompts/get`), **Resources** (`scraper://jobs/{jobId}`, `scraper://jobs/{jobId}/logs`, `scraper://jobs/{jobId}/screenshots/{stepNumber}`), **Tasks Extension** (`tasks/get`, `tasks/cancel`), and **Argument Completions** (`completion/complete`).

---

## 🏗️ Architecture

```mermaid
graph TD
    ClientAPI[Client API] -->|Start/Compare/Stop| ScraperJobService[ScraperJobService]
    ScraperJobService -->|Spawns Background Task| ScraperRunner[ScraperRunner]
    ScraperRunner -->|Single execution loop| AgentActor[Agent Actor <br> DomSelectorAgent / VisualCoordinateAgent]
    AgentActor -->|Decides browser action| IExecutionDriver[IExecutionDriver <br> Playwright / Host]
    IExecutionDriver -->|Interacts with| TargetPage[Target Page / DOM]
    IExecutionDriver -->|Returns page state & screenshots| AgentActor
```

### Key Components
1. **`IExecutionDriver`**: Defines the interface for interacting with the environment (clicking, typing, scrolling, taking screenshots).
   * `PlaywrightBrowserDriver` (Default): Container-isolated headless Chromium browser connected over CDP.
2. **`IInnerLoopAgent`**: Represents the decision-making brain of the crawler.
   * `DomSelectorAgent` (Default): Evaluates a simplified XML representation of visible page elements with unique `pg-id`s. Highly reliable and token-efficient.
   * `VisualCoordinateAgent`: Operates on raw screenshots and predicts precise pixel coordinates `(x, y)` to click or interact with.
3. **`SearxngClient`**: Direct integration with the local SearXNG service to perform dynamic product/store URL discovery.

---

## 🚦 API Endpoints

### 1. Start Single Scrape Job
* **Endpoint:** `POST /api/scrape/start`
* **Body:**
  ```json
  {
    "url": "https://news.ycombinator.com",
    "goal": "Extract the top 5 article titles and points",
    "model": "gemini-2.5-flash",
    "maxSteps": 10,
    "driverType": "playwright",
    "agentType": "dom"
  }
  ```
* **Response (202 Accepted):**
  ```json
  {
    "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "status": "Running",
    "message": "Scraping job enqueued."
  }
  ```

### 2. Check Job Status & Result
* **Status Endpoint:** `GET /api/scrape/status/{jobId}`
* **Result Endpoint:** `GET /api/scrape/result/{jobId}`
* **Logs & Screenshots:** `GET /api/scrape/logs/{jobId}`
  * *Note:* You can render the screenshots in real-time in any UI by requesting `http://localhost:8428/screenshots/{jobId}/step_{stepNumber}.png`.

### 3. Parallel Comparison (Explicit URLs)
Spawns concurrent scraper runs for a set of known URLs, running them simultaneously in isolated browser contexts.
* **Endpoint:** `POST /api/scrape/compare[?sync=true]`
* **Body:**
  ```json
  {
    "urls": [
      "https://www.target.com/p/huy-fong-sriracha-hot-chili-sauce-17oz/-/A-13473417",
      "https://www.meijer.com/shopping/product/huy-fong-sriracha-chili-sauce-17-oz/3989610189.html"
    ],
    "goal": "Find and extract the product name, price, and store availability.",
    "model": "gemini-2.5-flash",
    "maxSteps": 5
  }
  ```
* **Query Params:** `sync=true` blocks the HTTP request and returns the compiled price comparison grid once all jobs finish. Leaving it out returns immediately with a `compareId`.

### 4. Dynamic Auto-Discovery & Compare
The orchestrator uses the LLM to analyze your product query and location, determines the best search query and retailer domains (e.g. grocery domains for foods, electronics domains for headphones), searches SearXNG, spawns parallel crawls, and aggregates results.
* **Endpoint:** `POST /api/scrape/discover-compare[?sync=true]`
* **Body:**
  ```json
  {
    "query": "Huy Fong Sriracha 17oz",
    "location": "Schaumburg, IL",
    "model": "gemini-2.5-flash",
    "maxSteps": 5
  }
  ```

---

## ⚙️ Configuration

The microservice is configured via environment variables. You can copy the template `.env.example` to `.env` to customize settings.

| Variable | Description | Default |
|----------|-------------|---------|
| `TZ` | Timezone setting for the microservice. | `America/Chicago` |
| `DEFAULT_LLM_BASE_URL` | Base URL for OpenAI-compatible completions API. | `http://litellm:4000/v1` |
| `DEFAULT_LLM_API_KEY` | Bearer API Token for the completions API. | `sk-placeholder` |
| `DEFAULT_LLM_MODEL` | Default model used to guide agent decisions. | `gemini-3.5-flash` |
| `LLM_REFERER` | Optional `HTTP-Referer` header for OpenRouter analytics. | `https://github.com/spelech/playwright-csharp-scraper` |
| `BROWSER_WS_ENDPOINT` | WebSocket connection string (CDP) for remote browsers. If omitted, uses a container-isolated Chromium instance. | *(Empty - runs locally)* |
| `SEARXNG_BASE_URL` | SearXNG instance endpoint for web query discovery. | `http://searxng:8080` |

---

## 🛠️ Build & Run

### Docker (Recommended)
Docker is the easiest way to run the scraper as all Playwright browser dependencies and execution runtimes are pre-packaged.

1. **Copy the environment template:**
   ```bash
   cp .env.example .env
   ```
2. **Configure your API keys** inside `.env`.
3. **Start the service:**
   ```bash
   docker compose up -d
   ```
4. Access the service at `http://localhost:8428`.

### Locally (with .NET 10 SDK)
To run outside of docker:

1. **Install .NET 10 SDK** on your machine.
2. **Build the project:**
   ```bash
   dotnet build
   ```
3. **Install Playwright dependencies:**
   ```bash
   dotnet tool install --global Microsoft.Playwright.CLI
   playwright install
   ```
4. **Define your environment variables** or add them to your `appsettings.json`, then run:
   ```bash
   dotnet run
   ```
