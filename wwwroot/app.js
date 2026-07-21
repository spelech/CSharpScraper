document.addEventListener('DOMContentLoaded', () => {
    // Navigation Handling
    const navItems = document.querySelectorAll('.nav-item');
    const tabContents = document.querySelectorAll('.tab-content');
    const pageTitle = document.getElementById('page-title');
    const newScrapeHeaderBtn = document.getElementById('new-scrape-header-btn');
    const refreshBtn = document.getElementById('refresh-btn');

    let currentModalJobId = null;
    let pollInterval = null;
    let availablePrompts = [];

    const pageTitles = {
        'dashboard': 'Jobs Dashboard',
        'launch': 'Start New Scrape',
        'mcp-tools': 'MCP Tools (`tools/list`)',
        'mcp-prompts': 'MCP Prompts (`prompts/list` & `prompts/get`)',
        'mcp-resources': 'MCP Resources (`resources/read`)'
    };

    navItems.forEach(item => {
        item.addEventListener('click', () => {
            const targetTab = item.getAttribute('data-tab');
            
            navItems.forEach(n => n.classList.remove('active'));
            tabContents.forEach(c => c.classList.remove('active'));

            item.classList.add('active');
            const targetContent = document.getElementById(`tab-${targetTab}`);
            if (targetContent) targetContent.classList.add('active');

            if (pageTitles[targetTab]) pageTitle.textContent = pageTitles[targetTab];

            if (targetTab === 'mcp-tools') loadMcpTools();
            else if (targetTab === 'mcp-prompts') loadMcpPrompts();
            else if (targetTab === 'mcp-resources') loadMcpResources();
        });
    });

    newScrapeHeaderBtn.addEventListener('click', () => {
        document.querySelector('[data-tab="launch"]').click();
    });

    refreshBtn.addEventListener('click', () => {
        const activeTab = document.querySelector('.nav-item.active')?.getAttribute('data-tab');
        if (activeTab === 'dashboard') fetchJobs();
        else if (activeTab === 'mcp-tools') loadMcpTools();
        else if (activeTab === 'mcp-prompts') loadMcpPrompts();
        else if (activeTab === 'mcp-resources') loadMcpResources();
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
            const jobId = r.uri.replace('scraper://jobs/', '');
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
            const statusRes = await fetch(`/api/scrape/status/${currentModalJobId}`);
            if (!statusRes.ok) return;
            const job = await statusRes.json();

            const badge = document.getElementById('modal-status-badge');
            badge.textContent = job.status;
            badge.className = `badge badge-${job.status.toLowerCase()}`;

            document.getElementById('modal-progress-text').textContent = `Step ${job.currentStep} / ${job.maxSteps}`;
            document.getElementById('modal-tokens-text').textContent = `P: ${job.totalPromptTokens} / C: ${job.totalCompletionTokens}`;

            const logsRes = await fetch(`/api/scrape/logs/${currentModalJobId}`);
            if (logsRes.ok) {
                const logsData = await logsRes.json();
                renderModalLogs(logsData.logs || []);
            }

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

    // --- TAB: MCP Tools ---
    async function loadMcpTools() {
        const container = document.getElementById('mcp-tools-container');
        try {
            const res = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list' })
            });
            if (res.ok) {
                const data = await res.json();
                const tools = data.result?.tools || [];
                if (tools.length === 0) {
                    container.innerHTML = '<div class="placeholder-text">No MCP tools registered.</div>';
                    return;
                }
                container.innerHTML = tools.map(t => `
                    <div class="mcp-card">
                        <h4>⚡ ${t.name}</h4>
                        <p class="tab-description">${t.description}</p>
                        <div class="form-group">
                            <label>Input Schema Properties:</label>
                            <pre class="json-code">${JSON.stringify(t.inputSchema.properties, null, 2)}</pre>
                        </div>
                    </div>
                `).join('');
            }
        } catch (e) {
            container.innerHTML = `<div class="placeholder-text">Error loading tools: ${e.message}</div>`;
        }
    }

    // --- TAB: MCP Prompts ---
    async function loadMcpPrompts() {
        const group = document.getElementById('prompts-list-group');
        try {
            const res = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'prompts/list' })
            });
            if (res.ok) {
                const data = await res.json();
                availablePrompts = data.result?.prompts || [];

                if (availablePrompts.length === 0) {
                    group.innerHTML = '<div class="placeholder-text">No prompts declared.</div>';
                    return;
                }

                group.innerHTML = availablePrompts.map((p, idx) => `
                    <div class="prompt-item-card ${idx === 0 ? 'active' : ''}" data-name="${p.name}">
                        <div class="mcp-item-title">${p.name}</div>
                        <div class="mcp-item-desc">${p.description || ''}</div>
                    </div>
                `).join('');

                document.querySelectorAll('.prompt-item-card').forEach(card => {
                    card.addEventListener('click', () => {
                        document.querySelectorAll('.prompt-item-card').forEach(c => c.classList.remove('active'));
                        card.classList.add('active');
                        selectPrompt(card.getAttribute('data-name'));
                    });
                });

                if (availablePrompts.length > 0) {
                    selectPrompt(availablePrompts[0].name);
                }
            }
        } catch (e) {
            group.innerHTML = `<div class="placeholder-text">Error loading prompts: ${e.message}</div>`;
        }
    }

    function selectPrompt(promptName) {
        const prompt = availablePrompts.find(p => p.name === promptName);
        if (!prompt) return;

        document.getElementById('prompt-active-title').textContent = prompt.name;
        document.getElementById('prompt-active-desc').textContent = prompt.description || '';

        const argsContainer = document.getElementById('prompt-dynamic-args');
        const form = document.getElementById('prompt-exec-form');
        form.classList.remove('hidden');

        let argsHtml = '';
        if (prompt.arguments && prompt.arguments.length > 0) {
            prompt.arguments.forEach(arg => {
                argsHtml += `
                    <div class="form-group">
                        <label>${arg.name} ${arg.required ? '<span style="color:var(--danger)">*</span>' : '(optional)'}</label>
                        <input type="text" name="${arg.name}" ${arg.required ? 'required' : ''} placeholder="${arg.description || ''}">
                    </div>
                `;
            });
        } else {
            argsHtml = '<p class="tab-description">This prompt does not require any parameters.</p>';
        }
        argsContainer.innerHTML = argsHtml;

        form.onsubmit = async (e) => {
            e.preventDefault();
            const formData = new FormData(form);
            const argsObj = {};
            formData.forEach((val, key) => {
                if (val) argsObj[key] = val;
            });

            try {
                const res = await fetch('/mcp', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        jsonrpc: '2.0',
                        id: 3,
                        method: 'prompts/get',
                        params: {
                            name: prompt.name,
                            arguments: argsObj
                        }
                    })
                });

                if (res.ok) {
                    const data = await res.json();
                    document.getElementById('prompt-output-box').classList.remove('hidden');
                    document.getElementById('prompt-output-text').textContent = JSON.stringify(data.result, null, 2);
                }
            } catch (err) {
                alert(`Error executing prompt: ${err.message}`);
            }
        };
    }

    // --- TAB: MCP Resources ---
    async function loadMcpResources() {
        const tplList = document.getElementById('res-templates-list');
        const activeList = document.getElementById('res-active-list');

        try {
            // Resource Templates
            const tplRes = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 4, method: 'resources/templates/list' })
            });
            if (tplRes.ok) {
                const data = await tplRes.json();
                const templates = data.result?.resourceTemplates || [];
                tplList.innerHTML = templates.map(t => `
                    <li class="mcp-item res-uri-click" data-uri="${t.uriTemplate}">
                        <div class="mcp-item-title"><code>${t.uriTemplate}</code></div>
                        <div class="mcp-item-desc">${t.description}</div>
                    </li>
                `).join('');
            }

            // Active Resources
            const actRes = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ jsonrpc: '2.0', id: 5, method: 'resources/list' })
            });
            if (actRes.ok) {
                const data = await actRes.json();
                const resources = data.result?.resources || [];
                if (resources.length === 0) {
                    activeList.innerHTML = '<li class="mcp-item">No active job resources. Start a job to see it listed!</li>';
                } else {
                    activeList.innerHTML = resources.map(r => `
                        <li class="mcp-item res-uri-click" data-uri="${r.uri}">
                            <div class="mcp-item-title">${r.name}</div>
                            <div class="mcp-item-desc"><code>${r.uri}</code></div>
                        </li>
                    `).join('');
                }
            }

            document.querySelectorAll('.res-uri-click').forEach(el => {
                el.addEventListener('click', () => {
                    const uri = el.getAttribute('data-uri');
                    document.getElementById('res-uri-input').value = uri;
                    if (!uri.includes('{')) {
                        readResourceUri(uri);
                    }
                });
            });

        } catch (e) {
            console.error(e);
        }
    }

    document.getElementById('res-read-btn').addEventListener('click', () => {
        const uri = document.getElementById('res-uri-input').value.trim();
        if (uri) readResourceUri(uri);
    });

    async function readResourceUri(uri) {
        const container = document.getElementById('res-output-container');
        container.innerHTML = '<div class="placeholder-text">Reading resource...</div>';

        try {
            const res = await fetch('/mcp', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    jsonrpc: '2.0',
                    id: 6,
                    method: 'resources/read',
                    params: { uri: uri }
                })
            });

            if (res.ok) {
                const data = await res.json();
                if (data.error) {
                    container.innerHTML = `<div style="color:var(--danger)">Error: ${data.error.message}</div>`;
                    return;
                }

                const contents = data.result?.contents || [];
                if (contents.length === 0) {
                    container.innerHTML = '<div class="placeholder-text">Empty content.</div>';
                    return;
                }

                const item = contents[0];
                if (item.mimeType === 'image/png' && item.blob) {
                    container.innerHTML = `<img src="data:image/png;base64,${item.blob}" alt="Resource Image" class="res-image-preview">`;
                } else if (item.text) {
                    try {
                        const parsed = JSON.parse(item.text);
                        container.innerHTML = `<pre class="json-code">${JSON.stringify(parsed, null, 2)}</pre>`;
                    } catch {
                        container.innerHTML = `<pre class="json-code">${escapeHtml(item.text)}</pre>`;
                    }
                }
            }
        } catch (e) {
            container.innerHTML = `<div style="color:var(--danger)">Network Error: ${e.message}</div>`;
        }
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
