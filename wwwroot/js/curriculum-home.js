
const qs = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));
const clone = id => document.getElementById(id).content.firstElementChild.cloneNode(true);
const schemeUrlExample = `e.g. https://${microsoftSharePointSubdomain}.sharepoint.com/:w:/r/sites/Department/Shared%20Documents/General/Unit/Scheme.docx`;
const createDocumentUrlConfig = title => ({
  title,
  question: 'Please paste a link to the document, which must be located in your department Teams folder. Right-click it and select Copy Link > Settings > Only people with existing access.',
  example: schemeUrlExample,
  input: 'text'
});

const yearsForCourse = course => course.subjectCode === 'Rg'
  ? [7, 8]
  : course.keyStage === 3
    ? [7, 8, 9]
    : course.keyStage === 4
      ? [10, 11]
      : [12, 13];

const fields = {
  intent: 'intent',
  specification: 'specification',
  'assignment-length': 'assignmentLength',
  term: 'term',
  checklist: 'checklist',
  'why-this': 'whyThis',
  'why-now': 'whyNow',
  'scheme-url': 'schemeUrl',
  'assessment-url': 'assessmentUrl',
  'mark-scheme-url': 'markSchemeUrl',
  rename: 'title'
};
const configuredChecklistItems = Array.isArray(checklistItems)
  ? checklistItems.filter(item => item?.id && item?.title)
  : [];
const checklistItemsById = new Map(configuredChecklistItems.map(item => [item.id, item]));
const checklistStatusOptions = [
  { value: '0', label: 'Incomplete', className: 'status-0' },
  { value: '2', label: 'Exempt', className: 'status-2' },
  { value: '1', label: 'Complete', className: 'status-1' }
];
let checklistTooltip;

const modalConfig = {
  intent: {
    title: 'Curriculum Intent',
    question: 'In a single sentence, what do students achieve by studying this course?',
    example: 'e.g. Develop the knowledge, skills, and confidence to communicate in common scenarios in French.',
    input: 'text'
  },
  specification: {
    title: 'Specification',
    question: 'State the exam board, course title, and specification code.',
    example: 'e.g. Edexcel GCSE Mathematics (1MA1)',
    input: 'text'
  },
  'assignment-length': {
    title: 'Weekly Assignment Length',
    question: 'Enter the number of questions to set each week.',
    example: 'e.g. 20',
    input: 'text'
  },
  term: { question: 'Select the term in which this unit is assessed:', input: 'select' },
  'why-this': {
    question: '<b>Why this?</b> Explain the reason we\'ve included the unit in our curriculum, without reference to exam specifications.',
    example: 'e.g. It is important to understand how and why objects move.',
    input: 'text'
  },
  'why-now': {
    question: '<b>Why now?</b> Explain why the unit is taught at this point in the course.',
    example: 'e.g. This unit develops ideas from the Year 7 forces topic by considering effects of forces on motion.',
    input: 'text'
  },
  'scheme-url': createDocumentUrlConfig('Scheme URL'),
  'assessment-url': createDocumentUrlConfig('Assessment URL'),
  'mark-scheme-url': createDocumentUrlConfig('Mark Scheme URL'),
  checklist: {
    title: 'Checklist',
    question: 'Evaluate this unit:',
    input: 'checklist'
  },
  rename: { question: 'Rename this unit:', input: 'text' },
  new: { title: yearGroup => `New Year ${yearGroup} Unit`, question: 'Enter the title:', input: 'text' }
};

const elements = {
  aside: document.querySelector('aside'),
  main: document.querySelector('main'),
  courseList: document.getElementById('course-list'),
  courseDetail: document.getElementById('course-detail'),
  unitDetail: document.getElementById('unit-detail'),
  unitQuiz: document.getElementById('unit-quiz'),
  quizBack: document.getElementById('quiz-back'),
  quizRestart: document.getElementById('quiz-restart'),
  quizTitle: document.getElementById('quiz-title'),
  question: document.getElementById('question'),
  answer1: document.getElementById('answer1'),
  answer2: document.getElementById('answer2'),
  progress: document.getElementById('progress'),
  outcome: document.getElementById('outcome'),
  quizPlay: document.getElementById('quiz-play'),
  quizResult: document.getElementById('quiz-result'),
  modal: document.getElementById('modal'),
  modalTitle: document.getElementById('modal-title'),
  modalQuestion: document.getElementById('modal-body-question'),
  modalExample: document.getElementById('modal-body-example'),
  modalText: document.getElementById('modal-text'),
  modalSelect: document.getElementById('modal-select'),
  modalChecklist: document.getElementById('modal-checklist-container'),
  modalSave: document.getElementById('modal-save'),
  modalClose: document.getElementById('modal-close'),
  textBoxContainer: document.getElementById('modal-text-container'),
  selectContainer: document.getElementById('modal-select-container')
};

const state = { courseId: null, unitId: null, courseEditable: false, editMode: false, quizQuestions: [], remainingQuestions: [] };
const coursesRootPath = '/courses';
const coursesPathPrefix = '/courses/';

document.addEventListener('DOMContentLoaded', async () => {
  renderCourseList();

  elements.courseList.addEventListener('click', event => {
    const li = event.target.closest('.course-item');
    if (li) {
      showCourse(li.dataset.courseId);
    }
  });

  $$('header h1').forEach(element => element.addEventListener('click', event => showCourseList(event, { scrollCourseListToTop: true })));
  elements.answer1.addEventListener('click', () => answer(elements.answer1));
  elements.answer2.addEventListener('click', () => answer(elements.answer2));
  elements.quizBack.addEventListener('click', backToUnit);
  elements.quizRestart.addEventListener('click', startQuiz);

  if (isEditableStaff) {
    elements.courseDetail.addEventListener('click', onCourseDetailClick);
    elements.modalSave.addEventListener('click', onSave);
    elements.modalClose.addEventListener('click', () => elements.modal.classList.remove('active'));
    elements.modalText.addEventListener('keydown', event => {
      if (event.key === 'Enter') {
        event.preventDefault();
      }
    });
  }

  window.addEventListener('popstate', () => {
    syncFromLocation();
  });
  window.addEventListener('scroll', hideChecklistTooltip, true);
  window.addEventListener('resize', hideChecklistTooltip);

  await syncFromLocation();
  updateLoginPath();
});

function encodeSegment(value) {
  return encodeURIComponent(value);
}

function decodeSegment(value) {
  try {
    return decodeURIComponent(value);
  } catch {
    return '';
  }
}

