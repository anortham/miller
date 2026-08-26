// Miller dashboard site glue — delegated handlers, htmx helpers, no inline onclick.

(function () {
    var openIssueDetails = window.__millerOpenIssueDetails || (window.__millerOpenIssueDetails = new Set());

    // Filter/sort/stale-open state for the #workspace-index section. It survives the 30s htmx
    // poll that swaps the whole section (mirrors openIssueDetails above): the store lives at
    // module scope, outside the swapped DOM, so a swap cannot clear it — morph rewrites node
    // attributes, so state parked on a node would be clobbered by the very swap it must outlive.
    var workspaceIndexState = window.__millerWorkspaceIndexState || (window.__millerWorkspaceIndexState = {
        query: '',
        autoOpenedStale: false,
        sortColumn: null,
        sortDir: 'asc',
        staleOpen: false,
    });

    // Same contract for the 30s #telemetry-panel poll.
    var telemetrySortState = window.__millerTelemetrySortState || (window.__millerTelemetrySortState = {
        sortColumn: null,
        sortDir: 'desc',
    });

    // ETag per polled element id. Module scope, not the DOM: a morph swap replaces attributes on the
    // live element, so an ETag parked on the node itself would be clobbered by the very swap it guards.
    var fragmentETags = {};

    function pollTriggerElement(elt) {
        return elt && typeof elt.getAttribute === 'function' &&
            elt.getAttribute('data-poll-trigger') && elt.id
            ? elt
            : null;
    }

    function issueKey(details) {
        return details.getAttribute('data-issue-id') || details.id || '';
    }

    function captureIssueDetailsState(root) {
        (root || document).querySelectorAll('details[data-issue-details]').forEach(function (details) {
            var key = issueKey(details);
            if (!key) {
                return;
            }
            if (details.open) {
                openIssueDetails.add(key);
            } else {
                openIssueDetails.delete(key);
            }
        });
    }

    window.rememberIssueDetailsState = function (root) {
        (root || document).querySelectorAll('details[data-issue-details]').forEach(function (details) {
            var key = issueKey(details);
            if (!key) {
                return;
            }
            if (openIssueDetails.has(key)) {
                details.open = true;
            }
            if (details.getAttribute('data-issue-bound') === '1') {
                return;
            }
            details.setAttribute('data-issue-bound', '1');
            details.addEventListener('toggle', function () {
                if (details.open) {
                    openIssueDetails.add(key);
                } else {
                    openIssueDetails.delete(key);
                }
            });
        });
    };

    function markCopied(button, label) {
        var original = button.getAttribute('data-copy-label') || button.textContent || 'Copy';
        button.setAttribute('data-copy-label', original);
        button.textContent = label;
        window.setTimeout(function () {
            button.textContent = original;
        }, 1200);
    }

    function copyTextFromTarget(targetId) {
        var target = targetId ? document.getElementById(targetId) : null;
        if (!target) {
            return false;
        }
        var text = target.value || target.textContent || '';
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text);
        }
        if (target.select) {
            target.select();
            document.execCommand('copy');
        }
        return Promise.resolve();
    }

    // The visible label is CSS-driven off html[data-theme] (see dashboard.css) so it cannot flash a wrong
    // value before this script runs. Only the pressed state, which CSS cannot express, is written here.
    function updateThemeButton(theme) {
        document.querySelectorAll('[data-toggle-theme]').forEach(function (button) {
            button.setAttribute('aria-pressed', theme === 'dark' ? 'true' : 'false');
        });
    }

    window.toggleTheme = function () {
        var current = document.documentElement.getAttribute('data-theme') || 'light';
        var next = current === 'dark' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-theme', next);
        localStorage.setItem('theme', next);
        updateThemeButton(next);
    };

    function updateRelativeTimes(root) {
        var now = Date.now();
        (root || document).querySelectorAll('time.rel-ts[data-ts]').forEach(function (el) {
            var parsed = Date.parse(el.getAttribute('data-ts'));
            if (isNaN(parsed)) {
                return;
            }
            var seconds = Math.max(0, Math.floor((now - parsed) / 1000));
            var label;
            if (seconds < 5) {
                label = 'just now';
            } else if (seconds < 60) {
                label = seconds + 's ago';
            } else if (seconds < 3600) {
                label = Math.floor(seconds / 60) + 'm ago';
            } else if (seconds < 86400) {
                label = Math.floor(seconds / 3600) + 'h ago';
            } else {
                label = Math.floor(seconds / 86400) + 'd ago';
            }
            if (!el.title) {
                el.title = el.getAttribute('data-ts');
            }
            el.textContent = label;
        });
    }

    // The two sortable tables. Each descriptor names its panel, the grids whose rows are reordered
    // (re-appending rows inside them leaves the header row/thead in place), and the ancestor that
    // carries aria-sort — a button is not a table header, so the state must land on the element with
    // role="columnheader" or on the <th> itself.
    var sortableTables = [
        {
            panelId: 'workspace-index',
            store: workspaceIndexState,
            gridSelector: '.ws-index',
            rowSelector: '.ws-index-row',
            nameColumn: 'workspace',
            nameAttribute: 'data-sort-name',
            headerSelector: '[role="columnheader"]',
        },
        {
            panelId: 'telemetry-panel',
            store: telemetrySortState,
            gridSelector: 'tbody',
            rowSelector: '.telemetry-row',
            nameColumn: 'tool',
            nameAttribute: 'data-sort-tool',
            headerSelector: 'th',
        },
    ];

    function sortablePanel(table) {
        return document.getElementById(table.panelId);
    }

    function tableForElement(el) {
        for (var i = 0; i < sortableTables.length; i++) {
            if (el.closest('#' + sortableTables[i].panelId)) {
                return sortableTables[i];
            }
        }
        return null;
    }

    function applyTableSort(table) {
        var panel = sortablePanel(table);
        var col = table.store.sortColumn;
        if (!panel || !col) {
            return;
        }
        var dir = table.store.sortDir === 'desc' ? -1 : 1;
        panel.querySelectorAll(table.gridSelector).forEach(function (grid) {
            var rows = Array.prototype.slice.call(grid.querySelectorAll(table.rowSelector));
            rows.sort(function (a, b) {
                if (col === table.nameColumn) {
                    var an = (a.getAttribute(table.nameAttribute) || '').toLowerCase();
                    var bn = (b.getAttribute(table.nameAttribute) || '').toLowerCase();
                    if (an < bn) return -dir;
                    if (an > bn) return dir;
                    return 0;
                }
                var av = parseFloat(a.getAttribute('data-sort-' + col));
                var bv = parseFloat(b.getAttribute('data-sort-' + col));
                if (isNaN(av)) av = -1;
                if (isNaN(bv)) bv = -1;
                return (av - bv) * dir;
            });
            rows.forEach(function (row) { grid.appendChild(row); });
        });
    }

    function reflectSortButtons(table) {
        var panel = sortablePanel(table);
        if (!panel) {
            return;
        }
        panel.querySelectorAll('[data-sort-col]').forEach(function (button) {
            var header = button.closest(table.headerSelector) || button;
            if (table.store.sortColumn === button.getAttribute('data-sort-col')) {
                header.setAttribute('aria-sort', table.store.sortDir === 'desc' ? 'descending' : 'ascending');
            } else {
                header.setAttribute('aria-sort', 'none');
            }
        });
    }

    function onSortClick(button) {
        var table = tableForElement(button);
        var col = button.getAttribute('data-sort-col');
        if (!table || !col) {
            return;
        }
        if (table.store.sortColumn === col) {
            table.store.sortDir = table.store.sortDir === 'asc' ? 'desc' : 'asc';
        } else {
            table.store.sortColumn = col;
            // Numeric columns default to descending (biggest first); the name column to ascending.
            table.store.sortDir = col === table.nameColumn ? 'asc' : 'desc';
        }
        applyTableSort(table);
        reflectSortButtons(table);
    }

    // A row's own textContent also carries the remove-confirm form ("Cancel", "rebuildable via
    // workspace open"), so filtering on it matches every row for those words. Read the data cells only.
    function workspaceRowFilterText(row) {
        var text = '';
        row.querySelectorAll('.workspace-row-main, .ws-cell:not(.ws-row-actions)').forEach(function (cell) {
            text += cell.textContent + ' ';
        });
        return text.toLowerCase();
    }

    function persistStaleOpen(panel) {
        var stale = panel.querySelector('.ws-stale-collapse');
        workspaceIndexState.staleOpen = stale ? stale.open : false;
    }

    function applyWorkspaceFilter() {
        var panel = document.getElementById('workspace-index');
        if (!panel) {
            return;
        }
        var q = (workspaceIndexState.query || '').trim().toLowerCase();
        var anyVisible = false;
        panel.querySelectorAll('.ws-index-row').forEach(function (row) {
            var hide = q.length > 0 && workspaceRowFilterText(row).indexOf(q) < 0;
            row.hidden = hide;
            if (!hide) anyVisible = true;
        });
        // Matches inside the collapsed stale section are invisible unless it is open;
        // auto-open while filtering, and restore only what we auto-opened.
        var stale = panel.querySelector('.ws-stale-collapse');
        if (stale) {
            if (q.length > 0 && !stale.open) {
                stale.open = true;
                workspaceIndexState.autoOpenedStale = true;
            } else if (q.length === 0 && workspaceIndexState.autoOpenedStale) {
                stale.open = false;
                workspaceIndexState.autoOpenedStale = false;
            }
        }
        var emptyNote = panel.querySelector('.ws-filter-empty');
        if (emptyNote) emptyNote.hidden = q.length === 0 || anyVisible;
        persistStaleOpen(panel);
    }

    // A morph swap patches these panels in place, so the server's freshly rendered rows arrive
    // unsorted, unfiltered, and with the stale section closed. Reapply the reader's view.
    function rehydrateSortableTables() {
        sortableTables.forEach(function (table) {
            applyTableSort(table);
            reflectSortButtons(table);
        });

        var panel = document.getElementById('workspace-index');
        if (!panel) {
            return;
        }
        var stale = panel.querySelector('.ws-stale-collapse');
        if (stale) {
            if (workspaceIndexState.staleOpen) {
                stale.open = true;
            }
            if (stale.getAttribute('data-stale-bound') !== '1') {
                stale.setAttribute('data-stale-bound', '1');
                stale.addEventListener('toggle', function () { persistStaleOpen(panel); });
            }
        }
        var filter = panel.querySelector('#workspace-filter');
        // Reassigning an identical value can move the caret, so only write a value that differs.
        if (filter && filter.value !== workspaceIndexState.query) {
            filter.value = workspaceIndexState.query;
        }
        applyWorkspaceFilter();
    }

    function applyVisibilityPolling() {
        document.querySelectorAll('[data-poll-trigger]').forEach(function (el) {
            if (document.visibilityState === 'hidden') {
                el.removeAttribute('hx-trigger');
            } else {
                el.setAttribute('hx-trigger', el.getAttribute('data-poll-trigger'));
            }
            if (window.htmx) {
                window.htmx.process(el);
            }
        });
    }

    window.showDashboardToast = function (message, tone) {
        var container = document.getElementById('dashboard-toast-container');
        if (!container) {
            return;
        }
        var toast = document.createElement('div');
        toast.className = 'dashboard-toast dashboard-toast-' + (tone || 'danger');
        toast.setAttribute('role', 'alert');
        toast.textContent = message;
        container.appendChild(toast);
        window.setTimeout(function () {
            toast.classList.add('dashboard-toast-hide');
            window.setTimeout(function () {
                toast.remove();
            }, 300);
        }, 5000);
    };

    function showHtmxFailureToast(message) {
        window.showDashboardToast(message, 'danger');
    }

    function mirrorNoticeAsToast() {
        var notice = document.querySelector('[data-notice]');
        if (!notice) {
            return;
        }
        var text = (notice.textContent || '').trim();
        if (text) {
            window.showDashboardToast(text, notice.getAttribute('data-notice-tone') || 'ok');
        }
    }

    document.addEventListener('click', function (event) {
        var toggle = event.target.closest('[data-toggle-theme]');
        if (toggle) {
            window.toggleTheme();
            return;
        }

        var closer = event.target.closest('[data-close-details]');
        if (closer) {
            var details = closer.closest('details');
            if (details) {
                event.preventDefault();
                details.open = false;
            }
            return;
        }

        var sortButton = event.target.closest('[data-sort-col]');
        if (sortButton) {
            onSortClick(sortButton);
            return;
        }

        var copyButton = event.target.closest('[data-copy-target]');
        if (copyButton) {
            var targetId = copyButton.getAttribute('data-copy-target');
            var promise = copyTextFromTarget(targetId);
            if (promise && typeof promise.then === 'function') {
                promise
                    .then(function () { markCopied(copyButton, 'Copied'); })
                    .catch(function () {
                        copyTextFromTarget(targetId);
                        markCopied(copyButton, 'Copied');
                    });
            } else {
                markCopied(copyButton, 'Copied');
            }
        }
    });

    document.addEventListener('input', function (event) {
        var filter = event.target.closest && event.target.closest('#workspace-filter');
        if (!filter) {
            return;
        }
        workspaceIndexState.query = filter.value;
        applyWorkspaceFilter();
    });

    document.addEventListener('DOMContentLoaded', function () {
        updateThemeButton(document.documentElement.getAttribute('data-theme') || 'light');
        updateRelativeTimes(document);
        window.rememberIssueDetailsState(document);
        rehydrateSortableTables();
        applyVisibilityPolling();
        // Runs before the first 30s poll, whose fragment carries no notice and morphs the inline
        // paragraph away — the toast is what survives for a reader who looked away.
        mirrorNoticeAsToast();
    });

    document.addEventListener('visibilitychange', applyVisibilityPolling);

    // "/" jumps to the workspace filter, the search convention users arrive with. Never steal the key
    // while the user is typing (including into the filter itself), and it only exists on the home page.
    document.addEventListener('keydown', function (event) {
        if (event.key !== '/' || event.metaKey || event.ctrlKey || event.altKey) {
            return;
        }
        var active = document.activeElement;
        if (active && (active.isContentEditable ||
            active.tagName === 'INPUT' ||
            active.tagName === 'TEXTAREA' ||
            active.tagName === 'SELECT')) {
            return;
        }
        var filter = document.getElementById('workspace-filter');
        if (!filter) {
            return;
        }
        event.preventDefault();
        filter.focus();
        filter.select();
    });

    document.addEventListener('htmx:configRequest', function (event) {
        var detail = event.detail;
        if (!detail || !detail.headers) {
            return;
        }
        // Server-side CSRF gate on the antiforgery-free POSTs: a cross-origin form cannot set a custom
        // header, and a cross-origin fetch that sets one preflights against a server that never answers.
        // Sent on every htmx request, GETs included — harmless there, and one rule cannot drift per-route.
        detail.headers['X-Miller-Dashboard'] = '1';
        var elt = pollTriggerElement(detail.elt);
        var etag = elt ? fragmentETags[elt.id] : null;
        if (etag) {
            detail.headers['If-None-Match'] = etag;
        }
    });

    document.addEventListener('htmx:afterOnLoad', function (event) {
        var detail = event.detail;
        var elt = pollTriggerElement(detail && detail.elt);
        if (!elt || !detail.xhr) {
            return;
        }
        var etag = detail.xhr.getResponseHeader('ETag');
        if (etag) {
            fragmentETags[elt.id] = etag;
        }
    });

    document.addEventListener('htmx:beforeSwap', function (event) {
        // htmx 2's default responseHandling swaps any 3xx, so an unguarded 304 would swap the
        // panel away with its empty body. Nothing changed — keep the live DOM exactly as it is.
        if (event.detail && event.detail.xhr && event.detail.xhr.status === 304) {
            event.detail.shouldSwap = false;
            return;
        }
        captureIssueDetailsState(event.target);
    });

    document.addEventListener('htmx:afterSwap', function (event) {
        updateRelativeTimes(event.target);
        window.rememberIssueDetailsState(event.target);
        rehydrateSortableTables();
        applyVisibilityPolling();
    });

    window.setInterval(function () {
        updateRelativeTimes(document);
    }, 5000);

    // Success toast for actions that opt in via data-toast-success (e.g. Open folder). The action
    // itself has no visible swap (hx-swap="none"), so without this it would look like nothing happened.
    document.body.addEventListener('htmx:afterRequest', function (event) {
        var detail = event.detail;
        var elt = detail && detail.elt;
        if (!elt || typeof elt.getAttribute !== 'function') {
            return;
        }
        var message = elt.getAttribute('data-toast-success');
        if (message && detail.successful) {
            window.showDashboardToast(message, 'ok');
        }
    });

    document.body.addEventListener('htmx:responseError', function (event) {
        var status = event.detail && event.detail.xhr ? event.detail.xhr.status : 0;
        var msg = status === 400
            ? 'Request was rejected. Refresh the page and try again.'
            : 'Something went wrong. Your action was not saved.';
        showHtmxFailureToast(msg);
    });

    document.body.addEventListener('htmx:sendError', function () {
        showHtmxFailureToast('Could not reach the dashboard server.');
    });

    document.body.addEventListener('htmx:timeout', function () {
        showHtmxFailureToast('The request timed out. Please try again.');
    });
})();
