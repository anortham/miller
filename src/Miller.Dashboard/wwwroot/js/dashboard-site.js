// Miller dashboard site glue — delegated handlers, htmx helpers, no inline onclick.

(function () {
    var openIssueDetails = window.__millerOpenIssueDetails || (window.__millerOpenIssueDetails = new Set());

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

    function updateThemeButton(theme) {
        var label = document.getElementById('theme-toggle-label');
        if (label) {
            label.textContent = theme === 'dark' ? 'Light' : 'Dark';
        }
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

    document.addEventListener('click', function (event) {
        var toggle = event.target.closest('[data-toggle-theme]');
        if (toggle) {
            window.toggleTheme();
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
    });

    document.addEventListener('visibilitychange', applyVisibilityPolling);

    document.addEventListener('htmx:beforeSwap', function (event) {
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