function buildCurriculumPath(courseId, unitId, action) {
  if (!courseId) {
    return coursesRootPath;
  }

  const courseSegment = encodeSegment(courseId);
  if (!unitId) {
    return `${coursesPathPrefix}${courseSegment}`;
  }

  const unitSegment = encodeSegment(unitId);
  if (action === 'quiz') {
    return `${coursesPathPrefix}${courseSegment}/${unitSegment}/quiz`;
  }

  return `${coursesPathPrefix}${courseSegment}/${unitSegment}`;
}

function updateDisplayPath(path, replace = false) {
  const currentPath = window.location.pathname;
  const isCanonicalCurrent = !window.location.search && !window.location.hash;
  if (currentPath === path && isCanonicalCurrent) {
    updateLoginPath();
    return;
  }

  if (replace) {
    history.replaceState(null, '', path);
  } else {
    history.pushState(null, '', path);
  }

  updateLoginPath();
}

function updateLoginPath() {
  const loginButton = document.querySelector('.auth-actions .auth-button[href^="/login?path="]');
  if (!loginButton) {
    return;
  }

  const path = `${window.location.pathname}${window.location.search}` || '/';
  loginButton.href = `/login?path=${encodeURIComponent(path)}`;
}

function renderCourseList() {
  const fragment = document.createDocumentFragment();
  for (const course of courses) {
    const li = clone('tpl-course-item');
    li.dataset.courseId = course.id;
    qs('.course-name', li).textContent = course.name;
    fragment.appendChild(li);
  }

  elements.courseList.replaceChildren(fragment);
}

function courseUnits(courseId) {
  return units
    .filter(unit => unit.courseId === courseId)
    .sort((a, b) =>
      a.yearGroup - b.yearGroup
      || (a.term ?? '\uFFFF').localeCompare(b.term ?? '\uFFFF')
      || a.order - b.order
      || a.title.localeCompare(b.title)
    );
}

function courseById(courseId) {
  return courses.find(course => course.id === courseId);
}

function unitById(unitId) {
  return units.find(unit => unit.id === unitId);
}

function showCourseList(eventOrOptions, maybeOptions = {}) {
  const event = eventOrOptions && typeof eventOrOptions.preventDefault === 'function' ? eventOrOptions : null;
  const options = event ? maybeOptions : (eventOrOptions ?? {});

  state.courseId = null;
  state.unitId = null;
  state.courseEditable = false;
  state.editMode = false;
  elements.aside.classList.add('active');
  elements.main.classList.remove('active');
  elements.courseDetail.classList.add('active');
  elements.courseDetail.classList.remove('edit-mode');
  elements.unitDetail.classList.remove('active');
  elements.unitQuiz.classList.remove('active');
  elements.courseList.querySelector('.course-item.active')?.classList.remove('active');
  elements.courseDetail.classList.add('is-empty');
  elements.courseDetail.replaceChildren(clone('tpl-course-placeholder'));

  if (options.scrollCourseListToTop) {
    elements.aside.scrollTop = 0;
  }

  if (!options.skipHistory) {
    updateDisplayPath(coursesRootPath, !!options.replaceHistory);
  } else {
    updateLoginPath();
  }

  event?.preventDefault();
}
function showCourse(courseId, options = {}) {
  const previousCourseId = state.courseId;
  if (!options.keepEditMode || previousCourseId !== courseId) {
    state.editMode = false;
  }

  state.courseId = courseId;
  state.unitId = null;

  for (const li of elements.courseList.children) {
    li.classList.toggle('active', li.dataset.courseId === courseId);
  }

  const course = courseById(courseId);
  if (!course) {
    showCourseList({ skipHistory: options.skipHistory, replaceHistory: options.replaceHistory });
    return;
  }

  state.courseEditable = isEditableStaff && editableCourseIds.includes(course.id);
  const inEditMode = state.courseEditable && state.editMode;

  const unitsForCourse = courseUnits(courseId);
  const leaders = course.leaderNames;
  const visibleYears = isStaff ? yearsForCourse(course) : [...new Set(unitsForCourse.map(unit => unit.yearGroup))];

  const container = document.createDocumentFragment();

  const back = clone('tpl-back-course');
  back.addEventListener('click', showCourseList);

  if (state.courseEditable) {
    const actions = document.createElement('div');
    actions.className = 'staff-course-actions';

    const summaryButton = document.createElement('button');
    summaryButton.type = 'button';
    summaryButton.className = 'summary-button material-symbols-outlined';
    summaryButton.title = 'Text summary';
    summaryButton.setAttribute('aria-label', 'Open text summary');
    summaryButton.textContent = 'summarize';
    summaryButton.addEventListener('click', () => window.open(`/courses/${courseId}/build/summary`, '_blank'));

    const editToggleButton = document.createElement('button');
    editToggleButton.type = 'button';
    editToggleButton.className = 'edit-toggle-button material-symbols-outlined';
    editToggleButton.title = 'Edit curriculum';
    editToggleButton.setAttribute('aria-label', 'Edit curriculum');
    editToggleButton.textContent = 'edit';
    editToggleButton.addEventListener('click', () => {
      state.editMode = true;
      showCourse(courseId, { keepEditMode: true, skipHistory: true });
    });

    if (state.editMode) {
      editToggleButton.classList.add('hide');
    }

    actions.append(summaryButton, editToggleButton);
    container.appendChild(actions);
  }

  const title = clone('tpl-course-title');
  title.appendChild(back);
  const titleSpan = document.createElement('span');
  titleSpan.textContent = course.name;
  title.appendChild(titleSpan);
  container.appendChild(title);

  const leaderInfo = clone('tpl-course-info');
  qs('.icon', leaderInfo).textContent = 'badge';
  qs('.label', leaderInfo).textContent = leaders.includes(',') ? 'Course leaders' : 'Course leader';
  qs('.value', leaderInfo).textContent = leaders;
  container.appendChild(leaderInfo);

  const showSpecification = isStaff || !!course.specification;
  if (showSpecification && !course.name.startsWith('KS')) {
    const specificationInfo = clone('tpl-course-info');
    qs('.icon', specificationInfo).textContent = 'book_5';
    qs('.label', specificationInfo).textContent = 'Specification';
    const value = qs('.value', specificationInfo);
    value.className = 'specification-content';
    if (course.specification) {
      value.textContent = course.specification;
    } else {
      value.textContent = 'Not configured';
      value.classList.add('not-configured');
    }
    if (inEditMode) {
      specificationInfo.appendChild(buildEditButton(courseId, '', 'specification'));
    }
    container.appendChild(specificationInfo);
  }

  const showIntent = isStaff || !!course.intent;
  if (showIntent) {
    const intentInfo = clone('tpl-course-info');
    qs('.icon', intentInfo).textContent = 'target';
    qs('.label', intentInfo).textContent = 'Curriculum intent';
    const value = qs('.value', intentInfo);
    value.className = 'intent-content';
    if (course.intent) {
      value.textContent = course.intent;
    } else {
      value.textContent = 'Not configured';
      value.classList.add('not-configured');
    }
    if (inEditMode) {
      intentInfo.appendChild(buildEditButton(courseId, '', 'intent'));
    }
    container.appendChild(intentInfo);
  }

  for (const year of visibleYears) {
    container.appendChild(renderYearSection(courseId, unitsForCourse, year));
  }

  if (isStaff) {
    const assignmentInfo = clone('tpl-course-info');
    qs('.icon', assignmentInfo).textContent = 'quiz';
    qs('.label', assignmentInfo).textContent = 'Weekly assignment length';
    qs('.value', assignmentInfo).textContent = `${course.assignmentLength} ${course.assignmentLength === 1 ? 'question' : 'questions'}`;
    if (inEditMode && isAdmin) {
      assignmentInfo.appendChild(buildEditButton(courseId, '', 'assignment-length'));
    }
    container.appendChild(assignmentInfo);
  }

  if (!isStaff && unitsForCourse.length === 0 && !course.specification && !course.intent) {
    container.appendChild(clone('tpl-no-course-info'));
  }

  elements.courseDetail.replaceChildren(container);
  elements.courseDetail.classList.toggle('edit-mode', state.editMode);
  elements.courseDetail.classList.remove('is-empty');
  elements.aside.classList.remove('active');
  elements.main.classList.add('active');
  elements.courseDetail.classList.add('active');
  elements.unitDetail.classList.remove('active');
  elements.unitQuiz.classList.remove('active');
  elements.main.scrollTop = 0;
  elements.courseDetail.scrollTop = 0;

  if (state.courseEditable && window.Sortable) {
    setupSortable();
  }

  if (!options.skipHistory) {
    updateDisplayPath(buildCurriculumPath(courseId), !!options.replaceHistory);
  } else {
    updateLoginPath();
  }
}

