// DCB Admin Panel
(function() {
    'use strict';

    // Configuration from server-injected config.js
    const config = window.DCB_ADMIN_CONFIG || {
        basePath: '/dcb-admin',
        moduleKey: 'default',
        readOnly: false,
        title: 'DCB Admin'
    };

    const apiBase = `${config.basePath}/${config.moduleKey}/api`;
    let currentView = 'overview';
    let currentProjectionType = null;

    // Initialize
    document.addEventListener('DOMContentLoaded', () => {
        document.getElementById('app-title').textContent = config.title;
        document.title = config.title;

        if (config.readOnly) {
            document.getElementById('read-only-badge').style.display = 'inline-block';
        }

        setupNavigation();
        loadView('overview');
    });

    // Navigation
    function setupNavigation() {
        document.querySelectorAll('.nav-item').forEach(item => {
            item.addEventListener('click', (e) => {
                e.preventDefault();
                const view = item.dataset.view;
                navigateTo(view);
            });
        });
    }

    function navigateTo(view) {
        document.querySelectorAll('.nav-item').forEach(item => {
            item.classList.toggle('active', item.dataset.view === view);
        });

        currentView = view;
        currentProjectionType = null;
        loadView(view);
    }

    function loadView(view) {
        const content = document.getElementById('content');
        content.innerHTML = '<div class="loading"><div class="spinner"></div>Loading...</div>';

        const titles = {
            'overview': 'Overview',
            'processors': 'Processors',
            'checkpoints': 'Checkpoints',
            'projections': 'Projections',
            'dead-letters': 'Dead Letters'
        };
        document.getElementById('view-title').textContent = titles[view] || view;

        switch (view) {
            case 'overview': loadOverview(); break;
            case 'processors': loadProcessors(); break;
            case 'checkpoints': loadCheckpoints(); break;
            case 'projections': loadProjections(); break;
            case 'dead-letters': loadDeadLetters(); break;
        }
    }

    window.refresh = function() {
        if (currentProjectionType) {
            loadProjectionStates(currentProjectionType);
        } else {
            loadView(currentView);
        }
    };

    // API helpers
    async function api(endpoint, options = {}) {
        const response = await fetch(`${apiBase}${endpoint}`, {
            ...options,
            headers: {
                'Content-Type': 'application/json',
                ...options.headers
            }
        });

        if (!response.ok) {
            throw new Error(`API error: ${response.status}`);
        }

        if (response.status === 204) return null;
        return response.json();
    }

    // Overview
    async function loadOverview() {
        try {
            const [systemInfo, position, processors, deadLetterCount] = await Promise.all([
                api('/system/info'),
                api('/system/position'),
                api('/processors'),
                api('/dead-letters/count')
            ]);

            const activeProcessors = processors.filter(p => p.isActive).length;
            const totalLag = processors.reduce((sum, p) => sum + (p.lag || 0), 0);

            const content = document.getElementById('content');
            content.innerHTML = `
                <div class="stats-grid">
                    <div class="stat-card">
                        <div class="stat-label">Global Position</div>
                        <div class="stat-value">${formatNumber(position.position)}</div>
                    </div>
                    <div class="stat-card">
                        <div class="stat-label">Active Processors</div>
                        <div class="stat-value success">${activeProcessors} / ${processors.length}</div>
                    </div>
                    <div class="stat-card">
                        <div class="stat-label">Total Lag</div>
                        <div class="stat-value ${totalLag > 1000 ? 'warning' : ''}">${formatNumber(totalLag)}</div>
                    </div>
                    <div class="stat-card">
                        <div class="stat-label">Dead Letters</div>
                        <div class="stat-value ${deadLetterCount.count > 0 ? 'danger' : ''}">${formatNumber(deadLetterCount.count)}</div>
                    </div>
                </div>

                <div class="card">
                    <div class="card-header">
                        <h3>System Information</h3>
                    </div>
                    <div class="card-body">
                        <div class="details-list">
                            <div class="details-item">
                                <div class="details-label">Module</div>
                                <div class="details-value">${config.moduleKey}</div>
                            </div>
                            <div class="details-item">
                                <div class="details-label">Event Store</div>
                                <div class="details-value">${systemInfo.eventStoreType || 'Unknown'}</div>
                            </div>
                            <div class="details-item">
                                <div class="details-label">State Store</div>
                                <div class="details-value">${systemInfo.stateStoreType || 'Unknown'}</div>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="card">
                    <div class="card-header">
                        <h3>Processors</h3>
                    </div>
                    <div class="card-body">
                        <div class="table-container">
                            <table>
                                <thead>
                                    <tr>
                                        <th>Processor</th>
                                        <th>Status</th>
                                        <th>Position</th>
                                        <th>Lag</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    ${processors.map(p => `
                                        <tr>
                                            <td>${escapeHtml(p.processorId)}</td>
                                            <td>
                                                <span class="status-dot ${p.isActive ? 'active' : 'inactive'}"></span>
                                                ${p.isActive ? 'Active' : 'Inactive'}
                                            </td>
                                            <td>${formatNumber(p.position)}</td>
                                            <td>${formatNumber(p.lag)}</td>
                                        </tr>
                                    `).join('')}
                                </tbody>
                            </table>
                        </div>
                    </div>
                </div>
            `;
        } catch (error) {
            showError(error.message);
        }
    }

    // Processors
    async function loadProcessors() {
        try {
            const processors = await api('/processors');
            const content = document.getElementById('content');

            if (processors.length === 0) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#9881;</div>
                        <p>No processors registered</p>
                    </div>
                `;
                return;
            }

            content.innerHTML = `
                <div class="card">
                    <div class="card-body">
                        <div class="table-container">
                            <table>
                                <thead>
                                    <tr>
                                        <th>Processor ID</th>
                                        <th>Status</th>
                                        <th>Position</th>
                                        <th>Lag</th>
                                        <th>Dead Letters</th>
                                        ${!config.readOnly ? '<th>Actions</th>' : ''}
                                    </tr>
                                </thead>
                                <tbody>
                                    ${processors.map(p => `
                                        <tr>
                                            <td><strong>${escapeHtml(p.processorId)}</strong></td>
                                            <td>
                                                <span class="status-dot ${p.isActive ? 'active' : 'inactive'}"></span>
                                                ${p.isActive ? 'Active' : 'Inactive'}
                                            </td>
                                            <td>${formatNumber(p.position)}</td>
                                            <td>
                                                <span class="${p.lag > 1000 ? 'badge badge-warning' : ''}">${formatNumber(p.lag)}</span>
                                            </td>
                                            <td>
                                                ${p.deadLetterCount > 0
                                                    ? `<span class="badge badge-danger">${p.deadLetterCount}</span>`
                                                    : '0'}
                                            </td>
                                            ${!config.readOnly ? `
                                                <td>
                                                    <button class="btn btn-sm btn-secondary" onclick="toggleProcessor('${escapeHtml(p.processorId)}', ${!p.isActive})">
                                                        ${p.isActive ? 'Deactivate' : 'Activate'}
                                                    </button>
                                                </td>
                                            ` : ''}
                                        </tr>
                                    `).join('')}
                                </tbody>
                            </table>
                        </div>
                    </div>
                </div>
            `;
        } catch (error) {
            showError(error.message);
        }
    }

    window.toggleProcessor = async function(processorId, activate) {
        try {
            const action = activate ? 'activate' : 'deactivate';
            await api(`/processors/${encodeURIComponent(processorId)}/${action}`, { method: 'POST' });
            loadProcessors();
        } catch (error) {
            alert('Failed to toggle processor: ' + error.message);
        }
    };

    // Checkpoints
    async function loadCheckpoints() {
        try {
            const [checkpoints, position] = await Promise.all([
                api('/checkpoints'),
                api('/system/position')
            ]);

            const content = document.getElementById('content');

            if (checkpoints.length === 0) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#10003;</div>
                        <p>No checkpoints found</p>
                    </div>
                `;
                return;
            }

            content.innerHTML = `
                <div class="card">
                    <div class="card-header">
                        <h3>Current Global Position: ${formatNumber(position.position)}</h3>
                    </div>
                    <div class="card-body">
                        <div class="table-container">
                            <table>
                                <thead>
                                    <tr>
                                        <th>Processor ID</th>
                                        <th>Position</th>
                                        <th>Lag</th>
                                        <th>Last Updated</th>
                                        ${!config.readOnly ? '<th>Actions</th>' : ''}
                                    </tr>
                                </thead>
                                <tbody>
                                    ${checkpoints.map(c => {
                                        const lag = position.position - c.position;
                                        return `
                                            <tr>
                                                <td><strong>${escapeHtml(c.processorId)}</strong></td>
                                                <td>${formatNumber(c.position)}</td>
                                                <td>
                                                    <span class="${lag > 1000 ? 'badge badge-warning' : ''}">${formatNumber(lag)}</span>
                                                </td>
                                                <td>${formatDate(c.updatedAt)}</td>
                                                ${!config.readOnly ? `
                                                    <td>
                                                        <button class="btn btn-sm btn-danger" onclick="confirmResetCheckpoint('${escapeHtml(c.processorId)}')">
                                                            Reset (Rebuild)
                                                        </button>
                                                    </td>
                                                ` : ''}
                                            </tr>
                                        `;
                                    }).join('')}
                                </tbody>
                            </table>
                        </div>
                    </div>
                </div>
            `;
        } catch (error) {
            showError(error.message);
        }
    }

    window.confirmResetCheckpoint = function(processorId) {
        showModal('Reset Checkpoint', `
            <p>Are you sure you want to reset the checkpoint for <strong>${escapeHtml(processorId)}</strong>?</p>
            <p>This will trigger a full rebuild of this processor from the beginning.</p>
            <div class="confirm-actions">
                <button class="btn btn-secondary" onclick="closeModal()">Cancel</button>
                <button class="btn btn-danger" onclick="resetCheckpoint('${escapeHtml(processorId)}')">Reset</button>
            </div>
        `);
    };

    window.resetCheckpoint = async function(processorId) {
        try {
            await api(`/checkpoints/${encodeURIComponent(processorId)}`, { method: 'DELETE' });
            closeModal();
            loadCheckpoints();
        } catch (error) {
            alert('Failed to reset checkpoint: ' + error.message);
        }
    };

    // Projections
    async function loadProjections() {
        try {
            const types = await api('/projection-states/types');
            const content = document.getElementById('content');

            if (types.length === 0) {
                content.innerHTML = `
                    <div class="empty-state">
                        <div class="empty-state-icon">&#9638;</div>
                        <p>No projection types found</p>
                    </div>
                `;
                return;
            }

            content.innerHTML = `
                <div class="card">
                    <div class="card-header">
                        <h3>Projection Types</h3>
                    </div>
                    <div class="card-body">
                        <div class="table-container">
                            <table>
                                <thead>
                                    <tr>
                                        <th>Type</th>
                                        <th>Actions</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    ${types.map(t => `
                                        <tr>
                                            <td><strong>${escapeHtml(t)}</strong></td>
                                            <td>
                                                <button class="btn btn-sm btn-primary" onclick="loadProjectionStates('${escapeHtml(t)}')">
                                                    View States
                                                </button>
                                            </td>
                                        </tr>
                                    `).join('')}
                                </tbody>
                            </table>
                        </div>
                    </div>
                </div>
            `;
        } catch (error) {
            showError(error.message);
        }
    }

    window.loadProjectionStates = async function(projectionType, page = 1) {
        currentProjectionType = projectionType;
        document.getElementById('view-title').textContent = `Projections: ${projectionType}`;

        try {
            const result = await api(`/projection-states/${encodeURIComponent(projectionType)}?page=${page}&pageSize=20`);
            const content = document.getElementById('content');

            content.innerHTML = `
                <div style="margin-bottom: 1rem;">
                    <button class="btn btn-secondary" onclick="loadProjections()">
                        &larr; Back to Types
                    </button>
                </div>

                <div class="card">
                    <div class="card-body">
                        ${result.items.length === 0 ? `
                            <div class="empty-state">
                                <p>No projection states found</p>
                            </div>
                        ` : `
                            <div class="table-container">
                                <table>
                                    <thead>
                                        <tr>
                                            <th>Document ID</th>
                                            <th>Tenant</th>
                                            <th>Version</th>
                                            <th>Last Updated</th>
                                            <th>Actions</th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        ${result.items.map(s => `
                                            <tr>
                                                <td><strong>${escapeHtml(s.documentId)}</strong></td>
                                                <td>${escapeHtml(s.tenantId || '-')}</td>
                                                <td>${s.version}</td>
                                                <td>${formatDate(s.updatedAt)}</td>
                                                <td>
                                                    <button class="btn btn-sm btn-primary" onclick="viewProjectionState('${escapeHtml(projectionType)}', '${escapeHtml(s.documentId)}', ${s.tenantId ? `'${escapeHtml(s.tenantId)}'` : 'null'})">
                                                        View
                                                    </button>
                                                </td>
                                            </tr>
                                        `).join('')}
                                    </tbody>
                                </table>
                            </div>
                            ${renderPagination(result, (p) => `loadProjectionStates('${escapeHtml(projectionType)}', ${p})`)}
                        `}
                    </div>
                </div>
            `;
        } catch (error) {
            showError(error.message);
        }
    };

    window.viewProjectionState = async function(projectionType, documentId, tenantId) {
        try {
            let url = `/projection-states/${encodeURIComponent(projectionType)}/${encodeURIComponent(documentId)}`;
            if (tenantId) url += `?tenantId=${encodeURIComponent(tenantId)}`;

            const state = await api(url);
            showModal(`Projection State: ${documentId}`, `
                <div class="details-list" style="margin-bottom: 1rem;">
                    <div class="details-item">
                        <div class="details-label">Type</div>
                        <div class="details-value">${escapeHtml(projectionType)}</div>
                    </div>
                    <div class="details-item">
                        <div class="details-label">Document ID</div>
                        <div class="details-value">${escapeHtml(documentId)}</div>
                    </div>
                    <div class="details-item">
                        <div class="details-label">Version</div>
                        <div class="details-value">${state.version}</div>
                    </div>
                </div>
                <h4 style="margin-bottom: 0.5rem;">State</h4>
                <div class="json-viewer">${formatJson(state.state)}</div>
            `);
        } catch (error) {
            alert('Failed to load projection state: ' + error.message);
        }
    };

    // Dead Letters
    async function loadDeadLetters(page = 1, processorId = null) {
        try {
            let url = `/dead-letters?page=${page}&pageSize=20`;
            if (processorId) url += `&processorId=${encodeURIComponent(processorId)}`;

            const [result, processors] = await Promise.all([
                api(url),
                api('/processors')
            ]);

            const content = document.getElementById('content');

            content.innerHTML = `
                <div class="card">
                    <div class="card-header">
                        <h3>Dead Letters</h3>
                        <div style="display: flex; gap: 0.5rem; align-items: center;">
                            <select id="processor-filter" onchange="filterDeadLetters()">
                                <option value="">All Processors</option>
                                ${processors.map(p => `
                                    <option value="${escapeHtml(p.processorId)}" ${processorId === p.processorId ? 'selected' : ''}>
                                        ${escapeHtml(p.processorId)} (${p.deadLetterCount})
                                    </option>
                                `).join('')}
                            </select>
                            ${!config.readOnly && processorId ? `
                                <button class="btn btn-sm btn-danger" onclick="confirmClearDeadLetters('${escapeHtml(processorId)}')">
                                    Clear All
                                </button>
                            ` : ''}
                        </div>
                    </div>
                    <div class="card-body">
                        ${result.items.length === 0 ? `
                            <div class="empty-state">
                                <div class="empty-state-icon">&#9888;</div>
                                <p>No dead letters found</p>
                            </div>
                        ` : `
                            <div class="table-container">
                                <table>
                                    <thead>
                                        <tr>
                                            <th>Event Type</th>
                                            <th>Processor</th>
                                            <th>Position</th>
                                            <th>Failed At</th>
                                            <th>Actions</th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        ${result.items.map(d => `
                                            <tr>
                                                <td><strong>${escapeHtml(d.eventType)}</strong></td>
                                                <td>${escapeHtml(d.processorId)}</td>
                                                <td>${formatNumber(d.position)}</td>
                                                <td>${formatDate(d.failedAt)}</td>
                                                <td>
                                                    <button class="btn btn-sm btn-primary" onclick="viewDeadLetter('${d.id}')">
                                                        View
                                                    </button>
                                                    ${!config.readOnly ? `
                                                        <button class="btn btn-sm btn-danger" onclick="confirmRemoveDeadLetter('${d.id}')">
                                                            Remove
                                                        </button>
                                                    ` : ''}
                                                </td>
                                            </tr>
                                        `).join('')}
                                    </tbody>
                                </table>
                            </div>
                            ${renderPagination(result, (p) => `loadDeadLettersPage(${p}, ${processorId ? `'${escapeHtml(processorId)}'` : 'null'})`)}
                        `}
                    </div>
                </div>
            `;
        } catch (error) {
            showError(error.message);
        }
    }

    window.loadDeadLettersPage = function(page, processorId) {
        loadDeadLetters(page, processorId);
    };

    window.filterDeadLetters = function() {
        const processorId = document.getElementById('processor-filter').value || null;
        loadDeadLetters(1, processorId);
    };

    window.viewDeadLetter = async function(id) {
        try {
            const deadLetter = await api(`/dead-letters/${id}`);
            showModal(`Dead Letter Details`, `
                <div class="details-list" style="margin-bottom: 1rem;">
                    <div class="details-item">
                        <div class="details-label">ID</div>
                        <div class="details-value">${escapeHtml(deadLetter.id)}</div>
                    </div>
                    <div class="details-item">
                        <div class="details-label">Processor</div>
                        <div class="details-value">${escapeHtml(deadLetter.processorId)}</div>
                    </div>
                    <div class="details-item">
                        <div class="details-label">Event Type</div>
                        <div class="details-value">${escapeHtml(deadLetter.eventType)}</div>
                    </div>
                    <div class="details-item">
                        <div class="details-label">Position</div>
                        <div class="details-value">${formatNumber(deadLetter.position)}</div>
                    </div>
                    <div class="details-item">
                        <div class="details-label">Stream</div>
                        <div class="details-value">${escapeHtml(deadLetter.streamId)}</div>
                    </div>
                    <div class="details-item">
                        <div class="details-label">Failed At</div>
                        <div class="details-value">${formatDate(deadLetter.failedAt)}</div>
                    </div>
                </div>
                <h4 style="margin-bottom: 0.5rem;">Error</h4>
                <div class="json-viewer" style="color: var(--danger); margin-bottom: 1rem;">${escapeHtml(deadLetter.error || 'No error message')}</div>
                <h4 style="margin-bottom: 0.5rem;">Event Data</h4>
                <div class="json-viewer">${formatJson(deadLetter.eventData)}</div>
            `);
        } catch (error) {
            alert('Failed to load dead letter: ' + error.message);
        }
    };

    window.confirmRemoveDeadLetter = function(id) {
        showModal('Remove Dead Letter', `
            <p>Are you sure you want to remove this dead letter?</p>
            <div class="confirm-actions">
                <button class="btn btn-secondary" onclick="closeModal()">Cancel</button>
                <button class="btn btn-danger" onclick="removeDeadLetter('${id}')">Remove</button>
            </div>
        `);
    };

    window.removeDeadLetter = async function(id) {
        try {
            await api(`/dead-letters/${id}`, { method: 'DELETE' });
            closeModal();
            loadDeadLetters();
        } catch (error) {
            alert('Failed to remove dead letter: ' + error.message);
        }
    };

    window.confirmClearDeadLetters = function(processorId) {
        showModal('Clear Dead Letters', `
            <p>Are you sure you want to remove all dead letters for <strong>${escapeHtml(processorId)}</strong>?</p>
            <div class="confirm-actions">
                <button class="btn btn-secondary" onclick="closeModal()">Cancel</button>
                <button class="btn btn-danger" onclick="clearDeadLetters('${escapeHtml(processorId)}')">Clear All</button>
            </div>
        `);
    };

    window.clearDeadLetters = async function(processorId) {
        try {
            await api(`/dead-letters?processorId=${encodeURIComponent(processorId)}`, { method: 'DELETE' });
            closeModal();
            loadDeadLetters();
        } catch (error) {
            alert('Failed to clear dead letters: ' + error.message);
        }
    };

    // Modal
    function showModal(title, content) {
        document.getElementById('modal-title').textContent = title;
        document.getElementById('modal-body').innerHTML = content;
        document.getElementById('modal').style.display = 'flex';
    }

    window.closeModal = function() {
        document.getElementById('modal').style.display = 'none';
    };

    // Utilities
    function showError(message) {
        const content = document.getElementById('content');
        content.innerHTML = `
            <div class="empty-state">
                <div class="empty-state-icon" style="color: var(--danger);">&#9888;</div>
                <p>Error: ${escapeHtml(message)}</p>
                <button class="btn btn-primary" onclick="refresh()" style="margin-top: 1rem;">Retry</button>
            </div>
        `;
    }

    function escapeHtml(str) {
        if (str === null || str === undefined) return '';
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }

    function formatNumber(num) {
        if (num === null || num === undefined) return '-';
        return num.toLocaleString();
    }

    function formatDate(dateStr) {
        if (!dateStr) return '-';
        const date = new Date(dateStr);
        return date.toLocaleString();
    }

    function formatJson(obj) {
        if (!obj) return 'null';
        try {
            if (typeof obj === 'string') {
                obj = JSON.parse(obj);
            }
            return escapeHtml(JSON.stringify(obj, null, 2));
        } catch {
            return escapeHtml(String(obj));
        }
    }

    function renderPagination(result, onClickFn) {
        if (result.totalPages <= 1) return '';

        const pages = [];
        for (let i = 1; i <= result.totalPages; i++) {
            if (i === 1 || i === result.totalPages ||
                (i >= result.page - 2 && i <= result.page + 2)) {
                pages.push(i);
            } else if (pages[pages.length - 1] !== '...') {
                pages.push('...');
            }
        }

        return `
            <div class="pagination">
                <button class="btn btn-sm btn-secondary" onclick="${onClickFn}(${result.page - 1})" ${result.page <= 1 ? 'disabled' : ''}>
                    &laquo; Prev
                </button>
                <span class="pagination-info">
                    Page ${result.page} of ${result.totalPages}
                </span>
                <button class="btn btn-sm btn-secondary" onclick="${onClickFn}(${result.page + 1})" ${result.page >= result.totalPages ? 'disabled' : ''}>
                    Next &raquo;
                </button>
            </div>
        `;
    }
})();
