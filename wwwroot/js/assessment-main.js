let tab = null;
const csrf = document.querySelector('input[name="__RequestVerificationToken"]').value;

window.addEventListener('load', () => { document.querySelector('.body-container').scrollTop = 0; });
document.addEventListener('DOMContentLoaded', async function() {
  schemeBuilder?.render();
  renderAssessment();
  setupHeader();
});

function setupHeader() {
  const btnSchemeOfWork = document.getElementById('btn-scheme-of-work');
  const btnKeyKnowledge = document.getElementById('btn-key-knowledge');
  const btnAssessment = document.getElementById('btn-assessment');
  const btnQuiz = document.getElementById('btn-quiz');
  const btnMarkScheme = document.getElementById('btn-mark-scheme');
  const btnPrint = document.getElementById('btn-print');
  const btnEdit = document.getElementById('btn-edit');
  const btnSave = document.getElementById('btn-save');
  const btnComplete = document.getElementById('btn-complete');
  const btnRecordSheets = document.getElementById('btn-recordsheets');
  const schemeOfWorkSheet = document.getElementById('scheme-of-work-sheet');
  const keyKnowledgeSheet = document.getElementById('key-knowledge-sheet');
  const assessmentElement = document.getElementById('assessment');
  const quizElement = document.getElementById('quiz');
  const bodyContainer = document.querySelector('.body-container');
  const pageContainer = document.querySelector('.page-container');

  function setActiveButtons() {
    const isAssessmentTab = tab === 'assessment' || tab === 'mark-scheme';
    btnSchemeOfWork?.classList.toggle('active', tab === 'scheme');
    btnKeyKnowledge.classList.toggle('active', tab === 'key-knowledge');
    btnAssessment.classList.toggle('active', tab === 'assessment');
    btnQuiz.classList.toggle('active', tab === 'quiz');
    btnMarkScheme.classList.toggle('active', tab === 'mark-scheme');
    btnEdit.classList.toggle('hide', editMode);
    btnSave.classList.toggle('hide', !editMode);
    if (btnSchemeOfWork) btnSchemeOfWork.disabled = editMode && tab !== 'scheme';
    btnKeyKnowledge.disabled = editMode && tab !== 'key-knowledge';
    btnAssessment.disabled = editMode && !isAssessmentTab;
    btnQuiz.disabled = editMode && tab !== 'quiz';
    btnMarkScheme.disabled = editMode && !isAssessmentTab;
    initMode();
  }

  btnSchemeOfWork?.addEventListener('click', function () {
    tab = 'scheme';
    history.replaceState(null, '', `#${tab}`);
    setActiveButtons();
    schemeOfWorkSheet.classList.remove('section-hidden');
    keyKnowledgeSheet.classList.add('section-hidden');
    assessmentElement.classList.add('section-hidden');
    quizElement.classList.add('section-hidden');
    pageContainer.classList.add('landscape-page');
    bodyContainer.scrollTop = 0;
  });

  btnKeyKnowledge.addEventListener('click', function () {
    tab = 'key-knowledge';
    history.replaceState(null, '', `#${tab}`);
    setActiveButtons();
    schemeOfWorkSheet.classList.add('section-hidden');
    keyKnowledgeSheet.classList.remove('section-hidden');
    assessmentElement.classList.add('section-hidden');
    quizElement.classList.add('section-hidden');
    pageContainer.classList.remove('landscape-page');
    bodyContainer.scrollTop = 0;
  });

  btnAssessment.addEventListener('click', function () {
    const scroll = tab !== 'assessment' && tab !== 'mark-scheme';
    tab = 'assessment';
    history.replaceState(null, '', `#${tab}`);
    setActiveButtons();
    schemeOfWorkSheet.classList.add('section-hidden');
    keyKnowledgeSheet.classList.add('section-hidden');
    assessmentElement.classList.remove('section-hidden');
    quizElement.classList.add('section-hidden');
    pageContainer.classList.remove('landscape-page');
    document.querySelectorAll('.mark-scheme').forEach(el => el.classList.add('hide'));
    if (scroll) bodyContainer.scrollTop = 0;
  });

  btnQuiz.addEventListener('click', function () {
    tab = 'quiz';
    history.replaceState(null, '', `#${tab}`);
    setActiveButtons();
    schemeOfWorkSheet.classList.add('section-hidden');
    keyKnowledgeSheet.classList.add('section-hidden');
    assessmentElement.classList.add('section-hidden');
    quizElement.classList.remove('section-hidden');
    pageContainer.classList.remove('landscape-page');
    bodyContainer.scrollTop = 0;
  });

  btnMarkScheme.addEventListener('click', function () {
    const scroll = tab !== 'assessment' && tab !== 'mark-scheme';
    tab = 'mark-scheme';
    history.replaceState(null, '', `#${tab}`);
    setActiveButtons();
    schemeOfWorkSheet.classList.add('section-hidden');
    keyKnowledgeSheet.classList.add('section-hidden');
    assessmentElement.classList.remove('section-hidden');
    quizElement.classList.add('section-hidden');
    pageContainer.classList.remove('landscape-page');
    document.querySelectorAll('.mark-scheme').forEach(el => el.classList.remove('hide'));
    if (scroll) bodyContainer.scrollTop = 0;
  });

  btnEdit.addEventListener('click', function() {
    if (!isEditor) return;
    editMode = true;
    setActiveButtons();
  });

  btnSave.addEventListener('click', async function() {
    editMode = false;
    if (tab === 'key-knowledge') {
      isKeyKnowledgeComplete = false;
    } else if (tab === 'quiz') {
      isQuizComplete = false;
    } else if (tab !== 'scheme') {
      isAssessmentComplete = false;
    }
    setActiveButtons();
    await save();
  });

  btnPrint.addEventListener('click', async function () {
    if (editMode) btnSave.click();
    window.print();
  });

  btnComplete.addEventListener('click', async function () {
    if (tab === 'scheme') return;
    if (tab === 'key-knowledge') {
      if (keyKnowledge.declarativeKnowledge.length === 0 || keyKnowledge.proceduralKnowledge.length === 0) { alert('Both key knowledge sections are required.'); return; }
      if (keyKnowledge.declarativeKnowledge.length < 5) { alert('There must be at least 5 declarative knowledge items.'); return; }
    } else if (tab === 'quiz') {
      if (questionBank.questions.length === 0) { alert('At least one quiz question is required.'); return; }
      if (questionBank.questions.some(q => q.question === '')) { alert('Quiz questions cannot be blank.'); return; }
      if (questionBank.questions.some(q => q.correctAnswer === '')) { alert('Every quiz question must have a correct answer.'); return; }
      if (questionBank.questions.some(q => q.incorrectAnswer1 === '' || q.incorrectAnswer2 === '' || q.incorrectAnswer3 === '')) { alert('Every quiz question must have three incorrect answers.'); return; }
    } else {
      if (assessment.sections.some(section => section.questions.length === 0)) { alert('All sections must have at least one question.'); return; }
      if (assessment.sections.some(section => section.questions.some(q => q.question === ''))) { alert('Questions cannot be blank.'); return; }
      if (assessment.sections.some(section => section.questions.some(q => q.answers && q.answers.some(c => c === '')))) { alert('All multiple-choice questions must have four choices.'); return; }
      if (assessment.sections.some(section => section.questions.some(q => q.markScheme.length === 0))) { alert('All questions must have a mark scheme.'); return; }
    }
    const part = tab === 'key-knowledge' ? 'key-knowledge' : tab === 'quiz' ? 'quiz' : 'assessment';
    const resp = await fetch(`/courses/${courseId}/${unitId}/build/${part}-complete`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
      body: JSON.stringify({ complete: true })
    });
    if (!resp.ok) {
      const error = await resp.text();
      alert(`Unable to mark as complete: ${error}`);
      return;
    }
    if (tab === 'key-knowledge') isKeyKnowledgeComplete = true;
    else if (tab === 'quiz') isQuizComplete = true;
    else isAssessmentComplete = true;
    initMode();
  });

  btnRecordSheets.classList.toggle('hide', courseId !== 'ks3-wellbeing-active');
  btnRecordSheets.addEventListener('click', function () {
    window.location.href = `/courses/${courseId}/${unitId}/build/recordsheets`;
  });

  window.addEventListener('hashchange', changeTabFromHash);
  changeTabFromHash();
  if (tab === 'key-knowledge' && keyKnowledge.declarativeKnowledge.length === 0 && keyKnowledge.proceduralKnowledge.length === 0 && isEditor) btnEdit.click();
  if (tab === 'quiz' && questionBank.questions.length === 0 && isEditor) btnEdit.click();

  function changeTabFromHash() {
    if (location.hash === '#scheme' && isAdmin && btnSchemeOfWork) btnSchemeOfWork.click();
    else if (location.hash === '#assessment') btnAssessment.click();
    else if (location.hash === '#quiz') btnQuiz.click();
    else if (location.hash === '#mark-scheme') btnMarkScheme.click();
    else btnKeyKnowledge.click();
  }
}

async function save() {
  if (tab === 'scheme') {
    await schemeBuilder.save();
    return;
  }
  const part = tab === 'key-knowledge' ? 'key-knowledge' : tab === 'quiz' ? 'quiz' : 'assessment';
  const resp = await fetch(`/courses/${courseId}/${unitId}/build/${part}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
    body: JSON.stringify(part === 'key-knowledge' ? keyKnowledge : part === 'quiz' ? questionBank : assessment)
  });
  if (!resp.ok) {
    const error = await resp.text();
    alert(`Unable to save: ${error}`);
  }
}