function renderYearSection(courseId, unitsForCourse, yearGroup) {
  const section = clone('tpl-year-group-section');
  qs('.year-title', section).textContent = `Year ${yearGroup} units`;

  const unitsList = qs('.units-list', section);
  const list = unitsForCourse.filter(unit => unit.yearGroup === yearGroup);
  if (list.length === 0) {
    unitsList.classList.add('hide');
    const noUnits = qs('.no-units', section);
    noUnits.classList.remove('hide');
    if (isStaff) {
      noUnits.classList.add('not-configured');
    }
  } else {
    unitsList.classList.remove('hide');
    const fragment = document.createDocumentFragment();
    for (const unit of list) {
      fragment.appendChild(renderUnit(courseId, unit));
    }
    unitsList.appendChild(fragment);
  }

  if (state.courseEditable && state.editMode) {
    const heading = qs('.year-group-heading', section);
    const newButton = buildEditButton(courseId, String(yearGroup), 'new', 'add');
    newButton.classList.add('staff-new-unit');
    heading.appendChild(newButton);
  }

  return section;
}

function renderUnit(courseId, unit) {
  if (!isStaff) {
    const li = clone('tpl-unit-item');
    li.dataset.id = unit.id;
    qs('.unit-title', li).textContent = unit.title;
    qs('.term-text', li).textContent = `Year ${unit.yearGroup} ${unit.term ? `${unit.term} Term` : ''}`;
    li.addEventListener('click', () => showUnit(unit.id));
    return li;
  }

  const li = document.createElement('li');
  li.className = 'unit-item unit-item-staff';
  li.dataset.id = unit.id;

  if (state.courseEditable && state.editMode) {
    const handle = document.createElement('div');
    handle.className = 'handle material-symbols-outlined';
    handle.title = 'Drag to reorder';
    handle.setAttribute('aria-label', 'Drag to reorder unit');
    handle.textContent = 'drag_indicator';
    li.appendChild(handle);
  }

  const topRow = document.createElement('div');
  topRow.className = 'staff-top-row';

  const unitInfo = document.createElement('div');
  unitInfo.className = 'staff-unit-info';

  const titleRow = document.createElement('div');
  titleRow.className = 'staff-unit-title';

  const title = document.createElement('button');
  title.type = 'button';
  title.className = 'unit-open';
  title.textContent = unit.title;
  title.addEventListener('click', () => showUnit(unit.id));
  titleRow.appendChild(title);

  if (state.courseEditable && state.editMode) {
    titleRow.append(buildEditButton(courseId, unit.id, 'rename'), buildEditButton(courseId, unit.id, 'delete', 'delete'));
  }

  const term = document.createElement('div');
  term.className = 'term';
  const termText = document.createElement('span');
  termText.textContent = formatTermText(unit);
  if (!unit.term) {
    termText.classList.add('not-configured');
  }
  term.appendChild(termText);
  if (state.courseEditable && state.editMode) {
    term.appendChild(buildEditButton(courseId, unit.id, 'term'));
  }

  const checklist = renderChecklist(courseId, unit);
  unitInfo.append(titleRow, term);
  if (checklist) {
    unitInfo.appendChild(checklist);
  }

  const assessmentLinks = document.createElement('div');
  assessmentLinks.className = 'unit-assessment';
  assessmentLinks.append(
    buildSchemeLink(courseId, unit),
    buildAssessmentLink(courseId, unit, 'key-knowledge', 'Knowledge', unit.keyKnowledgeStatus, unit.yearGroup <= 9, 'neurology'),
    buildAssessmentLink(courseId, unit, 'assessment', 'Assessment', unit.assessmentStatus, true, 'article'),
    buildAssessmentLink(courseId, unit, 'mark-scheme', 'Mark Scheme', unit.assessmentStatus, true, 'done_all'),
    buildQuizLink(courseId, unit),
  );

  topRow.append(unitInfo, assessmentLinks);
  li.appendChild(topRow);

  const why = document.createElement('div');
  why.className = 'unit-why';
  why.append(renderWhyField(courseId, unit, 'Why this?', 'why-this', 'whyThis'), renderWhyField(courseId, unit, 'Why now?', 'why-now', 'whyNow'));
  li.appendChild(why);

  return li;
}

