let tab = null;
let keyKnowledgeFeedbackHiddenUntilSave = false;
const csrf = document.querySelector('input[name="__RequestVerificationToken"]').value;
const keyKnowledgeFeedbackStale = document.querySelector('.key-knowledge-feedback-stale');
const keyKnowledgeFeedbackCheckboxes = [...document.querySelectorAll('.key-knowledge-feedback-checkbox')];

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
  const keyKnowledgeFeedback = document.getElementById('key-knowledge-feedback');
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

  function showTab(nextTab) {
    const assessmentTabs = ['assessment', 'mark-scheme'];
    const preserveScroll = assessmentTabs.includes(tab) && assessmentTabs.includes(nextTab);
    const visibleSection = nextTab === 'mark-scheme' ? 'assessment' : nextTab;

    tab = nextTab;
    history.replaceState(null, '', `#${tab}`);
    setActiveButtons();
    schemeOfWorkSheet.classList.toggle('section-hidden', visibleSection !== 'scheme');
    keyKnowledgeSheet.classList.toggle('section-hidden', visibleSection !== 'key-knowledge');
    assessmentElement.classList.toggle('section-hidden', visibleSection !== 'assessment');
    quizElement.classList.toggle('section-hidden', visibleSection !== 'quiz');
    const showKeyKnowledgeFeedback = visibleSection === 'key-knowledge' && keyKnowledgeFeedback !== null && !keyKnowledgeFeedbackHiddenUntilSave;
    keyKnowledgeFeedback?.classList.toggle('section-hidden', !showKeyKnowledgeFeedback);
    bodyContainer.classList.toggle('key-knowledge-feedback-visible', showKeyKnowledgeFeedback);
    pageContainer.classList.toggle('landscape-page', visibleSection === 'scheme');

    if (visibleSection === 'assessment')
      document.querySelectorAll('.mark-scheme').forEach(element => element.classList.toggle('hide', nextTab !== 'mark-scheme'));
    if (!preserveScroll) bodyContainer.scrollTop = 0;
  }

  btnSchemeOfWork?.addEventListener('click', () => showTab('scheme'));
  btnKeyKnowledge.addEventListener('click', () => showTab('key-knowledge'));
  btnAssessment.addEventListener('click', () => showTab('assessment'));
  btnQuiz.addEventListener('click', () => showTab('quiz'));
  btnMarkScheme.addEventListener('click', () => showTab('mark-scheme'));

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
    const validationError = getCompletionError();
    if (validationError) { alert(validationError); return; }

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

function getCompletionError() {
  if (tab === 'key-knowledge') {
    if (keyKnowledge.declarativeKnowledge.length === 0 || keyKnowledge.proceduralKnowledge.length === 0) return 'Both key knowledge sections are required.';
    if (keyKnowledge.declarativeKnowledge.length < 5) return 'There must be at least 5 declarative knowledge items.';
    return '';
  }

  if (tab === 'quiz') {
    if (questionBank.questions.length === 0) return 'At least one quiz question is required.';
    if (questionBank.questions.some(q => q.question === '')) return 'Quiz questions cannot be blank.';
    if (questionBank.questions.some(q => q.correctAnswer === '')) return 'Every quiz question must have a correct answer.';
    if (questionBank.questions.some(q => q.incorrectAnswer1 === '' || q.incorrectAnswer2 === '' || q.incorrectAnswer3 === '')) return 'Every quiz question must have three incorrect answers.';
    return '';
  }

  if (assessment.sections.some(section => section.questions.length === 0)) return 'All sections must have at least one question.';
  if (assessment.sections.some(section => section.questions.some(q => q.question === ''))) return 'Questions cannot be blank.';
  if (assessment.sections.some(section => section.questions.some(q => q.answers && q.answers.some(c => c === '')))) return 'All multiple-choice questions must have four choices.';
  if (assessment.sections.some(section => section.questions.some(q => q.markScheme.length === 0))) return 'All questions must have a mark scheme.';
  return '';
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
    return;
  }
  if (part === 'key-knowledge') {
    keyKnowledgeFeedbackHiddenUntilSave = false;
    document.getElementById('key-knowledge-feedback')?.classList.remove('section-hidden');
    document.querySelector('.body-container').classList.add('key-knowledge-feedback-visible');
    keyKnowledgeFeedbackStale?.classList.remove('hide');
    keyKnowledgeFeedbackCheckboxes.forEach(checkbox => checkbox.checked = false);
  }
}
