// wwwroot/js/workspace.js
//
// CAD workspace:
//
// 1. CAD Geometry = actual DWG/DXF polygon geometry.
// 2. Placement Grid = logical placement helper.
// 3. LaghuReferenceNumber is NOT used for geometry or alignment.
// 4. PointLabel is NOT used for geometry or alignment.
// 5. Server-side CadastralMergeService decides actual XY merging.
//
// The CAD view loads the project's existing SvgMosaicExportService
// through Sheets/SvgPreview.

(function () {

    'use strict';


    const config =
        window.workspaceConfig || {};

    const sheets =
        config.sheets || [];


    let selectedSheetId = null;

    let draggedSheetId = null;

    let magnetCell = null;


    // =========================================================
    // ANTI FORGERY
    // =========================================================

    function antiForgeryToken() {

        const input =
            document.querySelector(
                '#form-remove input[name="__RequestVerificationToken"]'
            );

        return input
            ? input.value
            : '';

    }


    // =========================================================
    // VIEW SWITCHING
    // =========================================================

    window.switchView = function (mode) {

        const cadView =
            document.getElementById(
                'cad-view'
            );

        const gridView =
            document.getElementById(
                'grid-view'
            );

        const cadButton =
            document.getElementById(
                'btn-cad'
            );

        const gridButton =
            document.getElementById(
                'btn-grid'
            );


        if (mode === 'grid') {

            cadView.style.display =
                'none';

            gridView.style.display =
                'grid';

            cadButton.classList.remove(
                'active'
            );

            gridButton.classList.add(
                'active'
            );

            return;
        }


        gridView.style.display =
            'none';

        cadView.style.display =
            'flex';

        gridButton.classList.remove(
            'active'
        );

        cadButton.classList.add(
            'active'
        );


        loadCadGeometry();

    };


    // =========================================================
    // SHEET SELECTION
    // =========================================================

    window.selectSheet = function (sheetId) {

        selectedSheetId =
            sheetId;


        const sheet =
            sheets.find(
                x => x.SheetID === sheetId
            );


        if (!sheet) {
            return;
        }


        const detailBody =
            document.getElementById(
                'detail-body'
            );


        detailBody.innerHTML = `

            <div class="detail-row">
                <label>Sheet Number</label>
                <span>
                    ${escapeHtml(sheet.SheetNumber)}
                </span>
            </div>

            <div class="detail-row">
                <label>Status</label>
                <span>
                    ${escapeHtml(sheet.Status)}
                </span>
            </div>

            <div class="detail-row">
                <label>Grid Position</label>
                <span>
                    ${sheet.GridRow !== null
                ? '(' +
                sheet.GridRow +
                ', ' +
                sheet.GridCol +
                ')'
                : 'Unplaced'
            }
                </span>
            </div>

            <div class="detail-row">
                <label>Geometry</label>
                <span>
                    Actual DWG/DXF boundary
                </span>
            </div>

            <div class="detail-row">
                <label>Merge Method</label>
                <span>
                    Pure XY geometry
                </span>
            </div>

        `;


        document.getElementById(
            'parse-sheet-id'
        ).value = sheetId;


        document.getElementById(
            'btn-georeference'
        ).href =
            config.urls.georeference +
            '?sheetId=' +
            encodeURIComponent(sheetId);


        document.getElementById(
            'detail-actions'
        ).style.display =
            'flex';


        highlightCadSheet(
            sheetId
        );

    };


    // =========================================================
    // HTML ESCAPE
    // =========================================================

    function escapeHtml(value) {

        if (value === null ||
            value === undefined) {

            return '';

        }


        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');

    }


    // =========================================================
    // CAD GEOMETRY VIEW
    // =========================================================

    let cadZoom = 1.0;


    let cadGeometryLoaded =
        false;


    function loadCadGeometry() {

        const content =
            document.getElementById(
                'cad-geometry-content'
            );


        const loading =
            document.getElementById(
                'cad-geometry-loading'
            );


        const empty =
            document.getElementById(
                'cad-geometry-empty'
            );


        if (!content) {
            return;
        }


        if (cadGeometryLoaded) {

            loading.style.display =
                'none';

            return;

        }


        loading.style.display =
            'flex';

        empty.style.display =
            'none';

        content.innerHTML =
            '';


        fetch(
            config.urls.svgPreview,
            {
                method: 'GET',
                headers: {
                    'X-Requested-With':
                        'XMLHttpRequest'
                }
            }
        )
            .then(response => {

                if (!response.ok) {

                    throw new Error(
                        'SVG preview failed: ' +
                        response.status
                    );

                }

                return response.text();

            })
            .then(svgText => {

                loading.style.display =
                    'none';


                if (
                    !svgText ||
                    !svgText.includes('<svg')
                ) {

                    empty.style.display =
                        'flex';

                    return;

                }


                content.innerHTML =
                    svgText;


                const svg =
                    content.querySelector(
                        'svg'
                    );


                if (!svg) {

                    empty.style.display =
                        'flex';

                    return;

                }


                cadGeometryLoaded =
                    true;


                prepareCadSvg(
                    svg
                );


                cadFitToGeometry();

            })
            .catch(error => {

                console.error(
                    'CAD geometry loading failed:',
                    error
                );


                loading.style.display =
                    'none';


                empty.style.display =
                    'flex';


                empty.textContent =
                    'Unable to load CAD geometry.';

            });

    }


    // =========================================================
    // PREPARE SVG
    // =========================================================

    function prepareCadSvg(svg) {

        svg.classList.add(
            'cad-real-svg'
        );


        svg.setAttribute(
            'preserveAspectRatio',
            'xMidYMid meet'
        );


        svg.style.width =
            '100%';

        svg.style.height =
            '100%';


        const polygons =
            svg.querySelectorAll(
                'polygon'
            );


        polygons.forEach(
            polygon => {

                polygon.classList.add(
                    'cad-boundary'
                );


                polygon.addEventListener(
                    'mouseenter',
                    function () {

                        this.classList.add(
                            'cad-boundary-hover'
                        );

                    }
                );


                polygon.addEventListener(
                    'mouseleave',
                    function () {

                        this.classList.remove(
                            'cad-boundary-hover'
                        );

                    }
                );

            }
        );


        /*
         * The SVG service emits polygons and text.
         *
         * We intentionally do not generate any
         * rectangle around the polygon.
         */

    }


    // =========================================================
    // CAD SHEET HIGHLIGHT
    // =========================================================

    function highlightCadSheet(
        sheetId
    ) {

        const svg =
            document.querySelector(
                '#cad-geometry-content svg'
            );


        if (!svg) {
            return;
        }


        svg.querySelectorAll(
            '.cad-selected-sheet'
        )
            .forEach(
                element => {

                    element.classList.remove(
                        'cad-selected-sheet'
                    );

                }
            );


        /*
         * The current SvgMosaicExportService does not yet
         * emit SheetID on each polygon.
         *
         * Therefore we do not attempt to guess which
         * polygon belongs to which sheet.
         *
         * The geometry itself remains authoritative.
         */

    }


    // =========================================================
    // CAD ZOOM
    // =========================================================

    function applyCadZoom() {

        const svg =
            document.querySelector(
                '#cad-geometry-content svg'
            );


        if (!svg) {
            return;
        }


        svg.style.transform =
            `scale(${cadZoom})`;


        svg.style.transformOrigin =
            'center center';

    }


    window.cadZoomIn =
        function () {

            cadZoom =
                Math.min(
                    cadZoom + 0.15,
                    5.0
                );


            applyCadZoom();

        };


    window.cadZoomOut =
        function () {

            cadZoom =
                Math.max(
                    cadZoom - 0.15,
                    0.25
                );


            applyCadZoom();

        };


    window.cadZoomReset =
        function () {

            cadZoom =
                1.0;


            applyCadZoom();

        };


    window.cadFitToGeometry =
        function () {

            cadZoom =
                1.0;


            const svg =
                document.querySelector(
                    '#cad-geometry-content svg'
                );


            if (!svg) {
                return;
            }


            svg.style.transform =
                'scale(1)';

        };


    // =========================================================
    // STITCH ALL
    // =========================================================

    window.stitchAll =
        function () {

            const form =
                document.getElementById(
                    'form-stitch-all'
                );


            if (form) {
                form.submit();
            }

        };


    // =========================================================
    // MERGE SHEETS (jigsaw full-entity DXF export)
    // =========================================================

    window.mergeSheets =
        function () {

            const columnsPerRow =
                window.prompt(
                    'Columns per row in the sheet index grid:',
                    '5'
                );


            if (columnsPerRow === null) {
                return;
            }


            const parsed =
                parseInt(
                    columnsPerRow,
                    10
                );


            if (!Number.isInteger(parsed) || parsed <= 0) {
                window.alert(
                    'Enter a whole number greater than zero.'
                );
                return;
            }


            const input =
                document.getElementById(
                    'merge-sheets-columns-per-row'
                );

            const form =
                document.getElementById(
                    'form-merge-sheets'
                );


            if (input && form) {
                input.value = parsed;
                form.submit();
            }

        };


    // =========================================================
    // REMOVE SHEET
    // =========================================================

    window.removeSheet =
        function (
            sheetId,
            sheetNumber
        ) {

            if (
                !confirm(
                    `Remove Sheet ${sheetNumber}? ` +
                    `This deletes its file and all tie points. ` +
                    `This cannot be undone.`
                )
            ) {

                return;

            }


            document.getElementById(
                'remove-sheet-id'
            ).value =
                sheetId;


            document.getElementById(
                'form-remove'
            ).submit();

        };


    window.removeSelectedSheet =
        function () {

            if (!selectedSheetId) {
                return;
            }


            const sheet =
                sheets.find(
                    x =>
                        x.SheetID ===
                        selectedSheetId
                );


            removeSheet(
                selectedSheetId,
                sheet
                    ? sheet.SheetNumber
                    : selectedSheetId
            );

        };


    // =========================================================
    // OPEN TARGET SLOTS
    //
    // These are placement slots only.
    //
    // NO LaghuReferenceNumber.
    // NO PointLabel.
    // NO geometry decision.
    //
    // The server performs actual XY merge.
    // =========================================================

    function clearConnectTargets() {

        document
            .querySelectorAll(
                '.grid-cell.connect-target'
            )
            .forEach(
                cell => {

                    cell.classList.remove(
                        'connect-target'
                    );


                    const label =
                        cell.querySelector(
                            '.connect-label'
                        );


                    if (label) {
                        label.remove();
                    }


                    const row =
                        cell.dataset.row;

                    const col =
                        cell.dataset.col;


                    if (
                        row !== undefined &&
                        col !== undefined
                    ) {

                        cell.onclick =
                            () =>
                                promptManualPlace(
                                    parseInt(
                                        row,
                                        10
                                    ),
                                    parseInt(
                                        col,
                                        10
                                    )
                                );

                    }

                }
            );

    }


    function highlightConnectTargets(
        sheetId
    ) {

        clearConnectTargets();


        if (!config.urls.getOpenSlots) {
            return;
        }


        fetch(
            config.urls.getOpenSlots +
            '?sheetId=' +
            encodeURIComponent(sheetId)
        )
            .then(
                response => {

                    if (!response.ok) {
                        throw new Error(
                            'Unable to get open slots.'
                        );
                    }

                    return response.json();

                }
            )
            .then(
                slots => {

                    if (!Array.isArray(slots)) {
                        return;
                    }


                    slots.forEach(
                        slot => {

                            const cell =
                                document.getElementById(
                                    `cell-${slot.gridRow}-${slot.gridCol}`
                                );


                            if (
                                !cell ||
                                !cell.classList.contains(
                                    'empty'
                                )
                            ) {

                                return;

                            }


                            cell.classList.add(
                                'connect-target'
                            );


                            const label =
                                document.createElement(
                                    'div'
                                );


                            label.className =
                                'connect-label';


                            label.textContent =
                                slot.label ||
                                'Connect Here';


                            cell.appendChild(
                                label
                            );


                            cell.onclick =
                                () =>
                                    connectSheetHere(
                                        sheetId,
                                        slot.gridRow,
                                        slot.gridCol
                                    );

                        }
                    );

                }
            )
            .catch(
                error => {

                    console.error(
                        error
                    );

                }
            );

    }


    // =========================================================
    // PLACE SHEET
    // =========================================================

    function connectSheetHere(
        sheetId,
        gridRow,
        gridCol
    ) {

        const form =
            new FormData();


        form.append(
            'sheetId',
            String(sheetId)
        );


        form.append(
            'gridRow',
            String(gridRow)
        );


        form.append(
            'gridCol',
            String(gridCol)
        );


        const token =
            antiForgeryToken();


        if (token) {

            form.append(
                '__RequestVerificationToken',
                token
            );

        }


        fetch(
            config.urls.placeSheet,
            {
                method: 'POST',
                body: form,
                headers: {
                    'X-Requested-With':
                        'XMLHttpRequest'
                }
            }
        )
            .then(
                response => {

                    if (!response.ok) {
                        throw new Error(
                            'Placement failed.'
                        );
                    }

                    return response.json();

                }
            )
            .then(
                data => {

                    showMergeToast(
                        data
                    );


                    setTimeout(
                        () =>
                            location.reload(),
                        1400
                    );

                }
            )
            .catch(
                error => {

                    console.error(
                        error
                    );


                    showPlacementError(
                        'Sheet placement failed.'
                    );

                }
            );

    }


    // =========================================================
    // GRID DRAG
    // =========================================================

    const SNAP_RADIUS =
        70;


    window.dragStart =
        function (
            event,
            sheetId
        ) {

            draggedSheetId =
                sheetId;


            event.dataTransfer.effectAllowed =
                'move';


            event.dataTransfer.setData(
                'text/plain',
                String(sheetId)
            );

        };


    function clearSnapHighlights() {

        document
            .querySelectorAll(
                '.grid-cell.snap-hint'
            )
            .forEach(
                cell =>
                    cell.classList.remove(
                        'snap-hint'
                    )
            );


        document
            .querySelectorAll(
                '.grid-cell.filled.align-glow'
            )
            .forEach(
                cell =>
                    cell.classList.remove(
                        'align-glow'
                    )
            );

    }


    const gridView =
        document.getElementById(
            'grid-view'
        );


    if (gridView) {

        gridView.addEventListener(
            'dragover',
            function (event) {

                event.preventDefault();


                if (!draggedSheetId) {
                    return;
                }


                let nearest = null;

                let nearestDistance =
                    Infinity;


                document
                    .querySelectorAll(
                        '.grid-cell.empty'
                    )
                    .forEach(
                        cell => {

                            const rect =
                                cell.getBoundingClientRect();


                            const centerX =
                                rect.left +
                                rect.width / 2;


                            const centerY =
                                rect.top +
                                rect.height / 2;


                            const distance =
                                Math.hypot(
                                    event.clientX -
                                    centerX,
                                    event.clientY -
                                    centerY
                                );


                            if (
                                distance <
                                nearestDistance
                            ) {

                                nearestDistance =
                                    distance;

                                nearest =
                                    cell;

                            }

                        }
                    );


                clearSnapHighlights();

                magnetCell = null;


                if (
                    nearest &&
                    nearestDistance <=
                    SNAP_RADIUS
                ) {

                    const row =
                        parseInt(
                            nearest.dataset.row,
                            10
                        );


                    const col =
                        parseInt(
                            nearest.dataset.col,
                            10
                        );


                    /*
                     * IMPORTANT:
                     *
                     * There is deliberately no:
                     *
                     *     LaghuRef == LaghuRef
                     *
                     * check here.
                     *
                     * This is only a placement position.
                     */

                    nearest.classList.add(
                        'snap-hint'
                    );


                    magnetCell = {
                        row,
                        col
                    };

                }

            }
        );


        gridView.addEventListener(
            'dragleave',
            function (event) {

                if (
                    !gridView.contains(
                        event.relatedTarget
                    )
                ) {

                    clearSnapHighlights();

                    magnetCell = null;

                }

            }
        );


        gridView.addEventListener(
            'drop',
            function (event) {

                event.preventDefault();


                clearSnapHighlights();


                if (
                    !draggedSheetId ||
                    !magnetCell
                ) {

                    draggedSheetId =
                        null;

                    magnetCell =
                        null;

                    return;

                }


                const row =
                    magnetCell.row;

                const col =
                    magnetCell.col;


                const form =
                    new FormData();


                form.append(
                    'sheetId',
                    String(
                        draggedSheetId
                    )
                );


                form.append(
                    'gridRow',
                    String(row)
                );


                form.append(
                    'gridCol',
                    String(col)
                );


                const token =
                    antiForgeryToken();


                if (token) {

                    form.append(
                        '__RequestVerificationToken',
                        token
                    );

                }


                fetch(
                    config.urls.placeSheet,
                    {
                        method: 'POST',
                        body: form,
                        headers: {
                            'X-Requested-With':
                                'XMLHttpRequest'
                        }
                    }
                )
                    .then(
                        response => {

                            if (!response.ok) {
                                throw new Error(
                                    'Placement failed.'
                                );
                            }

                            return response.json();

                        }
                    )
                    .then(
                        data => {

                            showMergeToast(
                                data
                            );


                            setTimeout(
                                () =>
                                    location.reload(),
                                1400
                            );

                        }
                    )
                    .catch(
                        error => {

                            console.error(
                                error
                            );


                            showPlacementError(
                                'Sheet placement failed.'
                            );

                        }
                    );


                draggedSheetId =
                    null;

                magnetCell =
                    null;

            }
        );

    }


    // =========================================================
    // MERGE / PLACEMENT TOAST
    // =========================================================

    function showMergeToast(
        data
    ) {

        let toast =
            document.getElementById(
                'workspace-toast'
            );


        if (!toast) {

            toast =
                document.createElement(
                    'div'
                );


            toast.id =
                'workspace-toast';


            toast.className =
                'toast-feedback';


            document.body.appendChild(
                toast
            );

        }


        if (
            !data ||
            !data.success
        ) {

            toast.textContent =
                data &&
                    data.placementMessage
                    ? data.placementMessage
                    : 'Placement failed.';


            toast.className =
                'toast-feedback neutral show';

        }

        else if (
            data.merges &&
            data.merges.length > 0
        ) {

            const merged =
                data.merges.find(
                    merge =>
                        merge.success
                );


            if (merged) {

                const rms =
                    Number(
                        merged.rmsError || 0
                    );


                toast.textContent =
                    `Merged with Sheet ` +
                    `${merged.neighborSheetNumber} ` +
                    `— RMS ${rms.toFixed(3)}`;


                toast.className =
                    'toast-feedback success show';

            }

            else {

                const attempt =
                    data.merges[0];


                toast.textContent =
                    `Placed next to Sheet ` +
                    `${attempt.neighborSheetNumber} ` +
                    `— merge skipped: ` +
                    `${attempt.message}`;


                toast.className =
                    'toast-feedback neutral show';

            }

        }

        else {

            toast.textContent =
                'Placed — no adjacent sheet to merge with';


            toast.className =
                'toast-feedback neutral show';

        }


        setTimeout(
            () =>
                toast.classList.remove(
                    'show'
                ),
            1600
        );

    }


    function showPlacementError(
        message
    ) {

        let toast =
            document.getElementById(
                'workspace-toast'
            );


        if (!toast) {

            toast =
                document.createElement(
                    'div'
                );


            toast.id =
                'workspace-toast';


            toast.className =
                'toast-feedback';


            document.body.appendChild(
                toast
            );

        }


        toast.textContent =
            message;


        toast.className =
            'toast-feedback neutral show';


        setTimeout(
            () =>
                toast.classList.remove(
                    'show'
                ),
            1800
        );

    }


    // =========================================================
    // MANUAL PLACEMENT
    // =========================================================

    window.promptManualPlace =
        function (
            row,
            col
        ) {

            if (
                config.unplacedCount === 0
            ) {

                return;

            }


            document.getElementById(
                'target-cell-label'
            ).textContent =
                `(${row}, ${col})`;


            document.getElementById(
                'manual-row'
            ).value =
                row;


            document.getElementById(
                'manual-col'
            ).value =
                col;


            const modalElement =
                document.getElementById(
                    'manualPlaceModal'
                );


            bootstrap.Modal
                .getOrCreateInstance(
                    modalElement
                )
                .show();

        };


    // =========================================================
    // INITIALIZATION
    // =========================================================

    document.addEventListener(
        'DOMContentLoaded',
        function () {

            /*
             * CAD geometry is the default view.
             */

            switchView(
                'cad'
            );


            /*
             * Load placement targets only when
             * a sheet is selected.
             */

        }
    );

})();