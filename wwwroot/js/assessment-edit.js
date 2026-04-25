const invalidWhitespace = /[\f\t\v\r\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]/g;
const repeatedSpaces = / {2,}/g;
const repeatedNewLines = /\n{3,}/g;
const trailingSpaces = / +$/gm;
const fancySingleQuotes = /[\u2018\u2019]/g;
const fancyDoubleQuotes = /[\u201C\u201D]/g;
const cleanText = text => text.replace(fancySingleQuotes, "'").replace(fancyDoubleQuotes, '"').replace(invalidWhitespace, ' ').replace(repeatedSpaces, ' ')
  .replace(trailingSpaces, '').replace(repeatedNewLines, '\n\n').trim();

let editMode = false;

window.addEventListener('beforeunload', function (event) { if (editMode) event.preventDefault(); });
document.getElementById('kki-add').addEventListener('click', uploadKeyKnowledgeImage);
document.getElementById('import-assessment').addEventListener('click', showModal);
document.getElementById('import-keyknowledge').addEventListener('click', showModal);
document.getElementById('modal-close').addEventListener('click', closeModal);

function initMode(root) {
  root ??= document;
  const editingSchemeOfWork = editMode && tab === 'scheme';
  const editingKeyKnowledge = editMode && tab === 'key-knowledge';
  const editingAssessment = editMode && tab === 'assessment';
  const editingQuiz = editMode && tab === 'quiz';
  const editingQuestions = editingAssessment || editingQuiz;
  const editingMarkScheme = editMode && tab === 'mark-scheme';

  schemeBuilder?.setEditMode(editingSchemeOfWork);

  if (!editMode) showKeyKnowledgeImages();
  root.querySelectorAll('#declarative-list, #procedural-list').forEach(el => toggleListEditing(el, editingKeyKnowledge));
  root.querySelectorAll('.kki-side-buttons').forEach(el => toggleKkiSideButtons(el, editingKeyKnowledge));

  root.querySelectorAll('.question,.answer,.marks,.section-title').forEach(el => toggleTextEditing(el, editingQuestions));
  root.querySelectorAll('.success-criteria').forEach(el => toggleListEditing(el, editingAssessment));
  root.querySelectorAll('.side-buttons').forEach(el => toggleSideButtons(el, editingQuestions));
  root.querySelectorAll('.feature-button').forEach(el => toggleFeatureEditing(el, editingQuestions));
  root.querySelectorAll('.add-question').forEach(el => toggleAddQuestionButton(el, editingQuestions));

  root.querySelectorAll('.text-overlay').forEach(el => toggleTextEditing(el, editingMarkScheme));
  root.querySelectorAll('.choice-overlay').forEach(el => toggleAnswerSelection(el, editingMarkScheme));
  root.querySelectorAll('.ms-side-buttons').forEach(el => toggleSideButtons(el, editingMarkScheme));

  document.getElementById('complete-section').classList.toggle('hide', editMode || tab === 'scheme');
  const isSectionComplete = tab === 'key-knowledge' ? isKeyKnowledgeComplete : tab === 'quiz' ? isQuizComplete : tab === 'scheme' ? false : isAssessmentComplete;
  document.getElementById('btn-complete').classList.toggle('hide', isSectionComplete);
  document.getElementById('complete-text').classList.toggle('hide', !isSectionComplete);
  if (tab !== 'scheme') {
    document.getElementById('complete-text-content').textContent = tab === 'key-knowledge' ? 'Key knowledge complete' : tab === 'quiz' ? 'Quiz complete' : 'Assessment complete';
  }

  document.querySelectorAll('.not-displayed').forEach(el => el.classList.toggle('crossed', editingAssessment));
  document.querySelectorAll('.not-displayed').forEach(el => el.classList.toggle('display', tab === 'mark-scheme'));
  document.querySelectorAll('.zero-marks .marks-container').forEach(el => el.classList.toggle('hide', !editingAssessment));

  document.getElementById('kki').classList.toggle('hide', !editMode);

  const showImport = editingAssessment && assessment.sections.every(o => o.questions.length === 0);
  document.getElementById('import-assessment').classList.toggle('hide', !showImport);

  document.getElementById('import-keyknowledge').classList.toggle('hide', !editingKeyKnowledge);

  document.getElementById('assessment-content').classList.toggle('mono', tab === 'assessment' && !editMode);
  document.getElementById('quiz-content').classList.toggle('mono', tab === 'quiz' && !editMode);

  if (!editMode) {
    MathJax.typesetPromise([...document.querySelectorAll('.question,.text-overlay,.answer')]);
  } else {
    document.querySelectorAll('.question,.text-overlay,.answer').forEach(el => { el.textContent = el.dataset.text; });
  }

}

