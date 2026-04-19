function renderAssessment() {
  renderKeyKnowledge();
  renderQuiz();

  const container = document.getElementById('assessment-content');
  container.innerHTML = '';

  if (keyKnowledge.images?.length > 0) {
    const kkiContainer = document.getElementById('kki');
    for (const image of keyKnowledge.images) {
      const div = document.createElement('div');
      div.className = 'image-container';
      div.dataset.index = image.index;
      div.innerHTML = `<div class="ref">Refer to the image below as <b>[img:${image.index}]</b></div><img src="${image.content}" class="question-image" style="width: ${image.width}mm">` +
        '<div class="kki-side-buttons"><button class="material-symbols-outlined remove-image">hide_image</button><button class="decrease-image">-</button>' +
        '<button class="increase-image">+</button></div >';
      kkiContainer.appendChild(div);
    }
  }

  if (assessment.sections.length === 0) {
    assessment.sections.push({ title: 'Recap', questions: [] });
    assessment.sections.push({ title: 'Knowledge', questions: [] });
    assessment.sections.push({ title: 'Application', questions: [] });
  }

  const q = { number: 1 };
  assessment.sections.forEach((section, index) => {
    const sectionElement = createSection(section, index, q);
    container.appendChild(sectionElement);
  });
  updateMarkTotals();
}

function renderQuiz() {
  const container = document.getElementById('quiz-content');
  if (!container) return;

  container.innerHTML = '';
  questionBank.questions ??= [];
  container.appendChild(createSection({ questions: questionBank.questions }, 0, { number: 1 }, { quiz: true, showHeader: false }));
}

function renderKeyKnowledge() {
  const declarativeList = document.getElementById('declarative-list');
  declarativeList.innerHTML = '';
  for (const item of keyKnowledge.declarativeKnowledge) {
    const li = document.createElement('li');
    li.textContent = item;
    li.dataset.text = item;
    declarativeList.appendChild(li);
  }
  if (keyKnowledge.declarativeKnowledge.length === 0) declarativeList.innerHTML = '<li></li>';

  const proceduralList = document.getElementById('procedural-list');
  proceduralList.innerHTML = '';
  for (const item of keyKnowledge.proceduralKnowledge) {
    const li = document.createElement('li');
    li.textContent = item;
    li.dataset.text = item;
    proceduralList.appendChild(li);
  }
  if (keyKnowledge.proceduralKnowledge.length === 0) proceduralList.innerHTML = '<li></li>';
}

function createSection(section, sectionIndex, q, options) {
  options ??= {};
  const sectionDiv = document.createElement('div');
  sectionDiv.dataset.index = sectionIndex;
  sectionDiv.className = 'section';

  const table = document.createElement('table');
  table.className = 'section-table';

  if (options.showHeader !== false) {
    const sectionLetter = String.fromCharCode(65 + sectionIndex);
    const headerRow = document.createElement('tr');
    const headerCell = document.createElement('td');
    headerCell.className = 'section-header';
    headerCell.colSpan = 5;
    headerCell.innerHTML = `Section ${sectionLetter}: <span class="section-title">${escapeHtml(section.title)}</span> (<span class="section-total"></span>)`;
    headerRow.appendChild(headerCell);
    table.appendChild(headerRow);
  }

  section.questions.forEach(question => {
    const questionFragment = createQuestionRow(question, q, options);
    table.appendChild(questionFragment);
  });

  sectionDiv.appendChild(table);

  const addQuestionButtons = document.createElement('div');
  addQuestionButtons.className = 'add-question-buttons';
  addQuestionButtons.innerHTML = options.quiz
    ? '<button class="add-question feature-button mc-type"><span class="material-symbols-outlined">add</span> Add multiple-choice question</button>' +
      '<button class="add-question feature-button generate-quiz"><span class="material-symbols-outlined">neurology</span> Replace all questions from key knowledge</button>'
    : '<button class="add-question feature-button mc-type"><span class="material-symbols-outlined">add</span> Add multiple-choice question</button>' +
      '<button class="add-question feature-button"><span class="material-symbols-outlined">add</span> Add written question</button>' +
      (section.title.startsWith('Knowledge') ? '<button class="add-question feature-button generate-questions"><span class="material-symbols-outlined">neurology</span> Generate questions from key knowledge</button>' : '');
  sectionDiv.appendChild(addQuestionButtons);

  return sectionDiv;
}