function renderWhyField(courseId, unit, label, property, field) {
  const container = document.createElement('div');
  container.className = property;
  const labelElement = document.createElement('b');
  labelElement.textContent = `${label} `;
  const value = document.createElement('span');
  value.textContent = unit[field] || 'Not configured';
  if (!unit[field]) {
    value.classList.add('not-configured');
  }
  container.append(labelElement, value);
  if (state.courseEditable && state.editMode) {
    container.appendChild(buildEditButton(courseId, unit.id, property));
  }
  return container;
}

function buildAssessmentLink(courseId, unit, tab, text, status, required, icon) {
  const link = document.createElement('a');
  const property = tab === 'assessment' ? 'assessment-url' : tab === 'mark-scheme' ? 'mark-scheme-url' : '';
  const url = property ? unit[fields[property]] : '';
  const resolvedStatus = url ? 2 : status;
  link.href = url || `/courses/${courseId}/${unit.id}/build#${tab}`;
  link.className = `material-symbols-outlined status-${resolvedStatus}${required ? ' required' : ''}`;
  link.textContent = icon;
  link.title = text;
  link.setAttribute('aria-label', text);

  if (property) {
    configureDocumentLink(link, courseId, unit, property, url, true);
  }

  return link;
}

function buildSchemeLink(courseId, unit) {
  const link = document.createElement('a');
  link.href = unit.schemeUrl || '#';
  link.className = `material-symbols-outlined ${unit.schemeUrl ? 'status-2' : 'status-0 required'}`;
  link.textContent = 'calendar_view_week';
  link.title = 'Scheme of Work';
  link.setAttribute('aria-label', 'Scheme of Work');
  configureDocumentLink(link, courseId, unit, 'scheme-url', unit.schemeUrl, false);
  return link;
}

function buildQuizLink(courseId, unit) {
  const link = document.createElement('a');
  link.href = `/courses/${courseId}/${unit.id}/build#quiz`;
  const required = unit.yearGroup <= 9 && unit.revisionQuizStatus === 0;
  link.className = `material-symbols-outlined status-${unit.revisionQuizStatus}${required ? ' required' : ''}`;
  link.textContent = 'quiz';
  link.title = 'Quiz Questions';
  link.setAttribute('aria-label', 'Quiz Questions');
  return link;
}

function configureDocumentLink(link, courseId, unit, property, url, blankClickNavigates) {
  const hasUrl = !!url;
  const modifierEditDisabled = blankClickNavigates && unit.assessmentStatus > 0;
  if (hasUrl) {
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
  }

  link.addEventListener('click', event => {
    const isModifierClick = event.shiftKey || event.ctrlKey || event.altKey;
    if (isModifierClick && modifierEditDisabled) {
      return;
    }

    if (hasUrl) {
      if (!isModifierClick || !state.courseEditable) {
        return;
      }
    } else if (blankClickNavigates && (!isModifierClick || !state.courseEditable)) {
      return;
    }

    event.preventDefault();
    if (!state.courseEditable) {
      return;
    }

    openEditModal(courseId, unit.id, property);
  });
}

function buildEditButton(courseId, unitId, property, icon = 'edit') {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'edit-button material-symbols-outlined';
  button.dataset.course = courseId;
  button.dataset.unit = unitId;
  button.dataset.property = property;
  const labels = {
    new: 'Add unit',
    delete: 'Delete unit',
    rename: 'Rename unit',
    term: 'Edit term',
    checklist: 'Edit checklist',
    'why-this': 'Edit why this',
    'why-now': 'Edit why now',
    intent: 'Edit curriculum intent',
    specification: 'Edit specification',
    'assignment-length': 'Edit weekly assignment length'
  };
  const label = labels[property] || 'Edit';
  button.title = label;
  button.setAttribute('aria-label', label);
  button.textContent = icon;
  return button;
}

function formatTermText(unit) {
  return unit.term ? `Year ${unit.yearGroup} ${unit.term} Term` : `Year ${unit.yearGroup} (Term not configured)`;
}

function renderChecklist(courseId, unit) {
  if (!isStaff || configuredChecklistItems.length === 0) {
    return null;
  }

  const checklist = document.createElement('div');
  checklist.className = 'unit-checklist';
  const canEditChecklist = state.courseEditable;

  if (canEditChecklist) {
    checklist.classList.add('unit-checklist-action');
    checklist.tabIndex = 0;
    checklist.setAttribute('role', 'button');
    checklist.setAttribute('aria-haspopup', 'dialog');
    checklist.setAttribute('aria-label', `Edit checklist for ${unit.title}`);
    checklist.addEventListener('click', () => openChecklistEditModal(courseId, unit.id));
    checklist.addEventListener('keydown', event => {
      if (event.key !== 'Enter' && event.key !== ' ') {
        return;
      }

      event.preventDefault();
      openChecklistEditModal(courseId, unit.id);
    });
  } else {
    checklist.setAttribute('aria-label', 'Checklist');
  }

  const statuses = parseChecklist(unit.checklist);
  for (const item of configuredChecklistItems) {
    const status = statuses[item.id] ?? 0;
    const target = document.createElement('div');
    target.className = 'checklist-dot-target';
    target.dataset.tooltip = item.title;
    target.setAttribute('aria-hidden', 'true');

    const dot = document.createElement('div');
    dot.className = 'checklist-dot';
    dot.classList.add(`checklist-status-${status}`);
    dot.setAttribute('aria-hidden', 'true');

    target.appendChild(dot);
    attachChecklistTooltip(target);
    checklist.appendChild(target);
  }

  return checklist;
}

function normalizeChecklistStatus(value) {
  if (value === 1 || value === '1') {
    return 1;
  }

  if (value === 2 || value === '2') {
    return 2;
  }

  return 0;
}

function parseChecklist(value) {
  const statuses = Object.create(null);
  if (!value) {
    return statuses;
  }

  for (const pair of String(value).split(';')) {
    const [idPart, statusPart] = pair.split(',', 2);
    const id = (idPart || '').trim();
    if (!id || !checklistItemsById.has(id)) {
      continue;
    }

    statuses[id] = normalizeChecklistStatus((statusPart || '').trim());
  }

  return statuses;
}

function serializeChecklist(statuses) {
  return configuredChecklistItems
    .map(item => `${item.id},${normalizeChecklistStatus(statuses[item.id])}`)
    .join(';');
}