function handleTextKeyDown(event) {
  if (event.key === 'Enter' && event.ctrlKey) {
    event.preventDefault();
    const element = event.target;
    element.blur();
    const question = findQuestionFromElement(element);
    const buttonClass = isQuizElement(element) || question.answers ? '.add-question.mc-type' : '.add-question:not(.mc-type)';
    const addButton = element.closest('.section').querySelector(buttonClass);
    addButton.click();
  }
}

function toggleListEditing(list, isActive) {
  list.contentEditable = isActive;
  list.classList.toggle('editable', isActive);
  if (isActive) {
    list.addEventListener('keydown', handleListKeyDown);
    list.addEventListener('blur', handleListBlur);
    list.querySelectorAll('li').forEach(item => item.textContent = item.dataset.text);
    if (list.id === 'declarative-list' || list.id === 'procedural-list') list.addEventListener('paste', handleKkiPaste);
  } else {
    list.removeEventListener('keydown', handleListKeyDown);
    list.removeEventListener('blur', handleListBlur);
    list.removeEventListener('paste', handleKkiPaste);
    MathJax.typesetPromise([list]);
  }
}

function handleListKeyDown(event) {
  const selection = window.getSelection();
  const node = selection.anchorNode;
  if (!node) return;
  const listItem = node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement;
  if (!listItem || listItem.tagName !== 'LI') return;
  const ul = listItem.parentElement;
  if (event.key === 'Backspace' && ul.childElementCount === 1 && listItem.textContent.trim() === '') {
    event.preventDefault();
  }
}

function handleListBlur(event) {
  const list = event.target;
  const items = Array.from(list.querySelectorAll('li'));
  const cleanItems = [];
  items.forEach(item => {
    item.textContent = cleanText(item.textContent);
    item.dataset.text = item.textContent;
    if (item.textContent === '') item.remove();
    else cleanItems.push(item.textContent);
  });
  if (cleanItems.length === 0) list.innerHTML = '<li></li>';
  if (list.id === 'declarative-list') keyKnowledge.declarativeKnowledge = cleanItems;
  else if (list.id === 'procedural-list') keyKnowledge.proceduralKnowledge = cleanItems;
  else findQuestionFromElement(list).successCriteria = cleanItems;
  list.classList.remove('editing');
}

function toggleTextEditing(element, isActive) {
  element.contentEditable = isActive;
  element.classList.toggle('editable', isActive);
  if (isActive) {
    element.addEventListener('blur', handleTextBlur);
    element.addEventListener('paste', handleTextPaste);
    element.addEventListener('keydown', handleTextKeyDown);
  } else {
    element.removeEventListener('blur', handleTextBlur);
    element.removeEventListener('paste', handleTextPaste);
    element.removeEventListener('keydown', handleTextKeyDown);
  }
}

function handleTextBlur(event) {
  const element = event.target;
  const text = cleanText(element.innerText);
  if (element.classList.contains('section-title')) {
    const section = assessment.sections[parseInt(element.closest('.section').dataset.index, 10)];
    if (text) section.title = text;
    element.textContent = section.title;
    return;
  }
  const question = findQuestionFromElement(element);
  if (element.classList.contains('question')) {
    question.question = text;
    element.textContent = text;
    element.dataset.text = text;
  } else if (element.classList.contains('text-overlay')) {
    question.markScheme = text;
    element.textContent = text;
    element.dataset.text = text;
    element.closest('td').querySelector('.generate-mark-scheme').classList.toggle('hide', !!text);
  } else if (element.classList.contains('answer')) {
    const answerCell = element.closest('td');
    const choiceIndex = Array.from(answerCell.parentElement.children).indexOf(answerCell);
    if (isQuizElement(element)) {
      question[['correctAnswer', 'incorrectAnswer1', 'incorrectAnswer2', 'incorrectAnswer3'][choiceIndex]] = text;
    } else {
      question.answers[choiceIndex] = text;
    }
    element.textContent = text;
    element.dataset.text = text;
  } else if (element.classList.contains('marks')) {
    const marks = parseInt(text, 10);
    if (!isNaN(marks) && (marks > 0 || (!question.answers && marks == 0))) question.marks = marks;
    element.textContent = question.marks;
    element.parentElement.querySelector('.marks-suffix').textContent = question.marks === 1 ? 'mark' : 'marks';
    element.parentElement.parentElement.classList.toggle('zero-marks', question.marks === 0);
    const row = element.closest('.question-row');
    const answerCell = row.nextElementSibling.querySelector('.answer-space-cell');
    if (!question.answers) {
      row.classList.toggle('not-displayed', question.marks === 0);
      row.classList.toggle('crossed', question.marks === 0);
      answerCell.classList.toggle('not-displayed', question.marks === 0 || question.answerSpaceFormat === 1);
      answerCell.classList.toggle('crossed', question.marks === 0 || question.answerSpaceFormat === 1);
    }
    updateMarkTotals();
    renumberQuestions(row.closest('#assessment-content'));
  }
}

