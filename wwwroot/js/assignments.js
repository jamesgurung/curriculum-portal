const assignmentsRoot = document.getElementById('assignments-root');
const assignmentState = { detailStack: [], showPupilPremium: false };

document.addEventListener('DOMContentLoaded', () => {
  if (!assignmentsRoot) {
    return;
  }

  assignmentsRoot.addEventListener('click', onAssignmentsClick);
  assignmentsRoot.addEventListener('change', onAssignmentsChange);
  assignmentsRoot.addEventListener('keydown', onAssignmentsKeydown);
  renderAssignments();
});

function onAssignmentsClick(event) {
  const backButton = event.target.closest('[data-action="back-to-overview"]');
  if (backButton) {
    assignmentState.detailStack.pop();
    renderAssignments();
    return;
  }

  const detailButton = event.target.closest('[data-detail-id]');
  if (detailButton) {
    assignmentState.detailStack.push(detailButton.dataset.detailId);
    renderAssignments();
    scrollAssignmentsToTop();
  }
}

function onAssignmentsChange(event) {
  if (event.target.id !== 'show-pupil-premium') {
    return;
  }

  assignmentState.showPupilPremium = event.target.checked;
  renderAssignments();
}

function onAssignmentsKeydown(event) {
  if (event.key !== 'Enter' && event.key !== ' ') {
    return;
  }

  const detailRow = event.target.closest('tr[data-detail-id]');
  if (!detailRow) {
    return;
  }

  event.preventDefault();
  assignmentState.detailStack.push(detailRow.dataset.detailId);
  renderAssignments();
  scrollAssignmentsToTop();
}

function renderAssignments() {
  if (assignmentsData?.isStaff) {
    renderStaffAssignments();
    return;
  }

  renderStudentAssignments();
}

function renderStudentAssignments() {
  const container = document.createDocumentFragment();
  container.appendChild(buildStudentSection('To Do', assignmentsData.student?.toDo ?? [], 'Nothing due right now.', false));
  container.appendChild(buildStudentSection('Past', assignmentsData.student?.past ?? [], 'Past assignments will appear here.', true));
  assignmentsRoot.replaceChildren(container);
}

function buildStudentSection(title, cards, emptyText, highlightIncomplete) {
  const section = createElement('section', 'assignment-section');
  section.append(
    createElement('div', 'assignment-section-heading', title),
    cards.length > 0 ? buildCardGrid(cards, highlightIncomplete) : createElement('p', 'assignments-empty', emptyText)
  );
  return section;
}

function buildCardGrid(cards, highlightIncomplete) {
  const grid = createElement('div', 'assignment-card-grid');
  for (const card of cards) {
    grid.appendChild(buildStudentCard(card, highlightIncomplete));
  }

  return grid;
}

function buildStudentCard(card, highlightIncomplete) {
  const tagName = card.href ? 'a' : 'div';
  const isOverdue = highlightIncomplete && !card.isComplete;
  const element = createElement(tagName, `assignment-card${card.isComplete ? ' is-complete' : ''}${isOverdue ? ' is-overdue' : ''}`);
  if (card.href) {
    element.href = card.href;
  }

  const header = createElement('div', 'assignment-card-header');
  const titleBlock = createElement('div', 'assignment-card-title-block');
  titleBlock.append(
    createElement('p', 'assignment-card-title', card.courseName),
    createElement('p', 'assignment-card-meta', `Due ${card.dueDateLabel}`)
  );

  const progress = buildProgressBadge(card.completed, card.totalQuestions, false);
  header.append(titleBlock, progress);
  element.appendChild(header);

  return element;
}

function renderStaffAssignments() {
  const detailId = assignmentState.detailStack[assignmentState.detailStack.length - 1];
  const detail = (assignmentsData.staff?.details ?? []).find(item => item.id === detailId);
  const wrapper = createElement('div', 'assignment-staff-view');
  wrapper.appendChild(buildStaffControls());
  if (detail) {
    wrapper.appendChild(buildStaffDetail(detail));
    assignmentsRoot.replaceChildren(wrapper);
    return;
  }

  wrapper.appendChild(buildStaffOverview());
  assignmentsRoot.replaceChildren(wrapper);
}

function buildStaffControls() {
  const controls = createElement('div', 'assignment-staff-controls');
  const label = createElement('label', 'assignment-pp-toggle');
  const checkbox = document.createElement('input');
  checkbox.type = 'checkbox';
  checkbox.id = 'show-pupil-premium';
  checkbox.checked = assignmentState.showPupilPremium;
  label.append(checkbox, createElement('span', '', 'Show Pupil Premium breakdown'));
  controls.appendChild(label);
  return controls;
}

function buildStaffOverview() {
  const wrapper = createElement('div', 'assignment-overview');
  wrapper.appendChild(buildStaffSection('My Classes', assignmentsData.staff?.dates ?? [], assignmentsData.staff?.classes ?? [], 'Class', 'No classes with assignment data were found.'));
  wrapper.appendChild(buildStaffSection('Year Groups', assignmentsData.staff?.dates ?? [], assignmentsData.staff?.yearGroups ?? [], 'Year Group', 'No year groups with assignment data were found.'));
  wrapper.appendChild(buildStaffSection('Courses', assignmentsData.staff?.dates ?? [], assignmentsData.staff?.courses ?? [], 'Course', 'No course summaries are available.'));

  return wrapper;
}

