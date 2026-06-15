// Miller dashboard — Alpine CSP component registrations.
// Factories run as plain JS; attribute expressions stay in the CSP-safe subset.

document.addEventListener('alpine:init', function () {
    Alpine.data('workspaceIndexFilter', function () {
        return {
            query: '',
            applyFilter: function () {
                var root = this.$el;
                var q = (this.query || '').trim().toLowerCase();
                root.querySelectorAll('.ws-index-row').forEach(function (row) {
                    var text = row.textContent.toLowerCase();
                    row.hidden = q.length > 0 && text.indexOf(q) < 0;
                });
            },
            onInput: function (event) {
                this.query = event.target.value;
                this.applyFilter();
            },
        };
    });
});