function handleTextPaste(event) {
  event.preventDefault();
  const text = event.clipboardData.getData('text/plain');
  const selection = window.getSelection();
  if (!selection.rangeCount) return;
  const range = selection.getRangeAt(0);
  range.deleteContents();
  const textNode = document.createTextNode(text);
  range.insertNode(textNode);
  range.setStartAfter(textNode);
  range.collapse(true);
  selection.removeAllRanges();
  selection.addRange(range);
}

function toggleAnswerSelection(element, isActive) {
  element.classList.toggle('selectable', isActive);
  if (isActive) {
    element.addEventListener('click', handleAnswerClick);
  } else {
    element.removeEventListener('click', handleAnswerClick);
  }
}

function handleAnswerClick(event) {
  if (event.target.classList.contains('disabled')) return;
  const tr = event.target.closest('tr');
  const question = findQuestionFromElement(tr);
  tr.querySelectorAll('.choice-overlay').forEach((overlay, index) => {
    const isSelected = overlay === event.target;
    overlay.classList.toggle('correct', isSelected);
    if (isSelected) question.markScheme = String.fromCharCode(97 + index);
  });
  tr.querySelector('.generate-mark-scheme').classList.add('hide');
}

function isQuizElement(el) {
  return !!el.closest('#quiz-content');
}

function toggleSideButtons(buttons, isActive) {
  buttons.classList.toggle('selectable', isActive);
  if (isActive) {
    buttons.querySelectorAll('button').forEach(button => button.addEventListener('click', handleSideButtonClick));
  } else {
    buttons.querySelectorAll('button').forEach(button => button.removeEventListener('click', handleSideButtonClick));
  }
  buttons.querySelectorAll('.generate-mark-scheme').forEach(button => {
    if (!isActive) {
      button.classList.add('hide');
      return;
    }
    const question = findQuestionFromElement(button);
    const eligible = question.question && (!question.answers || question.answers.every(a => a.length > 0)) && question.markScheme.length === 0;
    button.classList.toggle('hide', !eligible);
  });
}

