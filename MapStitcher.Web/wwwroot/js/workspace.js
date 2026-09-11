// wwwroot/js/workspace.js — Jigsaw Board workspace logic
// Expects window.workspaceConfig to be set by the view before this script loads:
// { sheets, unplacedCount, urls: { georeference, getOpenSlots, placeSheet } }

(function () {
    const config = window.workspaceConfig;
    const sheets = config.sheets;

    let selectedSheetId = null;
    let draggedSheetId = null;

    function antiForgeryToken() {
        return document.querySelector('#form-remove input[name="__RequestVerificationToken"]').value;
    }

    window.switchView = function (mode) {
        document.getElementById('grid-view').style.display = mode === 'grid' ? 'grid' : 'none';
        document.getElementById('canvas-view').style.display = mode === 'canvas' ? 'block' : 'none';
        document.getElementById('btn-grid').classList.toggle('active', mode === 'grid');
        document.getElementById('btn-canvas').classList.toggle('active', mode === 'canvas');
    };

    // ── Sheet Selection → Detail Panel + Connect Targets ──
    window.selectSheet = function (sheetId) {
        selectedSheetId = sheetId;
        const s = sheets.find(x => x.SheetID === sheetId);
        if (!s) return;

        document.getElementById('detail-body').innerHTML = `
            <div class="detail-row"><label>Sheet Number</label><span>${s.SheetNumber}</span></div>
            <div class="detail-row"><label>Laghu Ref</label><span>${s.LaghuRef}</span></div>
            <div class="detail-row"><label>Status</label><span>${s.Status}</span></div>
            <div class="detail-row"><label>Grid Position</label>
                <span>${s.GridRow !== null ? '(' + s.GridRow + ', ' + s.GridCol + ')' : 'Unplaced'}</span>
            </div>
        `;

        document.getElementById('parse-sheet-id').value = sheetId;
        document.getElementById('btn-georeference').href = config.urls.georeference + '?sheetId=' + sheetId;
        document.getElementById('detail-actions').style.display = 'flex';

        highlightConnectTargets(sheetId);
    };

    // Highlights valid empty neighbor cells for the selected sheet as
    // "Connect Here" — no directional (N/S/E/W) language shown to the user.
    function clearConnectTargets() {
        document.querySelectorAll('.grid-cell.connect-target').forEach(cell => {
            cell.classList.remove('connect-target');
            const lbl = cell.querySelector('.connect-label');
            if (lbl) lbl.remove();
            cell.onclick = null;
            if (cell.dataset.row !== undefined) {
                const r = cell.dataset.row, c = cell.dataset.col;
                cell.onclick = () => promptManualPlace(parseInt(r, 10), parseInt(c, 10));
            }
        });
    }

    function highlightConnectTargets(sheetId) {
        clearConnectTargets();

        fetch(config.urls.getOpenSlots + '?sheetId=' + sheetId)
            .then(r => r.json())
            .then(slots => {
                slots.forEach(slot => {
                    const cell = document.getElementById(`cell-${slot.gridRow}-${slot.gridCol}`);
                    if (!cell || !cell.classList.contains('empty')) return;

                    cell.classList.add('connect-target');
                    const label = document.createElement('div');
                    label.className = 'connect-label';
                    label.textContent = slot.label || 'Connect Here';
                    cell.appendChild(label);

                    cell.onclick = () => connectSheetHere(sheetId, slot.gridRow, slot.gridCol);
                });
            });
    }

    function connectSheetHere(sheetId, gridRow, gridCol) {
        const form = new FormData();
        form.append('sheetId', sheetId);
        form.append('gridRow', gridRow);
        form.append('gridCol', gridCol);
        form.append('__RequestVerificationToken', antiForgeryToken());

        fetch(config.urls.placeSheet, {
            method: 'POST', body: form,
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(r => r.json())
            .then(data => {
                showMergeToast(data);
                setTimeout(() => location.reload(), 1400);
            });
    }

    // ── Stitch Available Sheets (bulk auto place + auto merge, gaps left as-is) ──
    window.stitchAll = function () {
        document.getElementById('form-stitch-all').submit();
    };

    // ── Remove Sheet ──
    window.removeSheet = function (sheetId, sheetNumber) {
        if (!confirm(`Remove Sheet ${sheetNumber}? This deletes its file and all tie points. This cannot be undone.`)) {
            return;
        }
        document.getElementById('remove-sheet-id').value = sheetId;
        document.getElementById('form-remove').submit();
    };

    window.removeSelectedSheet = function () {
        if (!selectedSheetId) return;
        const s = sheets.find(x => x.SheetID === selectedSheetId);
        removeSheet(selectedSheetId, s ? s.SheetNumber : selectedSheetId);
    };

    // ── Drag and Drop (magnetic snap) ──
    const SNAP_RADIUS = 70; // px — how far the pointer can be from a cell center and still magnetize to it
    let draggedLaghu = null;
    let magnetCell = null; // currently highlighted {row, col} element

    window.dragStart = function (event, sheetId) {
        draggedSheetId = sheetId;
        const s = sheets.find(x => x.SheetID === sheetId);
        draggedLaghu = s ? s.LaghuRef : null;
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', String(sheetId)); // required by Firefox to start a drag
    };

    function getAlignedNeighbor(row, col) {
        if (!draggedLaghu || draggedLaghu === '—') return null;
        const offsets = [[-1, 0], [1, 0], [0, -1], [0, 1]];
        for (const [dr, dc] of offsets) {
            const n = sheets.find(x => x.GridRow === row + dr && x.GridCol === col + dc);
            if (n && n.LaghuRef === draggedLaghu) return n;
        }
        return null;
    }

    function clearSnapHighlights() {
        document.querySelectorAll('.grid-cell.snap-hint, .grid-cell.snap-align')
            .forEach(c => c.classList.remove('snap-hint', 'snap-align'));
        document.querySelectorAll('.grid-cell.filled.align-glow')
            .forEach(c => c.classList.remove('align-glow'));
    }

    document.getElementById('grid-view').addEventListener('dragover', (event) => {
        event.preventDefault();
        if (!draggedSheetId) return;

        let nearest = null, nearestDist = Infinity;
        document.querySelectorAll('.grid-cell.empty').forEach(cell => {
            const r = cell.getBoundingClientRect();
            const cx = r.left + r.width / 2;
            const cy = r.top + r.height / 2;
            const dist = Math.hypot(event.clientX - cx, event.clientY - cy);
            if (dist < nearestDist) { nearestDist = dist; nearest = cell; }
        });

        clearSnapHighlights();
        magnetCell = null;

        if (nearest && nearestDist <= SNAP_RADIUS) {
            const row = parseInt(nearest.dataset.row, 10);
            const col = parseInt(nearest.dataset.col, 10);
            const alignedNeighbor = getAlignedNeighbor(row, col);

            nearest.classList.add(alignedNeighbor ? 'snap-align' : 'snap-hint');
            if (alignedNeighbor) {
                const neighborCell = document.getElementById(`cell-${alignedNeighbor.GridRow}-${alignedNeighbor.GridCol}`);
                if (neighborCell) neighborCell.classList.add('align-glow');
            }
            magnetCell = { row, col, alignedNeighbor };
        }
    });

    document.getElementById('grid-view').addEventListener('dragleave', (event) => {
        if (!document.getElementById('grid-view').contains(event.relatedTarget)) {
            clearSnapHighlights();
            magnetCell = null;
        }
    });

    document.getElementById('grid-view').addEventListener('drop', (event) => {
        event.preventDefault();
        clearSnapHighlights();

        if (!draggedSheetId || !magnetCell) { draggedSheetId = null; return; }

        const { row, col } = magnetCell;
        const form = new FormData();
        form.append('sheetId', draggedSheetId);
        form.append('gridRow', row);
        form.append('gridCol', col);
        form.append('__RequestVerificationToken', antiForgeryToken());

        fetch(config.urls.placeSheet, {
            method: 'POST', body: form,
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(r => r.json())
            .then(data => {
                showMergeToast(data);
                setTimeout(() => location.reload(), 1400);
            });

        draggedSheetId = null;
        magnetCell = null;
    });

    function showMergeToast(data) {
        let toast = document.getElementById('workspace-toast');
        if (!toast) {
            toast = document.createElement('div');
            toast.id = 'workspace-toast';
            toast.className = 'toast-feedback';
            document.body.appendChild(toast);
        }

        if (!data.success) {
            toast.textContent = data.placementMessage || 'Placement failed.';
            toast.className = 'toast-feedback neutral show';
        } else if (data.merges && data.merges.length > 0) {
            const merged = data.merges.find(m => m.success);
            if (merged) {
                toast.textContent = `Merged with Sheet ${merged.neighborSheetNumber} — RMS ${merged.rmsError.toFixed(3)}`;
                toast.className = 'toast-feedback success show';
            } else {
                const attempt = data.merges[0];
                toast.textContent = `Placed next to Sheet ${attempt.neighborSheetNumber} — merge skipped: ${attempt.message}`;
                toast.className = 'toast-feedback neutral show';
            }
        } else {
            toast.textContent = 'Placed — no adjacent sheet to merge with';
            toast.className = 'toast-feedback neutral show';
        }
        setTimeout(() => toast.classList.remove('show'), 1300);
    }

    window.promptManualPlace = function (row, col) {
        if (config.unplacedCount === 0) return;
        document.getElementById('target-cell-label').textContent = `(${row}, ${col})`;
        document.getElementById('manual-row').value = row;
        document.getElementById('manual-col').value = col;
        new bootstrap.Modal(document.getElementById('manualPlaceModal')).show();
    };

    // ── Canvas View (SVG) drag with magnetic snap ──
    const CELL_W = 190, CELL_H = 145, ORIGIN_X = 40, ORIGIN_Y = 40, SVG_SNAP_RADIUS = 45;
    const svg = document.getElementById('canvas-svg');
    const ghost = document.getElementById('ghost-slot');
    let svgDragging = null; // { sheetId, group, startX, startY, origRow, origCol }

    function toSvgPoint(clientX, clientY) {
        const pt = svg.createSVGPoint();
        pt.x = clientX; pt.y = clientY;
        return pt.matrixTransform(svg.getScreenCTM().inverse());
    }

    function slotOccupied(row, col, excludeSheetId) {
        return sheets.some(s => s.GridRow === row && s.GridCol === col && s.SheetID !== excludeSheetId);
    }

    window.svgDragStart = function (event, sheetId) {
        event.stopPropagation();
        const s = sheets.find(x => x.SheetID === sheetId);
        if (!s) return;
        const p = toSvgPoint(event.clientX, event.clientY);
        svgDragging = {
            sheetId, group: document.getElementById(`poly-${sheetId}`),
            laghu: s.LaghuRef, startX: p.x, startY: p.y,
            origRow: s.GridRow, origCol: s.GridCol
        };
        svgDragging.group.style.cursor = 'grabbing';
    };

    svg.addEventListener('mousemove', (event) => {
        if (!svgDragging) return;
        const p = toSvgPoint(event.clientX, event.clientY);
        const dx = p.x - svgDragging.startX, dy = p.y - svgDragging.startY;
        svgDragging.group.setAttribute('transform', `translate(${dx},${dy})`);

        const curX = ORIGIN_X + svgDragging.origCol * CELL_W + dx;
        const curY = ORIGIN_Y + svgDragging.origRow * CELL_H + dy;
        const targetCol = Math.round((curX - ORIGIN_X) / CELL_W);
        const targetRow = Math.round((curY - ORIGIN_Y) / CELL_H);
        const slotX = ORIGIN_X + targetCol * CELL_W;
        const slotY = ORIGIN_Y + targetRow * CELL_H;
        const dist = Math.hypot(curX - slotX, curY - slotY);

        document.querySelectorAll('.sheet-polygon.align-glow rect').forEach(r => r.classList.remove('align-glow'));

        if (dist <= SVG_SNAP_RADIUS && !slotOccupied(targetRow, targetCol, svgDragging.sheetId)) {
            const neighbor = getAlignedNeighborFor(svgDragging.laghu, targetRow, targetCol);
            ghost.setAttribute('x', slotX);
            ghost.setAttribute('y', slotY);
            ghost.setAttribute('stroke', neighbor ? '#22c55e' : '#38bdf8');
            ghost.setAttribute('fill', neighbor ? '#22c55e' : '#38bdf8');
            ghost.style.display = 'block';
            svgDragging.snapTarget = { row: targetRow, col: targetCol, neighbor };

            if (neighbor) {
                const nEl = document.getElementById(`poly-${neighbor.SheetID}`);
                if (nEl) nEl.querySelector('rect').classList.add('align-glow');
            }
        } else {
            ghost.style.display = 'none';
            svgDragging.snapTarget = null;
        }
    });

    svg.addEventListener('mouseup', () => {
        if (!svgDragging) return;
        ghost.style.display = 'none';
        document.querySelectorAll('.sheet-polygon rect.align-glow').forEach(r => r.classList.remove('align-glow'));
        svgDragging.group.style.cursor = 'grab';

        if (svgDragging.snapTarget) {
            const { row, col } = svgDragging.snapTarget;
            const form = new FormData();
            form.append('sheetId', svgDragging.sheetId);
            form.append('gridRow', row);
            form.append('gridCol', col);
            form.append('__RequestVerificationToken', antiForgeryToken());

            fetch(config.urls.placeSheet, {
                method: 'POST', body: form,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    showMergeToast(data);
                    setTimeout(() => location.reload(), 1400);
                });
        } else {
            svgDragging.group.removeAttribute('transform'); // snap back, no valid slot
        }
        svgDragging = null;
    });

    function getAlignedNeighborFor(laghu, row, col) {
        if (!laghu || laghu === '—') return null;
        const offsets = [[-1, 0], [1, 0], [0, -1], [0, 1]];
        for (const [dr, dc] of offsets) {
            const n = sheets.find(x => x.GridRow === row + dr && x.GridCol === col + dc);
            if (n && n.LaghuRef === laghu) return n;
        }
        return null;
    }
})();