function createQuestionRow(question, q, options) {
  options ??= {};
  question.id ??= crypto.randomUUID();
  const fragment = document.createDocumentFragment();

  const questionRow = document.createElement('tr');
  questionRow.dataset.id = question.id;
  questionRow.className = 'question-row';
  questionRow.classList.toggle('not-displayed', !options.quiz && question.marks === 0);

  const questionNumber = document.createElement('td');
  questionNumber.className = 'question-number';
  questionNumber.rowSpan = 2;
  if (options.quiz || question.marks > 0) {
    questionNumber.textContent = q.number;
    q.number++;
  }

  const questionContent = document.createElement('td');
  questionContent.className = 'question-content' + (!options.quiz && question.marks === 0 ? ' zero-marks' : '');

  const modifyQuestionHtml = options.quiz
    ? '<div class="side-buttons modify-question-buttons"><button class="material-symbols-outlined delete-question" title="Delete question">delete</button></div>'
    : `<div class="side-buttons modify-question-buttons"><button class="material-symbols-outlined delete-question" title="Delete question">delete</button>` +
      `<button class="material-symbols-outlined move-up" title="Move question up">arrow_upward</button>` +
      `<button class="material-symbols-outlined move-down" title="Move question down">arrow_downward</button></div>`;

  if (options.quiz) {
    questionContent.innerHTML = `<span class="question" data-text="${escapeAttribute(question.question)}">${escapeHtml(question.question)}</span>${modifyQuestionHtml}`;
  } else {
    const imageHtml = question.image ? `<div class="image-container"><img src="${question.image}" class="question-image" style="width: ${question.imageWidth}mm"><div class="side-buttons">` +
      '<button class="material-symbols-outlined remove-image" title="Remove image">hide_image</button><button class="decrease-image" title="Decrease image size">-</button>' +
      '<button class="increase-image" title="Increase image size">+</button></div></div>' : '';

    const listItems = !question.successCriteria || question.successCriteria.length === 0 ? '<li></li>' : question.successCriteria.map(item => `<li data-text="${escapeAttribute(item)}">${escapeHtml(item)}</li>`).join('');
    const successCriteriaHtml = question.successCriteria ? `<div class="success-criteria-container">Success criteria: <button class="feature-button material-symbols-outlined remove-success-criteria">close</button><ul class="success-criteria">${listItems}</ul></div>` : '';

    questionContent.innerHTML = `${imageHtml}<span class="question" data-text="${escapeAttribute(question.question)}">${escapeHtml(question.question)}</span> <button class="feature-button material-symbols-outlined add-image${question.image ? ' hide' : ''}">image</button><button class="feature-button material-symbols-outlined add-success-criteria${question.successCriteria || question.answers ? ' hide' : ''}">checklist</button><button class="feature-button material-symbols-outlined hide-answer-space${question.answers ? ' hide' : ''}">${question.answerSpaceFormat ? (question.answerSpaceFormat === 1 ? 'visibility' : 'format_align_justify') : 'visibility_off'}</button><span class="marks-container">(<span class="marks">${question.marks}</span> <span class="marks-suffix">${question.marks === 1 ? 'mark' : `marks`}</span>)</span>${successCriteriaHtml}${modifyQuestionHtml}`;
  }

  questionContent.colSpan = 4;

  questionRow.appendChild(questionNumber);
  questionRow.appendChild(questionContent);
  fragment.appendChild(questionRow);

  const answerRow = document.createElement('tr');
  answerRow.dataset.id = question.id;
  answerRow.className = 'answer-row';

  if (options.quiz || question.answers) {
    const answers = options.quiz
      ? [question.correctAnswer, question.incorrectAnswer1, question.incorrectAnswer2, question.incorrectAnswer3]
      : question.answers;
    answers.forEach((answer, index) => {
      const answerCell = document.createElement('td');
      answerCell.className = 'choice-cell';
      const choiceLabel = options.quiz
        ? `<span class="material-symbols-outlined choice-icon">${index === 0 ? 'check' : 'close'}</span>`
        : `${String.fromCharCode(97 + index)}.`;
      answerCell.innerHTML = `<b>${choiceLabel}</b> <span class="answer" data-text="${escapeAttribute(answer)}">${escapeHtml(answer)}</span>`;
      answerRow.appendChild(answerCell);

      if (!options.quiz) {
        const isCorrect = question.markScheme && question.markScheme.charCodeAt(0) - 97 === index;
        const overlay = document.createElement('div');
        overlay.className = 'mark-scheme choice-overlay' + (isCorrect ? ' correct' : '');
        answerCell.appendChild(overlay);
        if (index === 3) {
          const sideDiv = document.createElement('div');
          sideDiv.className = 'ms-side-buttons';
          sideDiv.innerHTML = '<button class="shuffle material-symbols-outlined" title="Shuffle answers">compare_arrows</button><button class="generate-mark-scheme material-symbols-outlined" title="Generate mark scheme">wand_stars</button>';
          answerCell.appendChild(sideDiv);
        }
      }
    });
  } else {
    const answerCell = document.createElement('td');
    answerCell.className = 'answer-space-cell';
    answerCell.classList.toggle('not-displayed', question.answerSpaceFormat === 1 || question.marks === 0);
    answerCell.classList.toggle('no-lines', question.answerSpaceFormat === 2);
    answerCell.colSpan = 4;
    if (question.lines < 6) answerCell.classList.add('short-answer');
    for (let i = 0; i < question.lines; i++) {
      const answerLine = document.createElement('hr');
      answerLine.className = 'answer-line';
      answerCell.appendChild(answerLine);
    }
    answerRow.appendChild(answerCell);

    const overlay = document.createElement('div');
    overlay.className = 'mark-scheme text-overlay';
    overlay.textContent = question.markScheme;
    overlay.dataset.text = question.markScheme;
    answerCell.appendChild(overlay);

    const sizeButtons = document.createElement('div');
    sizeButtons.className = 'side-buttons';
    sizeButtons.innerHTML = `<button class="decrease-size" title="Decrease lines">-</button><button class="increase-size" title="Increase lines">+</button>`;
    answerCell.appendChild(sizeButtons);

    const sideDiv = document.createElement('div');
    sideDiv.className = 'ms-side-buttons';
    sideDiv.innerHTML = '<button class="generate-mark-scheme material-symbols-outlined" title="Generate mark scheme">wand_stars</button>';
    answerCell.appendChild(sideDiv);
  }

  fragment.appendChild(answerRow);
  return fragment;
}

