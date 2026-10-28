const assignmentsRoot = document.getElementById('assignments-root');
const assignmentState = { detailStack: [], showPupilPremium: false };

document.addEventListener('DOMContentLoaded', () => {
  if (!assignmentsRoot) {
    return;
  }

  assignmentsRoot.addEventListener('click', onAssignmentsClick);
  assignmentsRoot.addEventListener('change', onAssignmentsChange);
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
  document.getElementById('show-pupil-premium')?.focus();
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
  if (assignmentsData.student?.gamification) {
    container.appendChild(buildGamificationSummary(assignmentsData.student.gamification, assignmentsData.student.bonusQuiz));
  }
  container.appendChild(buildStudentSection('To Do', assignmentsData.student?.toDo ?? [], 'Nothing due right now.', false));
  container.appendChild(buildStudentSection('Recent', assignmentsData.student?.past ?? [], 'Past assignments will appear here.', true));
  assignmentsRoot.replaceChildren(container);
}

function buildGamificationSummary(progress, bonusQuiz) {
  const summary = createElement('section', 'assignment-gamification');
  summary.setAttribute('aria-labelledby', 'assignment-rank-title');

  const identity = createElement('div', 'assignment-gamification-identity');
  const rankTitle = createElement('h2', 'assignment-gamification-rank', progress.currentRank);
  rankTitle.id = 'assignment-rank-title';
  identity.appendChild(rankTitle);

  const total = createElement('div', 'assignment-gamification-total');
  total.appendChild(createElement('strong', 'assignment-gamification-xp', `${progress.totalXp} XP`));

  const header = createElement('div', 'assignment-gamification-header');
  header.append(identity, total);

  const streaks = createElement('div', 'assignment-gamification-streaks');
  streaks.append(
    buildGamificationStat('local_fire_department', progress.currentStreak, 'Current streak'),
    buildGamificationStat('workspace_premium', progress.bestStreak, 'Best streak')
  );

  const rankProgress = createElement('div', `assignment-rank-progress${progress.nextRank ? '' : ' is-maximum'}`);
  if (progress.nextRank) {
    const progressLabel = createElement('div', 'assignment-rank-progress-label');
    progressLabel.append(
      createElement('span', '', `Progress to ${progress.nextRank}`),
      createElement('span', '', `${progress.rankProgressXp} / ${progress.rankSpanXp} XP`)
    );
    const progressBar = createElement('div', 'assignment-rank-progress-bar');
    progressBar.setAttribute('role', 'progressbar');
    progressBar.setAttribute('aria-label', `Progress to ${progress.nextRank}`);
    progressBar.setAttribute('aria-valuemin', '0');
    progressBar.setAttribute('aria-valuemax', String(progress.rankSpanXp));
    progressBar.setAttribute('aria-valuenow', String(progress.rankProgressXp));
    const fill = createElement('span', 'assignment-rank-progress-fill');
    fill.style.width = `${progress.rankSpanXp > 0 ? Math.min(progress.rankProgressXp / progress.rankSpanXp, 1) * 100 : 0}%`;
    progressBar.appendChild(fill);
    rankProgress.append(progressLabel, progressBar);
  } else {
    const maximumIcon = createElement('span', 'material-symbols-outlined', 'verified');
    maximumIcon.setAttribute('aria-hidden', 'true');
    rankProgress.append(
      maximumIcon,
      createElement('span', '', 'Maximum rank reached')
    );
  }

  summary.append(header, streaks, rankProgress);
  if (bonusQuiz) {
    const action = createElement('a', 'assignment-bonus-quiz');
    const icon = createElement('span', 'material-symbols-outlined', bonusQuiz.inProgress ? 'play_arrow' : 'neurology');
    icon.setAttribute('aria-hidden', 'true');
    action.href = bonusQuiz.href;
    action.append(
      icon,
      createElement('strong', '', bonusQuiz.inProgress ? 'Resume bonus quiz' : 'Start bonus quiz'),
      createElement('span', 'assignment-bonus-quiz-xp', `+${bonusQuiz.quizXp} XP`)
    );
    summary.appendChild(action);
  }
  return summary;
}

function buildGamificationStat(iconName, value, label) {
  const stat = createElement('div', 'assignment-gamification-stat');
  const icon = createElement('span', 'material-symbols-outlined', iconName);
  icon.setAttribute('aria-hidden', 'true');
  const text = createElement('span', 'assignment-gamification-stat-text');
  text.append(
    createElement('strong', '', String(value)),
    document.createTextNode(` ${label}`)
  );
  stat.append(icon, text);
  return stat;
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
  if (!card.isComplete && card.awardsXp)
    titleBlock.appendChild(createElement('p', 'assignment-card-xp', `+${card.totalQuestions} XP`));

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
  if (assignmentsData.staff?.classes?.length > 0) {
    wrapper.appendChild(buildStaffSection('My Classes', assignmentsData.staff.dates ?? [], assignmentsData.staff.classes, 'Class', '', false));
  }
  wrapper.appendChild(buildStaffSection('Year Groups', assignmentsData.staff?.dates ?? [], assignmentsData.staff?.yearGroups ?? [], 'Year Group', 'No year groups with assignment data were found.', true));
  wrapper.appendChild(buildStaffSection('Courses', assignmentsData.staff?.dates ?? [], assignmentsData.staff?.courses ?? [], 'Course', 'No course summaries are available.', false));

  return wrapper;
}

function buildStaffSection(title, dates, rows, firstColumnTitle, emptyText, showCompletion) {
  const section = createElement('section', 'assignment-section');
  section.appendChild(createElement('div', 'assignment-section-heading', `${title} – ${showCompletion ? 'Completion' : 'Accuracy'}`));
  if (rows.length === 0) {
    section.appendChild(createElement('p', 'assignments-empty', emptyText));
    return section;
  }

  section.appendChild(buildAssignmentsTable(dates, rows, true, firstColumnTitle, showCompletion));
  return section;
}

function buildAssignmentsTable(dates, rows, clickable, firstColumnTitle, showCompletion) {
  const showComparison = assignmentState.showPupilPremium && firstColumnTitle !== 'Student';
  const scroller = createElement('div', 'assignments-table-scroller');
  scroller.tabIndex = 0;
  scroller.setAttribute('role', 'region');
  scroller.setAttribute('aria-label', `${firstColumnTitle} assignment ${showCompletion ? 'completion' : 'accuracy'}; scroll for more dates`);
  const table = createElement('table', 'assignments-table');
  const thead = document.createElement('thead');
  const headerRow = document.createElement('tr');
  const labelHeader = createElement('th', 'assignments-table-label', firstColumnTitle);
  labelHeader.scope = 'col';
  headerRow.appendChild(labelHeader);
  for (const date of dates) {
    const dateHeader = createElement('th', 'assignments-table-date', date.label);
    dateHeader.scope = 'col';
    headerRow.appendChild(dateHeader);
  }
  thead.appendChild(headerRow);
  table.appendChild(thead);

  const tbody = document.createElement('tbody');
  for (const row of rows) {
    const tr = document.createElement('tr');
    if (clickable && row.detailId) {
      tr.className = 'assignments-table-row is-clickable';
      tr.dataset.detailId = row.detailId;
    }

    const titleCell = createElement('th', 'assignments-table-row-title');
    titleCell.scope = 'row';
    titleCell.setAttribute('aria-label', `${row.title ?? row.name ?? ''}${assignmentState.showPupilPremium && row.pupilPremium ? ', Pupil Premium' : ''}`);
    const title = createElement('div', 'assignment-row-name');
    title.title = row.title ?? row.name ?? '';
    const name = createElement(clickable && row.detailId ? 'button' : 'span', 'assignment-row-text', row.title ?? row.name ?? '');
    if (clickable && row.detailId) {
      name.classList.add('assignment-row-link');
      name.type = 'button';
      name.dataset.detailId = row.detailId;
    }
    title.appendChild(name);
    if (assignmentState.showPupilPremium && row.pupilPremium) {
      title.appendChild(createElement('span', 'assignment-pp-badge', 'PP'));
    }
    const heading = createElement('div', 'assignment-row-heading');
    heading.appendChild(title);
    if (showComparison) {
      const cohorts = createElement('div', 'assignment-metric-cohorts');
      cohorts.title = 'All pupils (including PP) and Pupil Premium pupils';
      cohorts.append(createElement('span', '', 'All'), createElement('span', 'assignment-metric-pp', 'PP'));
      heading.appendChild(cohorts);
    }
    titleCell.appendChild(heading);
    tr.appendChild(titleCell);
    for (const cell of row.cells) {
      const td = document.createElement('td');
      td.className = 'assignments-table-cell';
      if (!cell.hasAssignment) {
        const empty = createElement('span', 'assignments-table-empty');
        empty.setAttribute('aria-label', 'No assignment');
        td.appendChild(empty);
      } else if (!showCompletion && firstColumnTitle === 'Student' && cell.completed < cell.total) {
        td.appendChild(createElement('span', 'assignment-metric-value', 'Incomplete'));
      } else {
        td.appendChild(buildStaffMetric(cell, showComparison, showCompletion));
      }
      tr.appendChild(td);
    }
    tbody.appendChild(tr);
  }

  table.appendChild(tbody);
  scroller.appendChild(table);
  return scroller;
}

function buildStaffMetric(cell, showComparison, showCompletion) {
  const metric = showCompletion
    ? { label: 'Completion', completed: cell.completed, total: cell.total, ppCompleted: cell.pupilPremiumCompleted, ppTotal: cell.pupilPremiumTotal, description: 'questions answered correctly, including retries' }
    : { label: 'Accuracy', completed: cell.firstTimeCorrect, total: cell.attempted, ppCompleted: cell.pupilPremiumFirstTimeCorrect, ppTotal: cell.pupilPremiumAttempted, description: 'attempted questions answered correctly first time' };
  const chart = createElement('div', `assignment-metric-chart${showCompletion ? '' : ' is-accuracy'}`);
  const cohorts = [{ label: 'All', completed: metric.completed, total: metric.total }];
  if (showComparison) {
    cohorts.push({ label: 'PP', completed: metric.ppCompleted, total: metric.ppTotal });
  }
  for (const cohort of cohorts) {
    const percentage = cohort.total > 0 ? cohort.completed / cohort.total * 100 : 0;
    const value = cohort.total > 0 ? `${Math.round(percentage)}%` : '';
    const assignedTotal = cohort.label === 'PP' ? cell.pupilPremiumTotal : cell.total;
    const detail = cohort.total > 0 ? `${value}; ${cohort.completed} of ${cohort.total} ${metric.description}` : assignedTotal > 0 ? 'no questions attempted' : 'no applicable questions';
    const description = `${showComparison ? `${cohort.label}: ` : ''}${metric.label}, ${detail}`;
    const bar = createElement('div', `assignment-metric-bar${cohort.label === 'PP' ? ' is-pp' : ''}${cohort.total > 0 ? '' : ' is-empty'}`);
    bar.title = description;
    bar.setAttribute('aria-label', description);
    const track = createElement('span', 'assignment-metric-track');
    track.setAttribute('aria-hidden', 'true');
    const fill = createElement('span', 'assignment-metric-fill');
    fill.style.width = `${Math.min(percentage, 100)}%`;
    track.appendChild(fill);
    bar.append(track, createElement('span', 'assignment-metric-value', value));
    chart.appendChild(bar);
  }
  return chart;
}

function buildStaffDetail(detail) {
  const wrapper = createElement('section', 'assignment-detail');
  const header = createElement('div', 'assignment-detail-header');
  const backButton = createElement('button', 'assignment-back-button material-symbols-outlined', 'arrow_back');
  backButton.type = 'button';
  backButton.dataset.action = 'back-to-overview';
  backButton.setAttribute('aria-label', 'Back');
  backButton.title = 'Back';
  header.append(backButton, createElement('h3', 'assignment-detail-title', `${detail.title} – ${detail.showCompletion ? 'Completion' : 'Accuracy'}`));
  wrapper.appendChild(header);

  if ((assignmentsData.staff?.dates ?? []).length === 0) {
    wrapper.appendChild(createElement('p', 'assignments-empty', 'No assignment dates are available.'));
    return wrapper;
  }

  if ((detail.rows ?? []).length === 0) {
    wrapper.appendChild(createElement('p', 'assignments-empty', 'No assignment data were found.'));
    return wrapper;
  }

  wrapper.appendChild(buildAssignmentsTable(assignmentsData.staff.dates, detail.rows, detail.clickableRows, detail.firstColumnTitle, detail.showCompletion));
  const questionsSection = buildQuestionsSection(detail);
  if (questionsSection) {
    wrapper.appendChild(questionsSection);
  }
  return wrapper;
}

function buildQuestionsSection(detail) {
  const questions = detail.questions ?? [];
  if (questions.length === 0) {
    return null;
  }

  const section = createElement('section', 'assignment-questions');
  section.appendChild(createElement('div', 'assignment-questions-heading', detail.questionsTitle || 'Questions'));

  const list = createElement('div', 'assignment-questions-list');
  for (const question of [...questions].sort((a, b) => (a.percentage ?? 0) - (b.percentage ?? 0))) {
    list.appendChild(buildQuestionSummary(question));
  }
  section.appendChild(list);
  return section;
}

function buildQuestionSummary(question) {
  const item = createElement('article', 'assignment-question-summary');
  item.appendChild(buildQuestionProgressBadge(question));

  const body = createElement('div', 'assignment-question-summary-body');
  if (question.unitTitle) {
    body.appendChild(createElement('p', 'assignment-question-summary-unit', question.unitTitle));
  }
  body.appendChild(createElement('p', 'assignment-question-summary-text', question.questionText));

  const answers = createElement('div', 'assignment-question-summary-answers');
  answers.appendChild(buildAnswerLine('Correct', question.correctAnswer, true));
  for (const answer of question.incorrectAnswers ?? []) {
    answers.appendChild(buildAnswerLine('Incorrect', answer, false));
  }
  body.appendChild(answers);
  item.appendChild(body);
  return item;
}

function buildQuestionProgressBadge(question) {
  const attempted = question.attempted ?? 0;
  const percentage = attempted > 0 ? question.percentage ?? 0 : 0;
  const badge = createElement('div', 'assignment-question-score');
  const ring = createElement('span', 'assignment-question-score-ring');
  ring.style.setProperty('--question-progress', `${Math.min(percentage / 100, 1) * 360}deg`);
  ring.style.setProperty('--question-ring-color', getQuestionRingColor(percentage));
  ring.appendChild(createElement('span', 'assignment-question-score-value', attempted > 0 ? `${percentage}%` : '0%'));
  badge.appendChild(ring);
  badge.setAttribute('aria-label', attempted > 0 ? `${percentage}% answered correctly first time` : 'Not answered yet');
  return badge;
}

function getQuestionRingColor(value) {
  const stops = [[50, [0, 86, 70]], [75, [48, 86, 56]], [100, [137, 45, 57]]];
  const [from, to] = value < 75 ? [stops[0], stops[1]] : [stops[1], stops[2]];
  const amount = Math.max(0, Math.min(1, (value - from[0]) / (to[0] - from[0])));
  const hsl = from[1].map((channel, index) => Math.round(channel + (to[1][index] - channel) * amount));
  return `hsl(${hsl[0]} ${hsl[1]}% ${hsl[2]}%)`;
}

function buildAnswerLine(label, text, correct) {
  const line = createElement('p', `assignment-question-summary-answer${correct ? ' is-correct' : ''}`);
  const icon = createElement('span', 'assignment-question-summary-answer-icon material-symbols-outlined', correct ? 'check' : 'close');
  icon.setAttribute('aria-label', label);
  line.append(icon, document.createTextNode(text));
  return line;
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
