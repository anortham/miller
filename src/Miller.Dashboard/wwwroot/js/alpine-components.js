// Miller dashboard — Alpine CSP component registrations.
// Factories run as plain JS; attribute expressions stay in the CSP-safe subset
// (property access, `foo($event)` calls only — no inline logic).

document.addEventListener('alpine:init', function () {
    Alpine.data('workspaceIndexFilter', function () {
        return {
            query: '',
            autoOpenedStale: false,
            sortColumn: null, // 'workspace' | 'files' | 'symbols' | 'rev'
            sortDir: 'asc',   // 'asc' | 'desc'

            // The #workspace-index section is swapped wholesale by a 30s htmx poll, which
            // destroys this component's DOM and reactive state. State that must survive a
            // swap lives in a module-level store owned by dashboard-site.js; init() rehydrates
            // from it and every mutation writes back, so the poll never clears the user's
            // filter text, sort choice, or a manually-opened stale section.
            store: function () {
                return window.__millerWorkspaceIndexState ||
                    (window.__millerWorkspaceIndexState = {
                        query: '', autoOpenedStale: false,
                        sortColumn: null, sortDir: 'asc', staleOpen: false,
                    });
            },
            persist: function () {
                var s = this.store();
                s.query = this.query;
                s.autoOpenedStale = this.autoOpenedStale;
                s.sortColumn = this.sortColumn;
                s.sortDir = this.sortDir;
                var stale = this.$el.querySelector('.ws-stale-collapse');
                s.staleOpen = stale ? stale.open : false;
            },
            init: function () {
                var s = this.store();
                this.query = s.query || '';
                this.autoOpenedStale = !!s.autoOpenedStale;
                this.sortColumn = s.sortColumn || null;
                this.sortDir = s.sortDir || 'asc';

                // Restore an open stale section (manual or auto) that a prior swap dropped.
                var stale = this.$el.querySelector('.ws-stale-collapse');
                if (stale && s.staleOpen) {
                    stale.open = true;
                }
                // Track manual open/close so it survives the next swap.
                if (stale && stale.getAttribute('data-stale-bound') !== '1') {
                    stale.setAttribute('data-stale-bound', '1');
                    var self = this;
                    stale.addEventListener('toggle', function () { self.persist(); });
                }

                this.applySort();
                this.reflectSortButtons();
                this.applyFilter();
            },

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
                this.persist();
            },
            onInput: function (event) {
                this.query = event.target.value;
                this.applyFilter();
            },

            onSort: function (event) {
                var col = event.currentTarget.getAttribute('data-sort-col');
                if (!col) return;
                if (this.sortColumn === col) {
                    this.sortDir = this.sortDir === 'asc' ? 'desc' : 'asc';
                } else {
                    this.sortColumn = col;
                    // Numeric columns default to descending (biggest first); name to ascending.
                    this.sortDir = col === 'workspace' ? 'asc' : 'desc';
                }
                this.applySort();
                this.reflectSortButtons();
                this.persist();
            },
            applySort: function () {
                if (!this.sortColumn) return;
                var col = this.sortColumn;
                var dir = this.sortDir === 'desc' ? -1 : 1;
                // Sort each grid (live + stale) independently; the header row is not a .ws-index-row
                // so re-appending the rows leaves the header in place.
                this.$el.querySelectorAll('.ws-index').forEach(function (grid) {
                    var rows = Array.prototype.slice.call(grid.querySelectorAll('.ws-index-row'));
                    rows.sort(function (a, b) {
                        if (col === 'workspace') {
                            var an = (a.getAttribute('data-sort-name') || '').toLowerCase();
                            var bn = (b.getAttribute('data-sort-name') || '').toLowerCase();
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
            },
            reflectSortButtons: function () {
                var self = this;
                this.$el.querySelectorAll('[data-sort-col]').forEach(function (btn) {
                    var col = btn.getAttribute('data-sort-col');
                    if (self.sortColumn === col) {
                        btn.setAttribute('aria-sort', self.sortDir === 'desc' ? 'descending' : 'ascending');
                    } else {
                        btn.setAttribute('aria-sort', 'none');
                    }
                });
            },
        };
    });
});
