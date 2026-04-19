const assignmentDetailRoot = document.getElementById('assignment-detail-root');
const assignmentCsrfToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';

const assignmentCorrectResponses = ['Well done!', 'Spot on!', 'Nice job!', 'Correct!', 'Great job!', 'Excellent!'];
const assignmentIncorrectResponses = ['Incorrect!', 'Not right!', 'Oops!', 'Missed it!', 'Think again!', 'Nope!'];
const assignmentCorrectDelayMs = 1000;
const assignmentIncorrectDelayMs = 5000;
const assignmentOptionRevealDelayMs = 4000;
const assignmentProgressAnimationDurationMs = 1000;

let assignmentOptionRevealTimer = 0;

const assignmentState = {
  courseId: assignmentDetailData?.courseId ?? '',
  currentQuestion: assignmentDetailData?.currentQuestion ?? null,
  completedQuestions: assignmentDetailData?.completedQuestions ?? 0,
  totalQuestions: assignmentDetailData?.totalQuestions ?? 0,
  isComplete: !!assignmentDetailData?.isComplete,
  optionsVisible: !(assignmentDetailData?.currentQuestion),
  busy: false,
  error: ''
};

document.addEventListener('DOMContentLoaded', async () => {
  if (!assignmentDetailRoot) {
    return;
  }

  assignmentDetailRoot.addEventListener('click', onAssignmentDetailClick);
  queueAssignmentOptionReveal();
  await renderAssignmentDetail();
});

async function onAssignmentDetailClick(event) {
  const button = event.target.closest('.assignment-answer-option');
  if (!button || assignmentState.busy || !assignmentState.currentQuestion) {
    return;
  }

  const answerIndex = Number(button.dataset.answerIndex);
  if (Number.isNaN(answerIndex)) {
    return;
  }

  assignmentState.busy = true;
  assignmentState.error = '';
  setAssignmentButtonsDisabled(true);

  try {
    const response = await fetch(assignmentSubmitUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': assignmentCsrfToken
      },
      body: JSON.stringify({
        questionNumber: assignmentState.currentQuestion.questionNumber,
        answer: answerIndex
      })
    });

    if (!response.ok) {
      throw new Error((await response.text()) || 'Unable to submit your answer.');
    }

    const result = await response.json();
    const nextCompletedQuestions = result.completedQuestions ?? assignmentState.completedQuestions;
    const nextTotalQuestions = result.totalQuestions ?? assignmentState.totalQuestions;
    const progressAnimation =
      nextCompletedQuestions > assignmentState.completedQuestions
        ? animateAssignmentProgress(nextCompletedQuestions, nextTotalQuestions)
        : Promise.resolve();

    await Promise.all([
      playAssignmentFeedback(answerIndex, result.correctAnswer),
      progressAnimation
    ]);

    setAssignmentCurrentQuestion(result.nextQuestion ?? null);
    assignmentState.completedQuestions = nextCompletedQuestions;
    assignmentState.totalQuestions = nextTotalQuestions;
    assignmentState.error = '';
  } catch (error) {
    assignmentState.error = error instanceof Error ? error.message : 'Unable to submit your answer.';
  } finally {
    assignmentState.busy = false;
    await renderAssignmentDetail();
  }
}

async function playAssignmentFeedback(selectedIndex, correctIndex) {
  const buttons = Array.from(assignmentDetailRoot.querySelectorAll('.assignment-answer-option'));
  const feedback = assignmentDetailRoot.querySelector('.assignment-feedback');
  if (buttons.length === 0 || !feedback) {
    return;
  }

  const selectedButton = buttons[selectedIndex];
  const correctButton = buttons[correctIndex];
  const correct = selectedIndex === correctIndex;

  if (selectedButton) {
    selectedButton.classList.add(correct ? 'correct' : 'incorrect');
  }

  if (!correct && correctButton) {
    correctButton.classList.add('correct-outline');
    correctButton.classList.add('correct-waiting');
    correctButton.style.setProperty('--assignment-wait-duration', `${assignmentIncorrectDelayMs}ms`);
  }

  feedback.replaceChildren(buildAssignmentOutcome(correct));
  await delay(correct ? assignmentCorrectDelayMs : assignmentIncorrectDelayMs);
}

async function renderAssignmentDetail() {
  if (!assignmentDetailRoot) {
    return;
  }

  assignmentDetailRoot.replaceChildren(
    assignmentState.isComplete || !assignmentState.currentQuestion
      ? buildAssignmentComplete()
      : buildAssignmentQuestion(assignmentState.currentQuestion, assignmentState.error)
  );

  if (assignmentState.busy) {
    setAssignmentButtonsDisabled(true);
  }

  await typesetAssignmentMath();
}

