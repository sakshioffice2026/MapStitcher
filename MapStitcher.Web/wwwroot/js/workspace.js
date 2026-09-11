document.addEventListener('DOMContentLoaded', () => {
    const workspace = document.getElementById('workspace');
    const cards = document.querySelectorAll('.sheet-card');
    const sidebarItems = document.querySelectorAll('.sidebar-item');
    const detailContent = document.getElementById('detailContent');
    const SNAP_DISTANCE = 60;

    let activeCard = null;
    let offsetX = 0;
    let offsetY = 0;
    let lastSnappedPair = null;

    cards.forEach(card => {
        card.addEventListener('mousedown', (e) => {
            activeCard = card;
            const rect = card.getBoundingClientRect();
            offsetX = e.clientX - rect.left;
            offsetY = e.clientY - rect.top;
            card.style.zIndex = 100;
            selectCard(card);
        });

        card.addEventListener('click', () => selectCard(card));
    });

    document.addEventListener('mousemove', (e) => {
        if (!activeCard) return;

        const workspaceRect = workspace.getBoundingClientRect();
        const newLeft = e.clientX - workspaceRect.left - offsetX;
        const newTop = e.clientY - workspaceRect.top - offsetY;

        activeCard.style.left = newLeft + 'px';
        activeCard.style.top = newTop + 'px';

        checkSnapProximity(activeCard, cards);
    });

    document.addEventListener('mouseup', () => {
        activeCard = null;
    });

    sidebarItems.forEach(item => {
        item.addEventListener('click', () => {
            const sheetId = item.dataset.sheetId;
            const matchingCard = document.querySelector(`.sheet-card[data-sheet-id="${sheetId}"]`);
            if (matchingCard) {
                selectCard(matchingCard);
            }
        });
    });

    function selectCard(card) {
        cards.forEach(c => c.classList.remove('selected'));
        sidebarItems.forEach(i => i.classList.remove('active'));

        card.classList.add('selected');

        const sheetId = card.dataset.sheetId;
        const sidebarMatch = document.querySelector(`.sidebar-item[data-sheet-id="${sheetId}"]`);
        if (sidebarMatch) {
            sidebarMatch.classList.add('active');
        }

        updateDetailPanel(card);
    }

    function updateDetailPanel(card) {
        const sheetId = card.dataset.sheetId;
        const laghu = card.dataset.laghu || '—';
        const statusText = card.querySelector('small:last-child')?.textContent || '';
        const sheetTitle = card.querySelector('h6')?.textContent || '';

        let mergeButtonHtml = '';
        if (lastSnappedPair && lastSnappedPair.includes(sheetId)) {
            const otherId = lastSnappedPair.find(id => id !== sheetId);
            mergeButtonHtml = `
                <button class="btn btn-sm btn-success w-100 mt-2" onclick="triggerMerge('${sheetId}', '${otherId}')">
                    Merge with Sheet ${otherId}
                </button>
            `;
        }

        detailContent.innerHTML = `
            <div class="detail-row">
                <span class="label">Sheet</span>
                ${sheetTitle}
            </div>
            <div class="detail-row">
                <span class="label">Sheet ID</span>
                ${sheetId}
            </div>
            <div class="detail-row">
                <span class="label">Laghu Reference</span>
                ${laghu}
            </div>
            <div class="detail-row">
                <span class="label">${statusText}</span>
            </div>
            <a class="btn btn-sm btn-outline-primary w-100 mt-2" href="/Sheets/Georeference?sheetId=${sheetId}">
                Georeference Control Points
            </a>
            ${mergeButtonHtml}
        `;
    }

    function checkSnapProximity(card, allCards) {
        const cardLaghu = card.dataset.laghu;
        if (!cardLaghu) {
            card.classList.remove('snapped');
            lastSnappedPair = null;
            return;
        }

        const cardRect = card.getBoundingClientRect();
        let snapped = false;

        allCards.forEach(other => {
            if (other === card) return;

            const otherLaghu = other.dataset.laghu;
            const otherSheetId = other.dataset.sheetId;
            if (!otherLaghu || cardLaghu !== otherLaghu) return;

            const otherRect = other.getBoundingClientRect();
            const dx = cardRect.left - otherRect.left;
            const dy = cardRect.top - otherRect.top;
            const distance = Math.sqrt(dx * dx + dy * dy);

            if (distance < SNAP_DISTANCE) {
                snapped = true;
                other.classList.add('snapped');
                lastSnappedPair = [card.dataset.sheetId, otherSheetId];
            } else {
                other.classList.remove('snapped');
            }
        });

        card.classList.toggle('snapped', snapped);
        if (!snapped) {
            lastSnappedPair = null;
        } else {
            updateDetailPanel(card);
        }
    }

    window.triggerMerge = function (baseSheetId, adjacentSheetId) {
        const form = document.createElement('form');
        form.method = 'POST';
        form.action = '/Sheets/Merge';

        const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');

        const baseInput = document.createElement('input');
        baseInput.type = 'hidden';
        baseInput.name = 'baseSheetId';
        baseInput.value = baseSheetId;

        const adjacentInput = document.createElement('input');
        adjacentInput.type = 'hidden';
        adjacentInput.name = 'adjacentSheetId';
        adjacentInput.value = adjacentSheetId;

        form.appendChild(baseInput);
        form.appendChild(adjacentInput);

        if (tokenInput) {
            form.appendChild(tokenInput.cloneNode());
        }

        document.body.appendChild(form);
        form.submit();
    };
});