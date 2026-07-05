# C# Playwright Agentic Scraper

A standalone, containerized, REST-API-driven web scraper microservice written in C# targeting **.NET 10**. 

The microservice automates browser interaction and extracts structured data from dynamic websites using a **Dual-Loop Agentic Architecture**. It separates high-level planning (Outer Loop) from low-level execution (Inner Loop), and decouples the execution driver (Playwright vs. Desktop) from the reasoning agent (DOM-based vs. Coordinate-based).

---

## 🏗️ Architecture

```
                       +-------------------+
                       |    Client API     |
                       +---------+---------+
                                 |  (Start/Check/Stop)
                                 v
                       +---------+---------+
                       | ScraperJobService |
                       +---------+---------+
                                 |  (Spawns Background Task)
                                 v
                       +---------+---------+
                       |   ScraperRunner   |
                       +----+---------+----+
                            |         |
      +---------------------+         +---------------------+
      | (Overall Goal)                                      | (Sub-Goal Instructions)
      v                                                     v
+-----+---------------+                               +-----+---------------+
|     Outer Loop      |                               |     Inner Loop      |
|  (Reasoning Model)  |<==============================|    (Agent Actor)    |
|  gemini-3.5-flash   |     (History of Steps)        |   claude-5-sonnet   |
+---------------------+                               +----------+----------+
                                                                 |
                                                                 | (Decided Action)
                                                                 v
                                                      +----------+----------+
                                                      |  IExecutionDriver   |
                                                      |  (Playwright/Host)  |
                                                      +----------+----------+
                                                                 |
                                                                 v
                                                      +----------+----------+
                                                      |  Target Page/DOM    |
                                                      +---------------------+
```

### Abstractions
1. **`IExecutionDriver`**: Defines the interface for interacting with the environment (clicking, typing, scrolling, taking screenshots).
   * `PlaywrightBrowserDriver` (Default): Container-isolated headless Chromium browser.
   * `DesktopSystemDriver` (Future): Desktop coordinate automation.
2. **`IInnerLoopAgent`**: Defines how the agent decides actions based on observations.
   * `DomSelectorAgent` (Default): Operates on simplified XML representation of visible page elements with unique `pg-id`s. Highly reliable and token-efficient.
   * `VisualCoordinateAgent`: Operates on raw screenshots and predicts precise pixel coordinates `(x, y)` to click or interact with.

---

## 🚦 API Endpoints

### 1. Start Scraping Job
* **Endpoint:** `POST /api/scrape/start`
* **Body:**
  ```json
  {
    "url": "https://news.ycombinator.com",
    "goal": "Extract the top 5 article titles and points",
    "outerModel": "gemini-3.5-flash",
    "innerModel": "claude-5-sonnet",
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

### 2. Check Job Status
* **Endpoint:** `GET /api/scrape/status/{jobId}`
* **Response:**
  ```json
  {
    "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "url": "https://news.ycombinator.com",
    "goal": "Extract the top 5 article titles and points",
    "status": "Running",
    "currentStep": 3,
    "maxSteps": 10,
    "lastAction": "Clicking element '[data-pg-id=\'12\']'",
    "startedAt": "2026-07-04T21:00:00Z",
    "completedAt": null,
    "error": null
  }
  ```

### 3. Get Scraped Result
* **Endpoint:** `GET /api/scrape/result/{jobId}`
* **Response:**
  ```json
  {
    "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "status": "Completed",
    "data": {
      "articles": [
        { "title": "Example Link A", "points": 120 },
        { "title": "Example Link B", "points": 85 }
      ]
    },
    "error": null
  }
  ```

### 4. Get Step Logs & Screenshots
* **Endpoint:** `GET /api/scrape/logs/{jobId}`
* **Response:**
  ```json
  {
    "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "logs": [
      {
        "timestamp": "2026-07-04T21:00:05Z",
        "stepNumber": 1,
        "thought": "We need to find the links on the main page. Let's inspect the page content.",
        "action": {
          "type": "click_selector",
          "selector": "[data-pg-id='4']"
        },
        "screenshotPath": "/screenshots/3fa85f64-5717-4562-b3fc-2c963f66afa6/step_01.png"
      }
    ]
  }
  ```
* *Note:* You can render the screenshots in real-time in any UI by requesting `http://localhost:8428/screenshots/{jobId}/step_{stepNumber}.png`.

### 5. Stop/Cancel Job
* **Endpoint:** `POST /api/scrape/stop/{jobId}`
* **Response:**
  ```json
  {
    "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "status": "Stopped",
    "message": "Job cancellation request sent."
  }
  ```

---

## 🛠️ Build & Run

### Locally (with .NET 10 SDK)
1. Build the application:
   ```bash
   dotnet build
   ```
2. Install Playwright browser dependencies:
   ```bash
   dotnet tool install --global Microsoft.Playwright.CLI
   playwright install
   ```
3. Start the service:
   ```bash
   dotnet run
   ```

### Docker
1. Start the service via Docker Compose (maps port `8428`):
   ```bash
   docker compose up --build -d
   ```

---

## 🚀 CI/CD Pipeline
GitHub Actions are configured in `.github/workflows/docker-publish.yml`. When code is pushed/merged to the `main` branch, the container image is compiled and pushed to GitHub Container Registry (`ghcr.io`).

**Branching Strategy:** To reduce GitHub Actions billing time, all feature development takes place on dedicated feature branches (e.g. `feature/playwright-csharp-scraper`) and is only merged into `main` upon explicit user sign-off.