function buildAssignmentQuestion(question, error) {
  const wrapper = document.createElement('section');
  wrapper.className = 'assignment-question-card';
  wrapper.setAttribute('aria-labelledby', 'assignment-question-heading');

  const label = buildAssignmentQuestionLabel(question);
  const progress = buildAssignmentProgress(assignmentState.completedQuestions, assignmentState.totalQuestions);

  const text = createAssignmentElement('p', 'assignment-question-text', question.questionText);
  const waiting = assignmentState.optionsVisible ? null : buildAssignmentWaitingEffect();
  const answers = document.createElement('div');
  answers.className = `assignment-answer-list${assignmentState.optionsVisible ? ' is-revealed' : ' is-hidden'}`;
  answers.setAttribute('aria-hidden', assignmentState.optionsVisible ? 'false' : 'true');
  question.answers.forEach((answer, index) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'assignment-answer-option';
    button.dataset.answerIndex = String(index);
    button.disabled = !assignmentState.optionsVisible;
    button.append(
      createAssignmentElement('span', 'assignment-answer-index', `${String.fromCharCode(97 + index)}.`),
      createAssignmentElement('span', 'assignment-answer-text', answer)
    );
    answers.appendChild(button);
  });

  const feedback = document.createElement('div');
  feedback.className = 'assignment-feedback';

  wrapper.append(progress, label, text);
  if (waiting) {
    wrapper.appendChild(waiting);
  }
  wrapper.append(answers, feedback);
  if (error) {
    wrapper.appendChild(createAssignmentElement('p', 'assignment-detail-error', error));
  }

  return wrapper;
}

function buildAssignmentQuestionLabel(question) {
  const label = document.createElement('div');
  label.className = 'assignment-question-label';
  label.id = 'assignment-question-heading';
  label.appendChild(createAssignmentElement('span', 'assignment-question-unit-title', question.unitTitle || `Question ${question.questionNumber}`));

  if (assignmentState.courseId && question.unitId) {
    const reviseLink = document.createElement('a');
    reviseLink.className = 'assignment-question-revise-link';
    reviseLink.href = buildAssignmentReviseHref(assignmentState.courseId, question.unitId);
    reviseLink.target = '_blank';
    reviseLink.rel = 'noopener noreferrer';
    reviseLink.append(
      createAssignmentIcon('info'),
      createAssignmentElement('span', 'assignment-question-revise-text', 'Revise')
    );
    label.appendChild(reviseLink);
  }

  return label;
}

function buildAssignmentComplete() {
  const wrapper = document.createElement('section');
  wrapper.className = 'assignment-complete';
  const backLink = document.createElement('a');
  backLink.className = 'assignment-complete-link';
  backLink.href = '/assignments';
  backLink.textContent = 'Back to assignments';
  wrapper.append(
    buildAssignmentProgress(assignmentState.totalQuestions, assignmentState.totalQuestions),
    createAssignmentIcon('trophy'),
    createAssignmentElement('p', 'assignment-complete-title', 'Assignment complete'),
    createAssignmentElement('p', 'assignment-complete-text', 'All questions have been answered.'),
    backLink
  );
  return wrapper;
}

function buildAssignmentProgress(completed, total) {
  const wrapper = document.createElement('div');
  wrapper.className = 'assignment-question-progress';

  const progressText = total > 0 ? `${Math.min(completed, total)} of ${total} answered` : '0 of 0 answered';
  const progressBar = document.createElement('div');
  progressBar.className = 'assignment-question-progress-bar';
  progressBar.setAttribute('role', 'progressbar');
  progressBar.setAttribute('aria-valuemin', '0');
  progressBar.setAttribute('aria-valuemax', String(Math.max(total, 1)));
  progressBar.setAttribute('aria-valuenow', String(Math.min(completed, total)));
  progressBar.setAttribute('aria-label', progressText);

  const progressFill = document.createElement('span');
  progressFill.className = 'assignment-question-progress-fill';
  progressFill.style.width = `${total > 0 ? (Math.min(completed, total) / total) * 100 : 0}%`;
  progressBar.appendChild(progressFill);

  wrapper.appendChild(progressBar);
  return wrapper;
}

function buildAssignmentOutcome(correct) {
  const outcome = document.createElement('div');
  outcome.className = `assignment-outcome${correct ? ' is-success' : ' is-error'}`;
  outcome.append(
    createAssignmentIcon(correct ? 'thumb_up' : 'close'),
    createAssignmentElement(
      'span',
      'assignment-outcome-text',
      (correct ? assignmentCorrectResponses : assignmentIncorrectResponses)[Math.floor(Math.random() * (correct ? assignmentCorrectResponses.length : assignmentIncorrectResponses.length))]
    )
  );
  return outcome;
}

