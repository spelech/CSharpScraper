document.addEventListener('DOMContentLoaded', () => {
    // Tab Navigation
    const navItems = document.querySelectorAll('.nav-item');
    const tabContents = document.querySelectorAll('.tab-content');
    const pageTitle = document.getElementById('page-title');
    const newScrapeHeaderBtn = document.getElementById('new-scrape-header-btn');
    const refreshBtn = document.getElementById('refresh-btn');

    let currentModalJobId = null;
    let pollInterval = null;

    navItems.forEach(item => {
        item.addEventListener('click', () => {
            const targetTab = item.getAttribute('data-tab');
            
            navItems.forEach(n => n.classList.remove('active'));
            tabContents.forEach(c => c.classList.remove('active'));

            item.classList.add('active');
            document.getElementById(`tab-${targetTab}`).classList.add('active');

            if (targetTab === 'dashboard') {
                pageTitle.textContent = 'Jobs Dashboard';
            } else if (targetTab === 'launch') {
                pageTitle.textContent = 'Start New Scrape';
            } else if (targetTab === 'mcp') {
                pageTitle.textContent = 'MCP Inspector & Sandbox';
                loadMcpData();
            }
        });
    });

    newScrapeHeaderBtn.addEventListener('click', () => {
        document.querySelector('[data-tab="launch"]').click();
    });

    refreshBtn.addEventListener('click', () => {
        fetchJobs();
    });

    // Form Mode Switcher
    const modeBtns = document.querySelectorAll('.mode-btn');
    const forms = {
        single: document.getElementById('scrape-form-single'),
        compare: document.getElementById('scrape-form-compare'),
        discovery: document.getElementById('scrape-form-discovery')
    };

    modeBtns.forEach(btn => {
        btn.addEventListener('click', () => {
            const mode = btn.getAttribute('data-mode');
            modeBtns.forEach(b => b.classList.remove('active'));
            btn.classList.add('active');

            Object.keys(forms).forEach(k => {
                if (k === mode) forms[k].classList.remove('hidden');
                else forms[k].classList.add('hidden');
            });
        });
    });

    // Submit Single Form
    forms.single.addEventListener('submit', async (e) => {
        e.preventDefault();
        const payload = {
            url: document.getElementById('url').value,
            goal: document.getElementById('goal').value,
            model: document.getElementById('model').value,
            maxSteps: parseInt(document.getElementById('maxSteps').value, 10),
            agentType: document.getElementById('agentType').value
        };

        try {
            const res = await fetch('/api/scrape/start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const data = await res.json();
            if (res.ok) {
                alert(`Scrape Job Enqueued! ID: ${data.jobId}`);
                document.querySelector('[data-tab="dashboard"]').click();
                fetchJobs();
                openJobModal(data.jobId);
            } else {
                alert(`Error: ${data.error || 'Failed to start job'}`);
            }
        } catch (err) {
            alert(`Network error: ${err.message}`);
        }
    });

    // Submit Compare Form
    forms.compare.addEventListener('submit', async (e) => {
        e.preventDefault();
        const rawUrls = document.getElementById('compare-urls').value;
        const urls = rawUrls.split('\n').map(u => u.trim()).filter(u => u.length > 0);
        const payload = {
            urls: urls,
            goal: document.getElementById('compare-goal').value,
            model: document.getElementById('compare-model').value,
            maxSteps: parseInt(document.getElementById('compare-maxSteps').value, 10)
        };

        try {
            const res = await fetch('/api/scrape/compare', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const data = await res.json();
            if (res.ok) {
                alert(`Compare Request Started! Group ID: ${data.compareId}`);
                document.querySelector('[data-tab="dashboard"]').click();
                fetchJobs();
            } else {
                alert(`Error: ${data.error || 'Failed to start comparison'}`);
            }
        } catch (err) {
            alert(`Network error: ${err.message}`);
        }
    });

    // Submit Discovery Form
    forms.discovery.addEventListener('submit', async (e) => {
        e.preventDefault();
        const payload = {
            query: document.getElementById('disc-query').value,
            location: document.getElementById('disc-location').value
        };

        try {
            const res = await fetch('/api/scrape/discover-compare', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const data = await res.json();
            if (res.ok) {
                alert(`Discovery Request Started! Group ID: ${data.compareId}`);
                document.querySelector('[data-tab="dashboard"]').click();
                fetchJobs();
            } else {
                alert(`Error: ${data.error || 'Failed to start discovery'}`);
            }
        } catch (err) {
            alert(`Network error: ${err.message}`);
        }
    });

    // Fetch Jobs List & Stats
    async function fetchJobs() {
        try {
            const res = await fetch('/health');
            if (!res.ok) return;

            // Fetch recent resources via MCP resources/list or standard endpoints
            const mcpRes = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'resources/list' })
            });

            if (mcpRes.ok) {
                const mcpData = await mcpRes.json();
                const resources = mcpData.result?.resources || [];
                renderJobsTableFromResources(resources);
            }
        } catch (err) {
            console.error("Failed fetching jobs:", err);
        }
    }

    async function renderJobsTableFromResources(resources) {
        const tbody = document.getElementById('jobs-tbody');
        if (resources.length === 0) {
            tbody.innerHTML = '<tr><td colspan="7" class="empty-row">No active or recent scraping jobs found. Start a new job above!</td></tr>';
            return;
        }

        let html = '';
        let total = resources.length;
        let running = 0;
        let completed = 0;
        let failed = 0;

        for (const r of resources) {
            // Extract jobId from scraper://jobs/{jobId}
            const jobId = r.uri.replace('scraper://jobs/', '');
            
            // Fetch detailed status for each
            try {
                const statusRes = await fetch(`/api/scrape/status/${jobId}`);
                if (!statusRes.ok) continue;
                const job = await statusRes.json();

                const statusClass = `badge-${job.status.toLowerCase()}`;
                if (job.status === 'Running' || job.status === 'Queued') running++;
                else if (job.status === 'Completed') completed++;
                else failed++;

                html += `
                    <tr>
                        <td><code>${job.jobId.substring(0, 8)}...</code></td>
                        <td title="${job.url}">${truncate(job.url, 30)}</td>
                        <td title="${job.goal}">${truncate(job.goal, 30)}</td>
                        <td><span class="badge ${statusClass}">${job.status}</span></td>
                        <td>${job.currentStep} / ${job.maxSteps}</td>
                        <td>P: ${job.totalPromptTokens} | C: ${job.totalCompletionTokens}</td>
                        <td>
                            <button class="btn btn-secondary btn-sm view-btn" data-id="${job.jobId}">Inspect</button>
                        </td>
                    </tr>
                `;
            } catch (e) {
                console.error(e);
            }
        }

        tbody.innerHTML = html;

        document.getElementById('stat-total').textContent = total;
        document.getElementById('stat-running').textContent = running;
        document.getElementById('stat-completed').textContent = completed;
        document.getElementById('stat-failed').textContent = failed;

        document.querySelectorAll('.view-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const id = btn.getAttribute('data-id');
                openJobModal(id);
            });
        });
    }

    // Modal Control & Live Polling
    const modal = document.getElementById('job-modal');
    const modalCloseBtn = document.getElementById('modal-close-btn');
    const modalStopBtn = document.getElementById('modal-stop-btn');

    modalCloseBtn.addEventListener('click', () => {
        modal.classList.add('hidden');
        if (pollInterval) clearInterval(pollInterval);
        currentModalJobId = null;
    });

    modalStopBtn.addEventListener('click', async () => {
        if (!currentModalJobId) return;
        try {
            const res = await fetch(`/api/scrape/stop/${currentModalJobId}`, { method: 'POST' });
            if (res.ok) alert('Stop request sent.');
        } catch (e) {
            alert(`Error: ${e.message}`);
        }
    });

    async function openJobModal(jobId) {
        currentModalJobId = jobId;
        modal.classList.remove('hidden');
        document.getElementById('modal-job-sub').textContent = `ID: ${jobId}`;

        updateModalContent();
        if (pollInterval) clearInterval(pollInterval);
        pollInterval = setInterval(updateModalContent, 1500);
    }

    async function updateModalContent() {
        if (!currentModalJobId) return;

        try {
            // Status
            const statusRes = await fetch(`/api/scrape/status/${currentModalJobId}`);
            if (!statusRes.ok) return;
            const job = await statusRes.json();

            const badge = document.getElementById('modal-status-badge');
            badge.textContent = job.status;
            badge.className = `badge badge-${job.status.toLowerCase()}`;

            document.getElementById('modal-progress-text').textContent = `Step ${job.currentStep} / ${job.maxSteps}`;
            document.getElementById('modal-tokens-text').textContent = `P: ${job.totalPromptTokens} / C: ${job.totalCompletionTokens}`;

            // Logs
            const logsRes = await fetch(`/api/scrape/logs/${currentModalJobId}`);
            if (logsRes.ok) {
                const logsData = await logsRes.json();
                renderModalLogs(logsData.logs || []);
            }

            // Results
            const resultRes = await fetch(`/api/scrape/result/${currentModalJobId}`);
            if (resultRes.ok) {
                const resultData = await resultRes.json();
                if (resultData.data) {
                    document.getElementById('modal-result-json').textContent = JSON.stringify(resultData.data, null, 2);
                } else if (resultData.error) {
                    document.getElementById('modal-result-json').textContent = `Error: ${resultData.error}`;
                } else {
                    document.getElementById('modal-result-json').textContent = 'In progress...';
                }
            }

            if (job.status !== 'Running' && job.status !== 'Queued') {
                if (pollInterval) clearInterval(pollInterval);
            }
        } catch (e) {
            console.error("Modal update error:", e);
        }
    }

    function renderModalLogs(logs) {
        const container = document.getElementById('modal-logs-timeline');
        if (logs.length === 0) {
            container.innerHTML = '<div class="log-entry">Waiting for step execution...</div>';
            return;
        }

        let html = '';
        let lastScreenshot = null;

        logs.forEach(l => {
            if (l.screenshotPath) lastScreenshot = l.screenshotPath;

            html += `
                <div class="log-entry">
                    <span class="log-step">Step ${l.stepNumber}</span> [${new Date(l.timestamp).toLocaleTimeString()}]
                    ${l.thought ? `<div class="log-thought">💭 ${escapeHtml(l.thought)}</div>` : ''}
                    ${l.action ? `<div class="log-action">⚡ ${escapeHtml(l.action)}</div>` : ''}
                </div>
            `;
        });

        container.innerHTML = html;
        container.scrollTop = container.scrollHeight;

        const img = document.getElementById('modal-screenshot-img');
        const placeholder = document.getElementById('modal-screenshot-placeholder');

        if (lastScreenshot) {
            img.src = lastScreenshot;
            img.classList.remove('hidden');
            placeholder.classList.add('hidden');
        }
    }

    // Load MCP Sandbox Data
    async function loadMcpData() {
        try {
            // Tools
            const toolsRes = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list' })
            });
            if (toolsRes.ok) {
                const data = await toolsRes.json();
                renderMcpItems('mcp-tools-list', data.result?.tools || [], t => `
                    <div class="mcp-item-title">${t.name}</div>
                    <div class="mcp-item-desc">${t.description}</div>
                `);
            }

            // Prompts
            const promptsRes = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'prompts/list' })
            });
            if (promptsRes.ok) {
                const data = await promptsRes.json();
                renderMcpItems('mcp-prompts-list', data.result?.prompts || [], p => `
                    <div class="mcp-item-title">${p.name}</div>
                    <div class="mcp-item-desc">${p.description}</div>
                `);
            }

            // Resource Templates
            const resRes = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 3, method: 'resources/templates/list' })
            });
            if (resRes.ok) {
                const data = await resRes.json();
                renderMcpItems('mcp-resources-list', data.result?.resourceTemplates || [], r => `
                    <div class="mcp-item-title"><code>${r.uriTemplate}</code></div>
                    <div class="mcp-item-desc">${r.description}</div>
                `);
            }
        } catch (e) {
            console.error("Failed loading MCP sandbox:", e);
        }
    }

    function renderMcpItems(elementId, items, formatter) {
        const el = document.getElementById(elementId);
        if (items.length === 0) {
            el.innerHTML = '<li class="mcp-item">No items declared.</li>';
            return;
        }
        el.innerHTML = items.map(item => `<li class="mcp-item">${formatter(item)}</li>`).join('');
    }

    // Utilities
    function truncate(str, n) {
        return (str && str.length > n) ? str.substr(0, n-1) + '&hellip;' : str;
    }

    function escapeHtml(str) {
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    // Initial Load & Auto Poll
    fetchJobs();
    setInterval(fetchJobs, 3000);
});