async function handleSideButtonClick(event) {
  const button = event.currentTarget ?? event.target;
  const answerCell = button.closest('td');
  const question = findQuestionFromElement(answerCell);
  if (button.classList.contains('decrease-size')) {
    if (question.lines > 1) {
      question.lines--;
      answerCell.querySelector('.answer-line').remove();
    }
    answerCell.classList.toggle('short-answer', question.lines < 6);
  } else if (button.classList.contains('increase-size')) {
    question.lines++;
    const newLine = document.createElement('hr');
    newLine.className = 'answer-line';
    answerCell.appendChild(newLine);
    answerCell.classList.toggle('short-answer', question.lines < 6);
  } else if (button.classList.contains('remove-image')) {
    if (!confirm('Are you sure you want to remove the image?')) return;
    const imageContainer = answerCell.querySelector('.image-container');
    if (imageContainer) {
      imageContainer.remove();
      delete question.image;
      delete question.imageWidth;
      answerCell.querySelector('.add-image').classList.remove('hide');
    }
  } else if (button.classList.contains('decrease-image')) {
    if (question.imageWidth > 40) {
      question.imageWidth -= 10;
      answerCell.querySelector('.question-image').style.width = `${question.imageWidth}mm`;
    }
  } else if (button.classList.contains('increase-image')) {
    if (question.imageWidth < 150) {
      question.imageWidth += 10;
      answerCell.querySelector('.question-image').style.width = `${question.imageWidth}mm`;
    }
  } else if (button.classList.contains('delete-question')) {
    if (!confirm('Are you sure you want to delete this question?')) return;
    const questionRow = answerCell.closest('tr');
    const answerRow = questionRow.nextElementSibling;
    const sectionElement = questionRow.closest('.section');
    const questions = getSectionQuestions(sectionElement);
    const questionIndex = questions.findIndex(q => q.id === questionRow.dataset.id);
    questions.splice(questionIndex, 1);
    questionRow.remove();
    answerRow.remove();
    if (!isQuizElement(button)) updateMarkTotals();
    renumberQuestions(sectionElement.parentElement);
  } else if (button.classList.contains('move-up')) {
    const questionRow = answerCell.closest('.question-row');
    const answerRow = questionRow.nextElementSibling;
    const sectionElement = questionRow.closest('.section');
    const questions = getSectionQuestions(sectionElement);
    const questionIndex = questions.findIndex(q => q.id === questionRow.dataset.id);
    if (questionIndex === 0) return;
    const thisQuestion = questions[questionIndex];
    const prevQuestion = questions[questionIndex - 1];
    questions[questionIndex - 1] = thisQuestion;
    questions[questionIndex] = prevQuestion;
    sectionElement.querySelector('table').insertBefore(questionRow, questionRow.previousElementSibling.previousElementSibling);
    sectionElement.querySelector('table').insertBefore(answerRow, answerRow.previousElementSibling.previousElementSibling);
    renumberQuestions(sectionElement.parentElement);
  } else if (button.classList.contains('move-down')) {
    const questionRow = answerCell.closest('.question-row');
    const answerRow = questionRow.nextElementSibling;
    const sectionElement = questionRow.closest('.section');
    const questions = getSectionQuestions(sectionElement);
    const questionIndex = questions.findIndex(q => q.id === questionRow.dataset.id);
    if (questionIndex === questions.length - 1) return;
    const thisQuestion = questions[questionIndex];
    const nextQuestion = questions[questionIndex + 1];
    questions[questionIndex + 1] = thisQuestion;
    questions[questionIndex] = nextQuestion;
    sectionElement.querySelector('table').insertBefore(answerRow, answerRow.nextElementSibling.nextElementSibling.nextElementSibling);
    sectionElement.querySelector('table').insertBefore(questionRow, questionRow.nextElementSibling.nextElementSibling.nextElementSibling);
    renumberQuestions(sectionElement.parentElement);
  } else if (button.classList.contains('shuffle')) {
    const question = findQuestionFromElement(button);
    shuffleQuestion(question);
    answerCell.closest('.answer-row').querySelectorAll('.choice-cell').forEach((td, index) => {
      td.querySelector('.answer').textContent = question.answers[index];
      td.querySelector('.answer').dataset.text = question.answers[index];
      td.querySelector('.choice-overlay').classList.toggle('correct', question.markScheme === String.fromCharCode(97 + index));
    });
  } else if (button.classList.contains('generate-mark-scheme')) {
    if (question.markScheme?.length > 0) return;
    button.disabled = true;
    button.textContent = 'pending';
    const row = answerCell.closest('tr');
    row.querySelectorAll('.text-overlay').forEach(el => { el.contentEditable = false; el.textContent = 'Generating...'; });
    row.querySelectorAll('.choice-overlay').forEach(el => el.classList.add('disabled'));
    const resp = await fetch(`/courses/${courseId}/build/ai/generatemarkscheme`, { 
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
      body: JSON.stringify(question)
    });
    if (resp.ok) {
      question.markScheme = await resp.json();
      if (question.answers) {
        const index = 'abcd'.indexOf(question.markScheme);
        if (index === -1) {
          alert('Invalid mark scheme generated.');
          question.markScheme = '';
        } else {
          row.querySelectorAll('.choice-overlay').forEach((overlay, i) => overlay.classList.toggle('correct', i === index));
        }
      } else {
        row.querySelector('.mark-scheme').textContent = question.markScheme;
        row.querySelector('.mark-scheme').dataset.text = question.markScheme;
      }
    } else {
      alert(`Error generating mark scheme: ${await resp.text()}`);
      row.querySelector('.mark-scheme').textContent = '';
    }
    button.textContent = 'wand_stars';
    button.disabled = false;
    button.classList.add('hide');
    row.querySelectorAll('.text-overlay').forEach(el => el.contentEditable = true);
    row.querySelectorAll('.choice-overlay').forEach(el => el.classList.remove('disabled'));
  }
}

function toggleFeatureEditing(button, isActive) {
  button.classList.toggle('selectable', isActive);
  if (isActive) {
    button.addEventListener('click', handleFeatureClick);
  } else {
    button.removeEventListener('click', handleFeatureClick);
  }
}