function buildChecklistEditor(checklistValue) {
  const list = document.createElement('div');
  list.className = 'checklist-editor';
  const statuses = parseChecklist(checklistValue);

  for (const item of configuredChecklistItems) {
    const row = document.createElement('div');
    row.className = 'checklist-editor-row';

    const controls = document.createElement('div');
    controls.className = 'checklist-editor-controls';

    for (const option of checklistStatusOptions) {
      const label = document.createElement('label');
      label.className = 'checklist-radio-label';

      const input = document.createElement('input');
      input.type = 'radio';
      input.className = `checklist-radio ${option.className}`;
      input.name = `checklist-${item.id}`;
      input.value = option.value;
      input.title = option.label;
      input.setAttribute('aria-label', `${option.label}: ${item.title}`);
      input.checked = normalizeChecklistStatus(statuses[item.id]) === Number(option.value);

      label.appendChild(input);
      controls.appendChild(label);
    }

    const title = document.createElement('div');
    title.className = 'checklist-editor-title';
    title.textContent = item.title;

    row.append(controls, title);
    list.appendChild(row);
  }

  return list;
}

function readChecklistEditorValue() {
  const statuses = Object.create(null);
  for (const item of configuredChecklistItems) {
    const selected = elements.modalChecklist.querySelector(`input[name="checklist-${item.id}"]:checked`);
    statuses[item.id] = normalizeChecklistStatus(selected?.value);
  }

  return serializeChecklist(statuses);
}

function getChecklistTooltip() {
  if (!checklistTooltip) {
    checklistTooltip = document.createElement('div');
    checklistTooltip.className = 'checklist-tooltip';
    checklistTooltip.setAttribute('aria-hidden', 'true');
    document.body.appendChild(checklistTooltip);
  }

  return checklistTooltip;
}

function isEditModalOpen() {
  return elements.modal.classList.contains('active');
}

function positionChecklistTooltip(target) {
  const tooltip = getChecklistTooltip();
  const rect = target.getBoundingClientRect();
  tooltip.style.left = `${rect.left + (rect.width / 2)}px`;
  tooltip.style.top = `${rect.top - 10}px`;
}

function showChecklistTooltip(target) {
  if (isEditModalOpen()) {
    hideChecklistTooltip();
    return;
  }

  const tooltip = getChecklistTooltip();
  tooltip.textContent = target.dataset.tooltip || '';
  positionChecklistTooltip(target);
  tooltip.classList.add('active');
  tooltip.setAttribute('aria-hidden', 'false');
}

function hideChecklistTooltip() {
  if (!checklistTooltip) {
    return;
  }

  checklistTooltip.classList.remove('active');
  checklistTooltip.setAttribute('aria-hidden', 'true');
}

function attachChecklistTooltip(element) {
  element.addEventListener('mouseenter', () => showChecklistTooltip(element));
  element.addEventListener('focus', () => showChecklistTooltip(element));
  element.addEventListener('mouseleave', hideChecklistTooltip);
  element.addEventListener('blur', hideChecklistTooltip);
}

function openChecklistEditModal(courseId, unitId) {
  hideChecklistTooltip();
  openEditModal(courseId, unitId, 'checklist');
}

function setupSortable() {
  for (const list of $$('.units-list', elements.courseDetail)) {
    new Sortable(list, {
      handle: '.handle',
      animation: 150,
      ghostClass: 'drop-highlight',
      direction: 'vertical',
      onEnd: sortUnits
    });
  }
}

function resetQuizState() {
  state.quizQuestions = [];
  state.remainingQuestions = [];
}

async function showUnit(unitId, options = {}) {
  state.unitId = unitId;
  resetQuizState();
  const unit = unitById(unitId);
  if (!unit) {
    return;
  }

  const fragment = document.createDocumentFragment();

  const header = clone('tpl-unit-header');
  qs('.unit-title', header).textContent = unit.title;
  qs('[data-action="back-to-course"]', header).addEventListener('click', () => showCourse(state.courseId, { keepEditMode: state.editMode }));
  fragment.appendChild(header);

  if (unit.whyThis || unit.whyNow) {
    const rationale = clone('tpl-unit-rationale');
    if (unit.whyThis) {
      const whyThis = qs('.why-this', rationale);
      whyThis.classList.remove('hide');
      qs('.text', whyThis).textContent = unit.whyThis;
    }
    if (unit.whyNow) {
      const whyNow = qs('.why-now', rationale);
      whyNow.classList.remove('hide');
      qs('.text', whyNow).textContent = unit.whyNow;
    }
    fragment.appendChild(rationale);
  }

  const hasKeyKnowledge = unitHasKeyKnowledge(unit);
  if (hasKeyKnowledge) {
    fragment.appendChild(clone('tpl-key-knowledge'));
  }

  if (!unit.whyThis && !unit.whyNow && !hasKeyKnowledge) {
    fragment.appendChild(clone('tpl-no-unit-info'));
  }

  elements.unitDetail.replaceChildren(fragment);
  elements.aside.classList.remove('active');
  elements.main.classList.add('active');
  elements.courseDetail.classList.remove('active');
  elements.unitQuiz.classList.remove('active');
  elements.unitDetail.classList.add('active');
  elements.main.scrollTop = 0;

  if (!options.skipHistory) {
    updateDisplayPath(buildCurriculumPath(state.courseId, unitId), !!options.replaceHistory);
  } else {
    updateLoginPath();
  }

  if (hasKeyKnowledge) {
    await loadKeyKnowledge(unitId);
  }
}

function unitHasKeyKnowledge(unit) {
  if (typeof unit.hasKeyKnowledge === 'boolean') {
    return unit.hasKeyKnowledge;
  }

  return unit.keyKnowledgeStatus === 2;
}

async function loadKeyKnowledge(unitId) {
  const declarative = document.getElementById('declarative-knowledge');
  const procedural = document.getElementById('procedural-knowledge');

  const response = await fetch(`/keyknowledge?unit=${unitId}`);
  if (!response.ok) {
    declarative.textContent = 'Unable to load key knowledge.';
    procedural.textContent = '';
    return;
  }

  const data = await response.json();

  declarative.replaceChildren();
  if (data.declarativeKnowledge?.length > 0) {
    const list = document.createElement('ul');
    list.className = 'key-knowledge-list';
    for (const item of data.declarativeKnowledge) {
      const li = document.createElement('li');
      appendKnowledgeItem(li, item, data.images);
      list.appendChild(li);
    }
    declarative.appendChild(list);
  }

  procedural.replaceChildren();
  if (data.proceduralKnowledge?.length > 0) {
    const list = document.createElement('ul');
    list.className = 'key-knowledge-list';
    for (const item of data.proceduralKnowledge) {
      const li = document.createElement('li');
      appendKnowledgeItem(li, item, data.images);
      list.appendChild(li);
    }
    procedural.appendChild(list);
  }

  if (data.revisionQuiz?.length > 0) {
    const button = document.createElement('button');
    button.textContent = 'Help me revise';
    button.addEventListener('click', startQuiz);
    document.getElementById('declarative-header-container').appendChild(button);
    state.quizQuestions = data.revisionQuiz;
    state.remainingQuestions = [];
  }

  await MathJax.typesetPromise([declarative, procedural]);
}