function buildAssignmentWaitingEffect() {
  const waiting = document.createElement('div');
  waiting.className = 'assignment-answer-waiting';
  waiting.setAttribute('role', 'status');
  waiting.setAttribute('aria-live', 'polite');

  const dots = document.createElement('span');
  dots.className = 'assignment-answer-waiting-dots';
  dots.setAttribute('aria-hidden', 'true');

  for (let i = 0; i < 3; i++) {
    dots.appendChild(createAssignmentElement('span', 'assignment-answer-waiting-dot'));
  }

  waiting.append(dots);

  return waiting;
}

function createAssignmentIcon(name) {
  const icon = createAssignmentElement('span', 'material-symbols-outlined', name);
  icon.setAttribute('aria-hidden', 'true');
  return icon;
}

function buildAssignmentReviseHref(courseId, unitId) {
  return `/courses/${encodeURIComponent(courseId)}/${encodeURIComponent(unitId)}`;
}

function setAssignmentButtonsDisabled(disabled) {
  assignmentDetailRoot.querySelectorAll('.assignment-answer-option').forEach(button => {
    button.disabled = disabled;
  });
}

async function typesetAssignmentMath() {
  if (typeof window.MathJax?.typesetPromise !== 'function') {
    return;
  }

  const nodes = [
    ...assignmentDetailRoot.querySelectorAll('.assignment-question-text'),
    ...assignmentDetailRoot.querySelectorAll('.assignment-answer-text')
  ];
  if (nodes.length > 0) {
    await window.MathJax.typesetPromise(nodes);
  }
}

function createAssignmentElement(tagName, className, textContent) {
  const element = document.createElement(tagName);
  if (className) {
    element.className = className;
  }
  if (textContent !== undefined) {
    element.textContent = textContent;
  }
  return element;
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function animateAssignmentProgress(completed, total) {
  const progressBar = assignmentDetailRoot.querySelector('.assignment-question-progress-bar');
  const progressFill = progressBar?.querySelector('.assignment-question-progress-fill');
  if (!progressBar || !progressFill) {
    return;
  }

  const clampedCompleted = Math.min(completed, total);
  const width = `${total > 0 ? (clampedCompleted / total) * 100 : 0}%`;
  const progressText = total > 0 ? `${clampedCompleted} of ${total} answered` : '0 of 0 answered';

  progressBar.setAttribute('aria-valuemax', String(Math.max(total, 1)));
  progressBar.setAttribute('aria-valuenow', String(clampedCompleted));
  progressBar.setAttribute('aria-label', progressText);

  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    progressFill.style.width = width;
    return;
  }

  await new Promise(resolve => {
    let settled = false;
    const finish = () => {
      if (settled) {
        return;
      }

      settled = true;
      progressFill.removeEventListener('transitionend', onTransitionEnd);
      window.clearTimeout(timeoutId);
      resolve();
    };
    const onTransitionEnd = event => {
      if (event.propertyName === 'width') {
        finish();
      }
    };
    const timeoutId = window.setTimeout(finish, assignmentProgressAnimationDurationMs + 100);

    progressFill.addEventListener('transitionend', onTransitionEnd);
    requestAnimationFrame(() => {
      progressFill.style.width = width;
    });
  });
}

function setAssignmentCurrentQuestion(question) {
  clearAssignmentOptionRevealTimer();
  assignmentState.currentQuestion = question;
  assignmentState.isComplete = !question;
  assignmentState.optionsVisible = !question;
  queueAssignmentOptionReveal();
}

function queueAssignmentOptionReveal() {
  if (!assignmentState.currentQuestion || assignmentState.optionsVisible) {
    return;
  }

  const questionNumber = assignmentState.currentQuestion.questionNumber;
  assignmentOptionRevealTimer = window.setTimeout(async () => {
    assignmentOptionRevealTimer = 0;

    if (!assignmentState.currentQuestion || assignmentState.currentQuestion.questionNumber !== questionNumber) {
      return;
    }

    assignmentState.optionsVisible = true;
    await renderAssignmentDetail();
  }, assignmentOptionRevealDelayMs);
}

function clearAssignmentOptionRevealTimer() {
  if (assignmentOptionRevealTimer) {
    window.clearTimeout(assignmentOptionRevealTimer);
    assignmentOptionRevealTimer = 0;
  }
}