async function handleFeatureClick(event) {
  const button = event.currentTarget ?? event.target;
  const td = button.closest('td');
  if (button.classList.contains('add-image')) {
    const question = findQuestionFromElement(td);
    const file = await getImageUpload(question.marks > 0);
    if (!file) return;
    const div = document.createElement('div');
    div.className = 'image-container';
    div.innerHTML = `<img src="${file}" class="question-image" style="width: 100mm"><div class="side-buttons selectable"><button class="material-symbols-outlined remove-image">hide_image` +
      '</button><button class="decrease-image">-</button><button class="increase-image">+</button></div>';
    td.insertBefore(div, td.firstChild);
    toggleSideButtons(div.querySelector('.side-buttons'), true);
    question.image = file;
    question.imageWidth = 100;
    button.classList.add('hide');
  } else if (button.classList.contains('add-success-criteria')) {
    const container = document.createElement('div');
    container.className = 'success-criteria-container';
    container.innerHTML = 'Success criteria: <button class="feature-button material-symbols-outlined remove-success-criteria">close</button><ul class="success-criteria"><li></li></ul>';
    td.appendChild(container);
    toggleListEditing(td.querySelector('.success-criteria'), true);
    toggleFeatureEditing(container.querySelector('button'), true);
    button.classList.add('hide');
    container.querySelector('ul').focus();
  } else if (button.classList.contains('remove-success-criteria')) {
    const container = button.closest('.success-criteria-container');
    const question = findQuestionFromElement(container);
    if (question.successCriteria && question.successCriteria.length > 0 && !confirm('Are you sure you want to remove the success criteria?')) return;
    container.remove();
    delete question.successCriteria;
    td.querySelector('.add-success-criteria').classList.remove('hide');
    button.classList.add('hide');
  } else if (button.classList.contains('hide-answer-space')) {
    const question = findQuestionFromElement(td);
    question.answerSpaceFormat = ((question.answerSpaceFormat ?? 0) + 1) % 3;
    const answerSpace = td.closest('.question-row').nextElementSibling.querySelector('.answer-space-cell');
    answerSpace.classList.toggle('not-displayed', question.answerSpaceFormat === 1 || question.marks === 0);
    answerSpace.classList.toggle('crossed', question.answerSpaceFormat === 1 || question.marks === 0);
    answerSpace.classList.toggle('no-lines', question.answerSpaceFormat === 2);
    button.textContent = question.answerSpaceFormat === 0 ? 'visibility_off' : question.answerSpaceFormat === 1 ? 'visibility' : 'format_align_justify';
    if (question.answerSpaceFormat === 0) delete question.answerSpaceFormat;
  }
}

async function toggleAddQuestionButton(button, isActive) {
  button.classList.toggle('selectable', isActive);
  if (isActive) {
    button.addEventListener('click', handleAddQuestion);
  } else {
    button.removeEventListener('click', handleAddQuestion);
  }
}

async function handleAddQuestion(event) {
  const button = event.currentTarget ?? event.target;
  if (button.classList.contains('generate-questions') || button.classList.contains('generate-quiz')) {
    showModal({ target: button });
    return;
  }
  const sectionElement = button.closest('.section');
  const questions = getSectionQuestions(sectionElement);

  const question = isQuizElement(button)
    ? { question: '', correctAnswer: '', incorrectAnswer1: '', incorrectAnswer2: '', incorrectAnswer3: '' }
    : (button.classList.contains('mc-type'))
    ? { question: '', answers: ['', '', '', ''], marks: 1, markScheme: '' }
    : { question: '', lines: 1, marks: 1, markScheme: '' };

  questions.push(question);
  const row = createQuestionRow(question, { number: 0 }, { quiz: isQuizElement(button) });
  sectionElement.querySelector('table').appendChild(row);
  renumberQuestions(sectionElement.parentElement);
  initMode(sectionElement);
  if (!isQuizElement(button)) updateMarkTotals();
  document.getElementById('import-assessment').classList.toggle('hide', true);
  button.scrollIntoView({ behavior: 'smooth', block: 'center' });
  sectionElement.querySelector(`.question-row[data-id="${question.id}"] .question`).focus();
}

async function prepareImage(file, mono) {
  const bitmap = await createImageBitmap(file);
  const scale = Math.min(1, 1000 / Math.max(bitmap.width, bitmap.height));
  const width = Math.round(bitmap.width * scale);
  const height = Math.round(bitmap.height * scale);
  const offscreen = new OffscreenCanvas(width, height);
  const ctx = offscreen.getContext('2d');
  if (mono) ctx.filter = 'grayscale(1)';
  ctx.drawImage(bitmap, 0, 0, width, height);
  const blob = await offscreen.convertToBlob({ type: 'image/webp', quality: 0.8 });
  return new Promise(resolve => {
    const reader = new FileReader();
    reader.onloadend = () => resolve(reader.result);
    reader.readAsDataURL(blob);
  });
}