function updateMarkTotals() {
  const sectionTotals = document.querySelectorAll('.section-total');
  let totalMarks = 0;
  sectionTotals.forEach((totalCell, index) => {
    const section = assessment.sections[index];
    const sectionMarks = section.questions.reduce((sum, q) => sum + (q.marks ?? 0), 0);
    totalCell.textContent = `${sectionMarks} mark${sectionMarks !== 1 ? 's' : ''}`;
    totalMarks += sectionMarks;
  });
  const assessmentTotal = document.querySelector('.assessment-total');
  assessmentTotal.textContent = totalMarks;
}

function showKeyKnowledgeImages() {
  const listItems = document.querySelectorAll('#key-knowledge-content li');
  for (const li of listItems) {
    const matches = [...li.textContent.matchAll(/\[img:(\d+)\]/g)];
    if (matches.length > 0) {
      let cleanText = li.textContent;
      for (const m of matches) {
        cleanText = cleanText.replace(m[0], '');
      }
      li.textContent = cleanText;
      for (const m of matches) {
        const index = parseInt(m[1], 10);
        const image = keyKnowledge.images.find(img => img.index === index);
        if (image) {
          const imgElement = document.createElement('img');
          imgElement.src = image.content;
          imgElement.className = 'kki-image-rendered';
          imgElement.style.width = `${image.width}mm`;
          li.appendChild(imgElement);
        }
      }
    }
  }
}

function escapeHtml(str) {
  return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function escapeAttribute(str) {
  return escapeHtml(str).replace(/"/g, '&quot;');
}