function buildStaffSection(title, dates, rows, firstColumnTitle, emptyText) {
  const section = createElement('section', 'assignment-section');
  section.appendChild(createElement('div', 'assignment-section-heading', title));
  if (rows.length === 0) {
    section.appendChild(createElement('p', 'assignments-empty', emptyText));
    return section;
  }

  section.appendChild(buildAssignmentsTable(dates, rows, true, firstColumnTitle));
  return section;
}

function buildAssignmentsTable(dates, rows, clickable, firstColumnTitle) {
  const scroller = createElement('div', 'assignments-table-scroller');
  const table = createElement('table', 'assignments-table');
  const thead = document.createElement('thead');
  const headerRow = document.createElement('tr');
  headerRow.appendChild(createElement('th', 'assignments-table-label', firstColumnTitle));
  for (const date of dates) {
    headerRow.appendChild(createElement('th', 'assignments-table-date', date.label));
  }
  thead.appendChild(headerRow);
  table.appendChild(thead);

  const tbody = document.createElement('tbody');
  for (const row of rows) {
    const tr = document.createElement('tr');
    if (clickable && row.detailId) {
      tr.className = 'assignments-table-row is-clickable';
      tr.dataset.detailId = row.detailId;
      tr.tabIndex = 0;
      tr.setAttribute('role', 'button');
    }

    const titleCell = createElement('th', 'assignments-table-row-title', row.title ?? row.name ?? '');
    if (assignmentState.showPupilPremium && row.pupilPremium) {
      titleCell.appendChild(createElement('span', 'assignment-pp-badge', 'PP'));
    }
    tr.appendChild(titleCell);
    for (const cell of row.cells) {
      const td = document.createElement('td');
      td.className = 'assignments-table-cell';
      if (!cell.hasAssignment) {
        td.appendChild(createElement('span', 'assignments-table-empty', ''));
      } else {
        td.appendChild(buildProgressBadge(cell.completed, cell.total, true, cell.pupilPremiumCompleted, cell.pupilPremiumTotal, row.pupilPremium));
      }
      tr.appendChild(td);
    }
    tbody.appendChild(tr);
  }

  table.appendChild(tbody);
  scroller.appendChild(table);
  return scroller;
}

function buildStaffDetail(detail) {
  const wrapper = createElement('section', 'assignment-detail');
  const header = createElement('div', 'assignment-detail-header');
  const backButton = createElement('button', 'assignment-back-button material-symbols-outlined', 'arrow_back');
  backButton.type = 'button';
  backButton.dataset.action = 'back-to-overview';
  backButton.setAttribute('aria-label', 'Back');
  backButton.title = 'Back';
  header.append(backButton, createElement('h3', 'assignment-detail-title', detail.title));
  wrapper.appendChild(header);

  if ((assignmentsData.staff?.dates ?? []).length === 0) {
    wrapper.appendChild(createElement('p', 'assignments-empty', 'No assignment dates are available.'));
    return wrapper;
  }

  if ((detail.rows ?? []).length === 0) {
    wrapper.appendChild(createElement('p', 'assignments-empty', 'No assignment data were found.'));
    return wrapper;
  }

  wrapper.appendChild(buildAssignmentsTable(assignmentsData.staff.dates, detail.rows, detail.clickableRows, detail.firstColumnTitle));
  return wrapper;
}

function buildProgressBadge(completed, total, compact, pupilPremiumCompleted = 0, pupilPremiumTotal = 0, pupilPremium = false) {
  const badge = createElement('div', `assignment-progress${compact ? ' is-compact' : ''}`);
  const showPupilPremium = compact && assignmentState.showPupilPremium;
  const ring = createElement('span', `assignment-progress-ring${total > 0 && completed >= total ? ' is-complete' : ''}${showPupilPremium && pupilPremium ? ' is-pupil-premium' : ''}`);
  const progress = total > 0 ? Math.min(completed / total, 1) : 0;
  ring.style.setProperty('--progress', `${progress * 360}deg`);

  if (showPupilPremium && pupilPremiumTotal > 0 && !pupilPremium) {
    const pupilPremiumRing = createElement('span', 'assignment-progress-pupil-premium');
    pupilPremiumRing.style.setProperty('--pp-progress', `${Math.min(pupilPremiumCompleted / pupilPremiumTotal, 1) * 360}deg`);
    ring.appendChild(pupilPremiumRing);
  } else {
    const ringValue = createElement('span', 'assignment-progress-ring-value', total > 0 ? `${Math.round(progress * 100)}%` : '0%');
    ring.appendChild(ringValue);
  }

  badge.setAttribute('aria-label', `${completed} of ${total} answered`);
  badge.appendChild(ring);
  return badge;
}

function scrollAssignmentsToTop() {
  const assignmentsApp = document.getElementById('assignments-app');
  if (assignmentsApp) {
    assignmentsApp.scrollTo({ top: 0, behavior: 'smooth' });
    return;
  }

  window.scrollTo({ top: 0, behavior: 'smooth' });
}

function createElement(tagName, className, textContent) {
  const element = document.createElement(tagName);
  if (className) {
    element.className = className;
  }
  if (textContent !== undefined) {
    element.textContent = textContent;
  }
  return element;
}