function getImageUpload(mono) {
  return new Promise(resolve => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'image/*';
    input.style.display = 'none';
    input.addEventListener('change', async () => {
      if (!input.files.length) return resolve(null);
      try {
        const file = input.files[0];
        const dataUrl = await prepareImage(file, mono);
        resolve(dataUrl);
      } catch {
        resolve(null);
      }
    }, { once: true });
    input.click();
  });
}

function renumberQuestions(root) {
  root ??= document.getElementById('assessment-content');
  let count = 1;
  root.querySelectorAll('.section').forEach(section => {
    section.querySelectorAll('.question-row:not(.not-displayed)').forEach(row => {
      row.querySelector('.question-number').textContent = count++;
    });
  });
}

function findQuestionFromElement(el) {
  const id = el.closest('tr').dataset.id;
  if (isQuizElement(el)) {
    return questionBank.questions.find(question => question.id === id) ?? null;
  }
  for (const section of assessment.sections) {
    for (const question of section.questions) {
      if (question.id === id) return question;
    }
  }
  return null;
}

async function uploadKeyKnowledgeImage() {
  const dataUrl = await getImageUpload(false);
  if (!dataUrl) return;
  addKeyKnowledgeImage(dataUrl);
}

async function handleKkiPaste(e) {
  const sel = window.getSelection();
  if (!sel.rangeCount) return;
  const range = sel.getRangeAt(0);
  const items = Array.from(e.clipboardData.items);
  const imageItem = items.find(item => item.type.startsWith('image/'));
  e.preventDefault();
  if (imageItem) {
    const file = imageItem.getAsFile();
    const dataUrl = await prepareImage(file, false);
    const index = addKeyKnowledgeImage(dataUrl);
    range.deleteContents();
    range.insertNode(document.createTextNode(` [img:${index}]`));
    range.collapse(false);
    sel.removeAllRanges();
    sel.addRange(range);
  } else {
    let lines = e.clipboardData.getData('text/plain').split(/\r?\n/);
    if (!lines.length) return;
    lines = lines.map(line => line.length > 1 && line[1] === '\t' ? line.slice(2) : line);
    let li = range.startContainer;
    while (li && li.nodeName !== 'LI') li = li.parentNode;
    const list = li.parentNode;
    range.deleteContents();
    range.insertNode(document.createTextNode(lines[0]));
    range.collapse(false);
    if (lines.length === 1) {
      sel.removeAllRanges();
      sel.addRange(range);
      return;
    }
    let prev = li;
    for (let i = 1; i < lines.length; i++) {
      const newLi = document.createElement('li');
      newLi.textContent = lines[i];
      list.insertBefore(newLi, prev.nextSibling);
      prev = newLi;
    }
    const newRange = document.createRange();
    newRange.selectNodeContents(prev);
    newRange.collapse(false);
    sel.removeAllRanges();
    sel.addRange(newRange);
  }
}

function addKeyKnowledgeImage(file) {
  keyKnowledge.images ??= [];
  const index = keyKnowledge.images.length ? Math.max(...keyKnowledge.images.map(img => img.index)) + 1 : 1;
  const container = document.getElementById('kki');
  const div = document.createElement('div');
  div.className = 'image-container';
  div.dataset.index = index;
  div.innerHTML = `<div class="ref">Refer to the image below as <b>[img:${index}]</b></div><img src="${file}" class="question-image" style="width: 120mm"><div class="kki-side-buttons">` +
    '<button class="material-symbols-outlined remove-image">hide_image</button><button class="decrease-image">-</button><button class="increase-image">+</button></div>';
  container.appendChild(div);
  toggleKkiSideButtons(div.querySelector('.kki-side-buttons'), true);
  keyKnowledge.images.push({ content: file, width: 120, index });
  return index;
}

function toggleKkiSideButtons(buttons, isActive) {
  buttons.classList.toggle('selectable', isActive);
  if (isActive) {
    buttons.querySelectorAll('button').forEach(button => button.addEventListener('click', handleKkiSideButtonClick));
  } else {
    buttons.querySelectorAll('button').forEach(button => button.removeEventListener('click', handleKkiSideButtonClick));
  }
}

function handleKkiSideButtonClick(event) {
  const container = event.target.closest('.image-container');
  const image = container.querySelector('.question-image');
  const imageEntry = keyKnowledge.images.find(img => img.index === parseInt(container.dataset.index, 10));
  if (event.target.classList.contains('remove-image')) {
    if (!confirm('Are you sure you want to remove the image?')) return;
    container.remove();
    keyKnowledge.images = keyKnowledge.images.filter(img => img.index !== imageEntry.index);
    if (keyKnowledge.images.length === 0) delete keyKnowledge.images;
  } else if (event.target.classList.contains('decrease-image')) {
    if (imageEntry.width > 40) {
      imageEntry.width -= 10;
      image.style.width = `${imageEntry.width}mm`;
    }
  } else if (event.target.classList.contains('increase-image')) {
    if (imageEntry.width < 150) {
      imageEntry.width += 10;
      image.style.width = `${imageEntry.width}mm`;
    }
  }
}