function appendKnowledgeItem(element, item, images) {
  const content = String(item ?? '');
  const pattern = /\[img:(\d+)\]/g;
  let cursor = 0;
  let match;

  while ((match = pattern.exec(content)) !== null) {
    if (match.index > cursor) {
      element.appendChild(document.createTextNode(content.slice(cursor, match.index)));
    }

    const image = images?.find(entry => String(entry.index) === match[1]);
    if (image) {
      const imageElement = document.createElement('img');
      imageElement.alt = 'Exemplification';
      imageElement.src = image.content;
      imageElement.style.width = `${image.width * 2}mm`;
      imageElement.style.verticalAlign = 'middle';
      element.appendChild(imageElement);
    }

    cursor = match.index + match[0].length;
  }

  if (cursor < content.length || content.length === 0) {
    element.appendChild(document.createTextNode(content.slice(cursor)));
  }
}

function parseRouteSegments(segments) {
  const parts = segments.filter(segment => segment.length > 0);
  if (parts.length === 0) {
    return {};
  }

  const courseId = decodeSegment(parts[0]);
  if (!courseId) {
    return { invalid: true };
  }

  if (parts.length === 1) {
    return { courseId };
  }

  const unitId = decodeSegment(parts[1]);
  if (!unitId) {
    return { invalid: true };
  }

  if (parts.length === 2) {
    return { courseId, unitId };
  }

  if (parts.length === 3 && parts[2] === 'quiz') {
    return { courseId, unitId, action: 'quiz' };
  }

  return { invalid: true };
}

function parseHashRoute(hash) {
  if (!hash || hash.length < 2) {
    return null;
  }

  let normalized = hash.startsWith('#') ? hash.slice(1) : hash;
  if (normalized.startsWith('/')) {
    normalized = normalized.slice(1);
  }

  return parseRouteSegments(normalized.split('/'));
}

function parsePathRoute(pathname) {
  if (!pathname || pathname === '/' || pathname === coursesRootPath) {
    return {};
  }

  let normalized = pathname;
  while (normalized.endsWith('/') && normalized.length > 1) {
    normalized = normalized.slice(0, -1);
  }

  if (!normalized.startsWith(coursesPathPrefix)) {
    return { invalid: true };
  }

  const suffix = normalized.slice(coursesPathPrefix.length);
  return parseRouteSegments(suffix.split('/'));
}

async function navigateToRoute(route) {
  if (!route || route.invalid || !route.courseId) {
    showCourseList({ skipHistory: true });
    return coursesRootPath;
  }

  const listItem = document.querySelector(`.course-item[data-course-id="${route.courseId}"]`);
  if (!listItem) {
    showCourseList({ skipHistory: true });
    return coursesRootPath;
  }

  listItem.scrollIntoView({ block: 'nearest' });

  const shouldShowCourse = state.courseId !== route.courseId
    || !elements.courseDetail.classList.contains('active')
    || elements.unitDetail.classList.contains('active')
    || elements.unitQuiz.classList.contains('active');

  if (shouldShowCourse) {
    showCourse(route.courseId, { skipHistory: true });
  }

  if (!route.unitId) {
    return buildCurriculumPath(route.courseId);
  }

  const unit = unitById(route.unitId);
  if (!unit || unit.courseId !== route.courseId) {
    showCourse(route.courseId, { skipHistory: true });
    return buildCurriculumPath(route.courseId);
  }

  await showUnit(route.unitId, { skipHistory: true });

  if (route.action === 'quiz') {
    startQuiz(true);
    return buildCurriculumPath(route.courseId, route.unitId, 'quiz');
  }

  return buildCurriculumPath(route.courseId, route.unitId);
}

async function syncFromLocation() {
  const hashRoute = parseHashRoute(window.location.hash);
  if (hashRoute) {
    const canonical = await navigateToRoute(hashRoute);
    updateDisplayPath(canonical, true);
    return;
  }

  const pathRoute = parsePathRoute(window.location.pathname);
  const canonical = await navigateToRoute(pathRoute);
  if (canonical !== window.location.pathname || window.location.search || window.location.hash) {
    updateDisplayPath(canonical, true);
  } else {
    updateLoginPath();
  }
}

function startQuiz(skipHistoryUpdate = false) {
  if (!state.unitId || state.quizQuestions.length === 0) {
    return;
  }

  const unit = unitById(state.unitId);
  if (!unit) {
    return;
  }

  if (state.remainingQuestions.length === 0) {
    state.remainingQuestions = state.quizQuestions
      .map(question => [question, Math.random()])
      .sort((a, b) => a[1] - b[1])
      .map(([question]) => question);
  }

  elements.quizTitle.textContent = unit.title;
  showQuestion();
  elements.progress.style.width = `${(state.quizQuestions.length - state.remainingQuestions.length) / state.quizQuestions.length * 100}%`;

  if (!skipHistoryUpdate) {
    updateDisplayPath(buildCurriculumPath(state.courseId, state.unitId, 'quiz'));
  } else {
    updateLoginPath();
  }

  elements.unitQuiz.scrollTop = 0;
  elements.unitDetail.classList.remove('active');
  elements.unitQuiz.classList.add('active');
  elements.quizPlay.classList.remove('hide');
  elements.quizResult.classList.add('hide');
}
function showQuestion() {
  const question = state.remainingQuestions[0];
  elements.question.textContent = question.question;

  const firstCorrect = Math.random() < 0.5;
  elements.answer1.textContent = firstCorrect ? question.correctAnswer : question.incorrectAnswer;
  elements.answer1.dataset.correct = String(firstCorrect);
  elements.answer1.classList.remove('correct', 'incorrect', 'correct-outline');
  elements.answer1.disabled = false;

  elements.answer2.textContent = firstCorrect ? question.incorrectAnswer : question.correctAnswer;
  elements.answer2.dataset.correct = String(!firstCorrect);
  elements.answer2.classList.remove('correct', 'incorrect', 'correct-outline');
  elements.answer2.disabled = false;

  MathJax.typesetPromise([elements.question, elements.answer1, elements.answer2]);
  elements.outcome.textContent = '';
}

