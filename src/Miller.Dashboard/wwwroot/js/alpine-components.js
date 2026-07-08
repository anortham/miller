// Miller dashboard — Alpine CSP component registrations.
// Factories run as plain JS; attribute expressions stay in the CSP-safe subset.

document.addEventListener('alpine:init', function () {
    Alpine.data('workspaceIndexFilter', function () {
        return {
            query: '',
            autoOpenedStale: false,
            applyFilter: function () {
                var root = this.$el;
                var q = (this.query || '').trim().toLowerCase();
                var anyVisible = false;
                root.querySelectorAll('.ws-index-row').forEach(function (row) {
                    var hide = q.length > 0 && row.textContent.toLowerCase().indexOf(q) < 0;
                    row.hidden = hide;
                    if (!hide) anyVisible = true;
                });
                // Matches inside the collapsed stale section are invisible unless it is open;
                // auto-open while filtering, and restore only what we auto-opened.
                var stale = root.querySelector('.ws-stale-collapse');
                if (stale) {
                    if (q.length > 0 && !stale.open) {
                        stale.open = true;
                        this.autoOpenedStale = true;
                    } else if (q.length === 0 && this.autoOpenedStale) {
                        stale.open = false;
                        this.autoOpenedStale = false;
                    }
                }
                var emptyNote = root.querySelector('.ws-filter-empty');
                if (emptyNote) emptyNote.hidden = q.length === 0 || anyVisible;
            },
            onInput: function (event) {
                this.query = event.target.value;
                this.applyFilter();
            },
        };
    });
});