function showModal(event) {
  const target = event.currentTarget ?? event.target;
  if (target.id === 'import-assessment') {
    document.getElementById('modal').classList.add('active');
    document.getElementById('modal-title').textContent = 'Import existing assessment';
    document.getElementById('modal-body-content').textContent = 'Paste the text of the whole assessment and mark scheme:';
    document.getElementById('modal-submit-text').textContent = 'Import';
    document.getElementById('modal-submit').onclick = async () => {
      const value = cleanText(document.getElementById('modal-text').value);
      if (!value) return;
      document.getElementById('modal-submit').disabled = true;
      document.getElementById('modal-submit').classList.add('loading');
      document.getElementById('modal-submit-text').textContent = 'Importing...';
      document.getElementById('modal-close').classList.add('hide');
      document.getElementById('modal-text').disabled = true;
      try {
        const resp = await fetch('/courses/build/ai/import', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
          body: JSON.stringify({ value })
        });
        if (!resp.ok) throw new Error(await resp.text());
        const data = await resp.json();
        assessment.sections = data.sections;
        document.getElementById('assessment-content').innerHTML = '';
        const q = { number: 1 };
        assessment.sections.forEach((section, index) => {
          const sectionElement = createSection(section, index, q);
          document.getElementById('assessment-content').appendChild(sectionElement);
        });
        renumberQuestions();
        updateMarkTotals();
        document.getElementById('btn-assessment').click();
      } catch (error) {
        alert(`Error importing assessment: ${error.message}`);
      }
      closeModal();
    };
  } else if (target.id === 'import-keyknowledge') {
    if (keyKnowledge.declarativeKnowledge.length > 0 || keyKnowledge.proceduralKnowledge.length > 0) {
      if (!confirm('Are you sure you want to replace the existing key knowledge? This cannot be undone.')) return;
    }
    document.getElementById('modal').classList.add('active');
    document.getElementById('modal-title').textContent = 'Generate key knowledge';
    document.getElementById('modal-body-content').textContent = 'Paste the text of the scheme for this unit:';
    document.getElementById('modal-submit-text').textContent = 'Generate';
    document.getElementById('modal-submit').onclick = async () => {
      const value = cleanText(document.getElementById('modal-text').value);
      if (!value) return;
      document.getElementById('modal-submit').disabled = true;
      document.getElementById('modal-submit').classList.add('loading');
      document.getElementById('modal-submit-text').textContent = 'Generating...';
      document.getElementById('modal-close').classList.add('hide');
      document.getElementById('modal-text').disabled = true;
      try {
        const resp = await fetch('/courses/build/ai/generatekeyknowledge', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
          body: JSON.stringify({ value })
        });
        if (!resp.ok) throw new Error(await resp.text());
        const data = await resp.json();
        keyKnowledge.declarativeKnowledge = data.declarativeKnowledge;
        keyKnowledge.proceduralKnowledge = data.proceduralKnowledge;
        renderKeyKnowledge();
        document.getElementById('btn-key-knowledge').click();
      } catch (error) {
        alert(`Error importing key knowledge: ${error.message}`);
      }
      closeModal();
    };
  } else if (target.classList.contains('generate-questions')) {
    if (keyKnowledge.declarativeKnowledge.length === 0) {
      alert('Please add key knowledge items first.');
      return;
    }
    document.getElementById('modal').classList.add('active');
    document.getElementById('modal-title').textContent = 'Generate questions from key knowledge';
    document.getElementById('modal-body-content').textContent = 'How many questions would you like to generate?';
    document.getElementById('modal-submit-text').textContent = 'Generate';
    document.getElementById('modal-numbers').classList.remove('hide');
    document.getElementById('modal-text').classList.add('hide');
    document.getElementById('modal-submit').onclick = async () => {
      const multipleChoiceCount = Number.parseInt(document.getElementById('modal-multiple-choice-count').value, 10);
      const shortAnswerCount = Number.parseInt(document.getElementById('modal-short-answer-count').value, 10);
      if (!Number.isInteger(multipleChoiceCount) || !Number.isInteger(shortAnswerCount) || multipleChoiceCount < 0 || shortAnswerCount < 0 || multipleChoiceCount > 20 || shortAnswerCount > 20) {
        alert('Please enter two valid numbers.');
        return;
      }
      if (multipleChoiceCount === 0 && shortAnswerCount === 0) {
        alert('At least one number must be non-zero.');
        return;
      }
      const currentSection = assessment.sections[parseInt(target.closest('.section').dataset.index, 10)];
      document.getElementById('modal-submit').disabled = true;
      document.getElementById('modal-submit').classList.add('loading');
      document.getElementById('modal-submit-text').textContent = 'Generating...';
      document.getElementById('modal-close').classList.add('hide');
      document.getElementById('modal-text').disabled = true;
      try {
        const resp = await fetch('/courses/build/ai/generatequestions', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
          body: JSON.stringify({
            declarativeKnowledge: keyKnowledge.declarativeKnowledge,
            multipleChoiceCount,
            shortAnswerCount,
            existingQuestions: currentSection.questions.map(q => q.question).filter(q => q.length > 0)
          })
        });
        if (!resp.ok) throw new Error(await resp.text());
        const data = await resp.json();
        currentSection.questions.push(...data);
        document.getElementById('assessment-content').innerHTML = '';
        const q = { number: 1 };
        assessment.sections.forEach((section, index) => {
          const sectionElement = createSection(section, index, q);
          document.getElementById('assessment-content').appendChild(sectionElement);
        });
        renumberQuestions();
        updateMarkTotals();
        document.getElementById('btn-assessment').click();
      } catch (error) {
        alert(`Error generating questions: ${error.message}`);
      }
      closeModal();
    };
  } else if (target.classList.contains('generate-quiz')) {
    if (keyKnowledge.declarativeKnowledge.length === 0) {
      alert('Please add key knowledge items first.');
      return;
    }
    if (questionBank.questions.length > 0 && !confirm('Are you sure you want to replace all existing quiz questions? This cannot be undone.')) return;
    document.getElementById('modal').classList.add('active');
    document.getElementById('modal-title').textContent = 'Replace quiz questions from key knowledge';
    document.getElementById('modal-body-content').textContent = 'Generate a new revision quiz from the unit key knowledge.';
    document.getElementById('modal-submit-text').textContent = 'Generate';
    document.getElementById('modal-text').classList.add('hide');
    document.getElementById('modal-submit').onclick = async () => {
      document.getElementById('modal-submit').disabled = true;
      document.getElementById('modal-submit').classList.add('loading');
      document.getElementById('modal-submit-text').textContent = 'Generating...';
      document.getElementById('modal-close').classList.add('hide');
      try {
        const resp = await fetch(`/courses/${courseId}/${unitId}/build/ai/generatequiz`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf }
        });
        if (!resp.ok) throw new Error(await resp.text());
        const data = await resp.json();
        questionBank.questions = data.questions ?? [];
        isQuizComplete = false;
        renderQuiz();
        initMode(document.getElementById('quiz-content'));
        document.getElementById('btn-quiz').click();
      } catch (error) {
        alert(`Error generating quiz questions: ${error.message}`);
      }
      closeModal();
    };
  }
  document.getElementById('modal-text').focus();
}