const correctResponses = ['Well done!', 'Spot on!', 'Nice job!', 'Correct!', 'Great job!', 'Brilliant!', 'Perfect!', 'Excellent!', 'Right on!', 'Exactly!', 'Superb!', 'You got it!', 'Bingo!', 'Fantastic!', 'Outstanding!', 'Nailed it!', 'Terrific!', 'Good work!', 'Ace!', 'Top notch!', 'Bravo!', 'Impressive!', 'Yes!', 'Great!', 'Amazing!', 'You rock!', 'Super!', 'On point!', 'Right!', 'Lovely!', 'Marvellous!', 'Aced it!', 'First class!', 'Bullseye!', 'Got it!', 'Magnificent!', 'Splendid!', 'Cool!', 'Smashing!', 'Thumbs up!', 'Nice!', 'Sweet!', 'Way to go!', 'High five!', 'Fab!', 'Class!', 'Good one!', 'Awesome!'];
const incorrectResponses = ['Incorrect!', 'Not right!', 'Oops!', 'Missed it!', 'Think again!', 'Nope!', 'Wrong!', 'Sorry!'];

async function answer(button) {
  elements.answer1.disabled = true;
  elements.answer2.disabled = true;

  const correct = button.dataset.correct === 'true';
  const otherButton = button === elements.answer1 ? elements.answer2 : elements.answer1;
  const answered = state.remainingQuestions.shift();

  if (correct) {
    button.classList.add('correct');
    elements.outcome.replaceChildren();

    const outcome = clone('tpl-outcome');
    qs('.material-symbols-outlined', outcome).textContent = 'thumb_up';
    qs('.outcome-text', outcome).textContent = correctResponses[Math.floor(Math.random() * correctResponses.length)];

    elements.outcome.appendChild(outcome);
    elements.outcome.style.color = 'var(--success)';
    elements.progress.style.width = `${(state.quizQuestions.length - state.remainingQuestions.length) / state.quizQuestions.length * 100}%`;
    await new Promise(resolve => setTimeout(resolve, 1000));
  } else {
    button.classList.add('incorrect');
    elements.outcome.replaceChildren();

    const outcome = clone('tpl-outcome');
    qs('.material-symbols-outlined', outcome).textContent = 'close';
    qs('.outcome-text', outcome).textContent = incorrectResponses[Math.floor(Math.random() * incorrectResponses.length)];

    elements.outcome.appendChild(outcome);
    elements.outcome.style.color = 'var(--danger)';
    otherButton.classList.add('correct-outline');

    const remaining = state.remainingQuestions.length;
    const start = Math.ceil(remaining / 2);
    const insertIndex = start + Math.floor(Math.random() * (remaining - start + 1));
    state.remainingQuestions.splice(insertIndex, 0, answered);
    await new Promise(resolve => setTimeout(resolve, 2000));
  }

  if (state.remainingQuestions.length > 0) {
    showQuestion();
    return;
  }

  elements.quizPlay.classList.add('hide');
  elements.quizResult.classList.remove('hide');
  celebrate();
}

function backToUnit() {
  elements.unitQuiz.classList.remove('active');
  elements.unitDetail.classList.add('active');
  updateDisplayPath(buildCurriculumPath(state.courseId, state.unitId));
}

function celebrate() {
  const old = document.getElementById('celebration-overlay');
  if (old) {
    old.remove();
  }

  const overlay = document.createElement('div');
  overlay.id = 'celebration-overlay';

  const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const confettiCount = document.documentElement.clientWidth / (prefersReducedMotion ? 15 : 5);
  const burstCount = prefersReducedMotion ? 8 : 24;

  const fragment = document.createDocumentFragment();

  const addWave = delayOffset => {
    for (let i = 0; i < confettiCount; i++) {
      const confetti = document.createElement('i');
      confetti.className = 'celebration-confetti';
      confetti.style.left = `${Math.random() * 100}vw`;
      confetti.style.setProperty('--h', Math.floor(Math.random() * 360));
      confetti.style.setProperty('--rot', `${Math.floor(Math.random() * 360)}deg`);
      confetti.style.setProperty('--sz', `${6 + Math.random() * 12}px`);
      confetti.style.setProperty('--drift', `${-30 + Math.random() * 60}vw`);
      confetti.style.setProperty('--dur', `${(prefersReducedMotion ? 2.4 : 3.6) + Math.random() * (prefersReducedMotion ? 1.2 : 2.2)}s`);
      confetti.style.setProperty('--delay', `${delayOffset + Math.random() * (prefersReducedMotion ? 0.5 : 1.1)}s`);
      confetti.style.setProperty('--rad', Math.random() < 0.3 ? '50%' : '3px');
      fragment.appendChild(confetti);
    }

    for (let i = 0; i < burstCount; i++) {
      const burst = document.createElement('div');
      burst.className = 'celebration-burst';
      burst.style.left = `${10 + Math.random() * 80}vw`;
      burst.style.top = `${20 + Math.random() * 50}vh`;
      burst.style.setProperty('--h', Math.floor(Math.random() * 360));
      burst.style.setProperty('--delay', `${delayOffset + Math.random() * 0.8}s`);

      for (let j = 0; j < 12; j++) {
        const spark = document.createElement('span');
        spark.className = 'spark';
        spark.style.setProperty('--i', j);
        burst.appendChild(spark);
      }

      fragment.appendChild(burst);
    }
  };

  addWave(0);
  addWave(prefersReducedMotion ? 0.6 : 1.2);

  const glow = document.createElement('div');
  glow.className = 'celebration-glow';
  fragment.appendChild(glow);

  overlay.appendChild(fragment);
  document.body.appendChild(overlay);

  setTimeout(() => overlay.classList.add('fade-out'), prefersReducedMotion ? 2000 : 3000);
  setTimeout(() => overlay.remove(), prefersReducedMotion ? 3000 : 4000);
}

function onCourseDetailClick(event) {
  const button = event.target.closest('.edit-button');
  if (!button) {
    return;
  }

  const { course, unit, property } = button.dataset;
  if (property === 'delete') {
    deleteUnit(course, unit);
    return;
  }

  openEditModal(course, unit, property);
}

