// Miller dashboard site glue — delegated handlers, htmx helpers, no inline onclick.

(function () {
    var openIssueDetails = window.__millerOpenIssueDetails || (window.__millerOpenIssueDetails = new Set());

    // Filter/sort/stale-open state for the #workspace-index section. It survives the 30s htmx
    // poll that swaps the whole section (mirrors openIssueDetails above): the store lives at
    // module scope, outside the swapped DOM, so a swap cannot clear it. The workspaceIndexFilter
    // Alpine component (alpine-components.js) rehydrates from this store on init() and writes back
    // on every change; declaring it here guarantees the shape exists before Alpine's deferred load.
    window.__millerWorkspaceIndexState = window.__millerWorkspaceIndexState || {
        query: '',
        autoOpenedStale: false,
        sortColumn: null,
        sortDir: 'asc',
        staleOpen: false,
    };

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

    document.addEventListener('DOMContentLoaded', function () {
        updateThemeButton(document.documentElement.getAttribute('data-theme') || 'light');
        updateRelativeTimes(document);
        window.rememberIssueDetailsState(document);
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