function closeModal() {
  document.getElementById('modal').classList.remove('active');
  document.getElementById('modal-text').value = '';
  document.getElementById('modal-submit').disabled = false;
  document.getElementById('modal-submit').classList.remove('loading');
  document.getElementById('modal-close').classList.remove('hide');
  document.getElementById('modal-text').disabled = false;
  document.getElementById('modal-numbers').classList.add('hide');
  document.getElementById('modal-text').classList.remove('hide');
}

function shuffleQuestion(question) {
  const choiceLabels = 'abcd';
  const originalCorrectIndex = choiceLabels.indexOf(question.markScheme);
  const correctAnswer = question.answers[originalCorrectIndex];
  const wrongAnswers = question.answers.filter((_, i) => i !== originalCorrectIndex).sort(() => Math.random() - 0.5);
  let newCorrectIndex;
  do {
    newCorrectIndex = Math.floor(Math.random() * 4);
  } while (newCorrectIndex === originalCorrectIndex);
  question.answers = [...wrongAnswers.slice(0, newCorrectIndex), correctAnswer, ...wrongAnswers.slice(newCorrectIndex)];
  question.markScheme = choiceLabels[newCorrectIndex];
}

function getSectionQuestions(sectionElement) {
  if (sectionElement.closest('#quiz-content')) {
    return questionBank.questions;
  }
  return assessment.sections[parseInt(sectionElement.dataset.index, 10)].questions;
}