async function deleteUnit(courseId, unitId) {
  if (!confirm('Are you sure you want to delete this unit? This action cannot be undone.')) {
    return;
  }

  try {
    await request(`/courses/${courseId}/${unitId}/build`, 'DELETE');
    const index = units.findIndex(unit => unit.id === unitId);
    if (index >= 0) {
      units.splice(index, 1);
    }

    showCourse(courseId, { keepEditMode: true, skipHistory: true });
  } catch (error) {
    alert(error.message);
  }
}

function openEditModal(courseId, unitId, property) {
  const course = courseById(courseId);
  const unit = unitId ? unitById(unitId) : null;
  const config = modalConfig[property];
  if (!config) {
    return;
  }

  elements.modalTitle.textContent = typeof config.title === 'function' ? config.title(unitId) : config.title || unit?.title || '';
  elements.modalQuestion.innerHTML = config.question;
  elements.modalExample.textContent = config.example || '';
  elements.textBoxContainer.classList.toggle('hide', config.input !== 'text');
  elements.selectContainer.classList.toggle('hide', config.input !== 'select');
  elements.modalChecklist.classList.toggle('hide', config.input !== 'checklist');
  elements.modalChecklist.replaceChildren();

  if (config.input === 'select') {
    elements.modalSelect.value = unit?.term || 'Autumn';
  } else if (config.input === 'checklist') {
    elements.modalChecklist.appendChild(buildChecklistEditor(unit?.checklist || ''));
  } else {
    elements.modalText.value = property === 'intent'
      ? (course?.intent || '')
      : property === 'specification'
        ? (course?.specification || '')
        : property === 'assignment-length'
          ? String(course?.assignmentLength ?? 0)
          : (unit?.[fields[property]] || '');
  }

  elements.modalSave.dataset.course = courseId;
  elements.modalSave.dataset.unit = unitId || '';
  elements.modalSave.dataset.property = property;
  elements.modalSave.disabled = false;
  elements.modal.classList.add('active');
  if (config.input === 'select') {
    elements.modalSelect.focus();
  } else if (config.input === 'checklist') {
    elements.modalChecklist.querySelector('input[type="radio"]:checked, input[type="radio"]')?.focus();
  } else {
    elements.modalText.focus();
  }
}

function validateDocumentUrl(url) {
  if (url.includes('"')) {
    alert('The link contains invalid characters. Please ensure you copy the link correctly without quotes.');
    return false;
  }

  const testUrl = url
    .replace('/:b:', '')
    .replace('/:w:', '')
    .replace('/:i:', '')
    .replace('/:v:', '')
    .replace('/:t:', '')
    .replace('/:u:', '')
    .replace('/:x:', '');

  const sharePointSitesRoot = `https://${microsoftSharePointSubdomain}.sharepoint.com/r/sites/`;
  const sharePointRoot = `https://${microsoftSharePointSubdomain}.sharepoint.com`;
  const oneDriveRoot = `https://${microsoftSharePointSubdomain}-my.sharepoint.com/`;
  if (!testUrl.startsWith(sharePointSitesRoot)) {
    if (testUrl.startsWith(oneDriveRoot)) {
      alert('You cannot link to a file in your personal OneDrive. Please save it in your department Teams folder and try again.');
    } else if (testUrl.startsWith(sharePointRoot)) {
      alert('This link is invalid. You must ensure that "People with existing access can use the link" is selected.');
    } else {
      alert('This is not a valid link to a document in your department Teams folder. Please check the link and try again.');
    }
    return false;
  }

  if (!url.includes('.docx?') && !url.includes('.xlsx?')) {
    alert('Only Microsoft Word or Excel documents are accepted. Please copy the link to a valid .docx or .xlsx file.');
    return false;
  }

  return true;
}

async function onSave() {
  elements.modalSave.disabled = true;

  const { course, unit, property } = elements.modalSave.dataset;
  try {
    if (property === 'new') {
      await createUnit(course, Number(unit), elements.modalText.value.trim());
      return;
    }

    const value = property === 'term'
      ? elements.modalSelect.value
      : property === 'checklist'
        ? readChecklistEditorValue()
        : elements.modalText.value.trim();
    if (property === 'assignment-length' && !/^\d{1,2}$/.test(value)) {
      alert('Please enter a whole number from 0 to 99.');
      elements.modalSave.disabled = false;
      return;
    }

    if (['scheme-url', 'assessment-url', 'mark-scheme-url'].includes(property) && value && !validateDocumentUrl(value)) {
      elements.modalSave.disabled = false;
      return;
    }

    if (property === 'intent' || property === 'specification' || property === 'assignment-length') {
      await request(`/courses/${course}/build/${property}`, 'PUT', { value });
      courseById(course)[fields[property]] = property === 'assignment-length' ? Number(value) : value;
    } else {
      await request(`/courses/${course}/${unit}/build/${property}`, 'PUT', { value });
      unitById(unit)[fields[property]] = value;
    }

    elements.modal.classList.remove('active');
    showCourse(course, { keepEditMode: true, skipHistory: true });
  } catch (error) {
    alert(error.message);
  } finally {
    elements.modalSave.disabled = false;
  }
}

async function createUnit(courseId, yearGroup, title) {
  if (!title) {
    alert('Please enter a title for the new unit.');
    elements.modalSave.disabled = false;
    return;
  }

  const newUnit = await request(`/courses/${courseId}/build`, 'POST', { yearGroup, title: title.trim() });
  units.push(newUnit);
  elements.modal.classList.remove('active');
  showCourse(courseId, { keepEditMode: true, skipHistory: true });
}

async function sortUnits(event) {
  const order = [...event.from.querySelectorAll('.unit-item')].map(item => item.dataset.id);
  try {
    await request(`/courses/${state.courseId}/build/sort-units`, 'PUT', { order });
    order.forEach((unitId, index) => {
      const unit = unitById(unitId);
      if (unit) {
        unit.order = index;
      }
    });
  } catch (error) {
    alert(`Failed to sort units: ${error.message}`);
    showCourse(state.courseId, { keepEditMode: true, skipHistory: true });
  }
}

async function request(url, method, value) {
  if (!csrfToken) {
    throw new Error('Missing anti-forgery token.');
  }

  const response = await fetch(url, {
    method,
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken
    },
    body: value !== undefined ? JSON.stringify(value) : undefined
  });

  if (!response.ok) {
    throw new Error(await response.text());
  }

  try {
    return await response.json();
  } catch {
    return {};
  }
}
