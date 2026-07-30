const assignmentDetailRoot = document.getElementById('assignment-detail-root');
const assignmentCsrfToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
const assignmentMode = typeof assignmentQuizConfig === 'undefined'
  ? {
      mode: 'assignment',
      completeTitle: 'Assignment complete',
      completeText: 'All questions have been answered.',
      backHref: '/assignments',
      backLabel: 'Back to assignments'
    }
  : assignmentQuizConfig;

const assignmentCorrectResponses = ['Well done!', 'Spot on!', 'Nice job!', 'Correct!', 'Great job!', 'Excellent!'];
const assignmentIncorrectResponses = ['Incorrect!', 'Not right!', 'Oops!', 'Missed it!', 'Think again!', 'Nope!'];
const assignmentCorrectDelayMs = 1000;
const assignmentIncorrectDelayMs = 5000;
const assignmentOptionRevealDelayMs = 4000;
const assignmentProgressAnimationDurationMs = 1000;
const assignmentXpAnimationDurationMs = 1200;
const assignmentRankProgressAnimationDurationMs = 1100;
const assignmentXpPhaseRevealDelayMs = 480;
const assignmentRankUpCelebrationDurationMs = 1000;

let assignmentOptionRevealTimer = 0;

const assignmentState = {
  courseId: assignmentDetailData?.courseId ?? '',
  attemptId: assignmentDetailData?.attemptId ?? '',
  currentQuestion: assignmentDetailData?.currentQuestion ?? null,
  completedQuestions: assignmentDetailData?.completedQuestions ?? 0,
  totalQuestions: assignmentDetailData?.totalQuestions ?? 0,
  isComplete: !!assignmentDetailData?.isComplete,
  gamification: assignmentDetailData?.gamification ?? null,
  previousGamification: null,
  remainingBonusXp: assignmentDetailData?.remainingBonusXp ?? 0,
  newlyAwardedXp: null,
  rankUp: '',
  celebrationPending: false,
  notice: '',
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
    const request = {
      questionNumber: assignmentState.currentQuestion.questionNumber,
      answer: answerIndex
    };
    if (assignmentMode.mode === 'bonus') {
      request.attemptId = assignmentState.attemptId;
    }

    const response = await fetch(assignmentSubmitUrl, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': assignmentCsrfToken
      },
      body: JSON.stringify(request)
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
    assignmentState.attemptId = result.attemptId ?? assignmentState.attemptId;
    assignmentState.completedQuestions = nextCompletedQuestions;
    assignmentState.totalQuestions = nextTotalQuestions;
    assignmentState.remainingBonusXp = result.remainingBonusXp ?? assignmentState.remainingBonusXp;
    const newlyAwardedXp = Number.isInteger(result.newlyAwardedXp) ? result.newlyAwardedXp : null;
    if (result.gamification) {
      if (newlyAwardedXp > 0) {
        assignmentState.previousGamification = assignmentState.gamification;
        assignmentState.celebrationPending = true;
      }
      const previousRank = assignmentState.gamification?.currentRank;
      assignmentState.gamification = result.gamification;
      if (previousRank && previousRank !== result.gamification.currentRank) {
        assignmentState.rankUp = result.gamification.currentRank;
      }
    }
    if (newlyAwardedXp !== null) {
      assignmentState.newlyAwardedXp = newlyAwardedXp;
    }
    assignmentState.notice = result.restarted ? 'A wrong answer restarted the bonus quiz with a fresh set of questions.' : '';
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

  if (assignmentState.celebrationPending && (assignmentState.isComplete || !assignmentState.currentQuestion)) {
    assignmentState.celebrationPending = false;
    await playAssignmentCompletionCelebration();
  }
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
  if (assignmentState.notice) {
    const notice = createAssignmentElement('p', 'assignment-detail-notice', assignmentState.notice);
    notice.setAttribute('role', 'status');
    wrapper.appendChild(notice);
  }

  return wrapper;
}

function buildAssignmentQuestionLabel(question) {
  const label = document.createElement('div');
  label.className = 'assignment-question-label';
  label.id = 'assignment-question-heading';
  label.appendChild(createAssignmentElement('span', 'assignment-question-unit-title', question.unitTitle || `Question ${question.questionNumber}`));

  const courseId = question.courseId || assignmentState.courseId;
  if (courseId && question.unitId) {
    const reviseLink = document.createElement('a');
    reviseLink.className = 'assignment-question-revise-link';
    reviseLink.href = buildAssignmentReviseHref(courseId, question.unitId);
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
  const celebrating = assignmentState.celebrationPending && assignmentState.newlyAwardedXp > 0;
  const displayProgress = celebrating && assignmentState.previousGamification
    ? assignmentState.previousGamification
    : assignmentState.gamification;
  wrapper.className = `assignment-complete${celebrating ? ' is-celebrating' : ''}`;
  wrapper.setAttribute('role', 'status');
  wrapper.setAttribute('aria-live', 'polite');
  if (celebrating) {
    const effects = createAssignmentElement('div', 'assignment-complete-effects');
    effects.setAttribute('aria-hidden', 'true');
    for (let i = 0; i < 18; i++) {
      const angle = Math.PI * 2 * i / 18;
      const distance = 72 + (i % 3) * 16;
      const particle = createAssignmentElement('span', 'assignment-complete-particle');
      particle.style.setProperty('--assignment-particle-x', `${Math.round(Math.cos(angle) * distance)}px`);
      particle.style.setProperty('--assignment-particle-y', `${Math.round(Math.sin(angle) * distance * 0.72)}px`);
      particle.style.setProperty('--assignment-rank-particle-x', `${Math.round(Math.cos(angle) * distance * 1.55)}px`);
      particle.style.setProperty('--assignment-rank-particle-y', `${Math.round(Math.sin(angle) * distance * 1.05)}px`);
      particle.style.setProperty('--assignment-particle-delay', `${(i % 4) * 45}ms`);
      effects.appendChild(particle);
    }
    const floatingXp = createAssignmentElement('span', 'assignment-complete-xp-float', `+${assignmentState.newlyAwardedXp} XP`);
    floatingXp.setAttribute('aria-hidden', 'true');
    effects.appendChild(floatingXp);
    wrapper.appendChild(effects);
  }
  const backLink = document.createElement('a');
  backLink.className = 'assignment-complete-link';
  backLink.href = assignmentMode.mode === 'bonus' && assignmentState.remainingBonusXp > 0
    ? '/bonus-quiz'
    : assignmentMode.backHref;
  backLink.textContent = assignmentMode.mode === 'bonus' && assignmentState.remainingBonusXp > 0
    ? `Start another bonus quiz (${assignmentState.remainingBonusXp} XP remaining)`
    : assignmentMode.backLabel;
  const trophy = createAssignmentIcon('trophy');
  trophy.classList.add('assignment-complete-trophy');
  wrapper.append(buildAssignmentProgress(assignmentState.totalQuestions, assignmentState.totalQuestions), trophy, createAssignmentElement('p', 'assignment-complete-title', assignmentMode.completeTitle));
  if (assignmentState.newlyAwardedXp > 0) {
    const xp = createAssignmentElement('p', 'assignment-complete-xp');
    xp.setAttribute('aria-label', `+${assignmentState.newlyAwardedXp} XP earned`);
    const visualXp = createAssignmentElement('span', 'assignment-complete-xp-value', celebrating ? '+0 XP earned' : `+${assignmentState.newlyAwardedXp} XP earned`);
    visualXp.setAttribute('aria-hidden', 'true');
    xp.appendChild(visualXp);
    wrapper.appendChild(xp);
  }
  const xpPhases = buildAssignmentXpPhases();
  if (xpPhases.some(phase => phase.type !== 'quiz')) {
    const bonuses = createAssignmentElement('div', 'assignment-complete-bonuses');
    xpPhases.filter(phase => phase.type !== 'quiz').forEach(phase => {
      const bonus = createAssignmentElement('p', `assignment-complete-bonus is-${phase.type}`);
      bonus.dataset.assignmentXpPhase = phase.type;
      bonus.setAttribute('aria-hidden', 'true');
      bonus.append(
        createAssignmentIcon(phase.type === 'weekly' ? 'task_alt' : 'local_fire_department'),
        createAssignmentElement(
          'span',
          '',
          phase.type === 'weekly'
            ? `100% weekly completion — +${phase.amount} XP`
            : `${assignmentState.gamification.currentStreak} week streak — +${phase.amount} XP`
        )
      );
      bonuses.appendChild(bonus);
    });
    wrapper.appendChild(bonuses);
  }
  if (assignmentState.rankUp) {
    const rankUp = createAssignmentElement('p', 'assignment-rank-up');
    if (celebrating) rankUp.setAttribute('aria-hidden', 'true');
    rankUp.append(
      createAssignmentIcon('auto_awesome'),
      createAssignmentElement('span', '', `Level up — ${assignmentState.rankUp}`)
    );
    wrapper.appendChild(rankUp);
  }
  if (assignmentState.newlyAwardedXp === null) {
    wrapper.appendChild(createAssignmentElement('p', 'assignment-complete-text', assignmentMode.completeText));
  }
  if (assignmentState.newlyAwardedXp !== null && assignmentState.gamification) {
    wrapper.appendChild(buildAssignmentGamificationStatus(displayProgress ?? assignmentState.gamification, assignmentState.gamification));
  }
  wrapper.appendChild(backLink);
  return wrapper;
}

function buildAssignmentGamificationStatus(progress, finalProgress = progress) {
  const status = createAssignmentElement('div', 'assignment-complete-rank');
  const summary = createAssignmentElement('div', 'assignment-complete-rank-summary');
  const total = createAssignmentElement('span', 'assignment-complete-total');
  total.setAttribute('aria-label', `${finalProgress.totalXp} lifetime XP`);
  const visualTotal = createAssignmentElement('span', 'assignment-complete-total-value', `${progress.totalXp} lifetime XP`);
  visualTotal.setAttribute('aria-hidden', 'true');
  total.appendChild(visualTotal);
  summary.append(createAssignmentElement('strong', 'assignment-complete-rank-name', progress.currentRank), total);
  status.appendChild(summary);

  if (progress.nextRank) {
    const progressBar = createAssignmentElement('div', 'assignment-complete-rank-bar');
    progressBar.setAttribute('role', 'progressbar');
    progressBar.setAttribute('aria-label', finalProgress.nextRank ? `Progress to ${finalProgress.nextRank}` : 'Maximum rank reached');
    progressBar.setAttribute('aria-valuemin', '0');
    progressBar.setAttribute('aria-valuemax', String(finalProgress.nextRank ? finalProgress.rankSpanXp : 1));
    progressBar.setAttribute('aria-valuenow', String(finalProgress.nextRank ? finalProgress.rankProgressXp : 1));
    const fill = createAssignmentElement('span', 'assignment-complete-rank-fill');
    fill.style.width = `${progress.rankSpanXp > 0 ? Math.min(progress.rankProgressXp / progress.rankSpanXp, 1) * 100 : 0}%`;
    progressBar.appendChild(fill);
    const next = createAssignmentElement('span', 'assignment-complete-rank-next');
    next.setAttribute('aria-label', finalProgress.nextRank
      ? `${finalProgress.rankProgressXp} / ${finalProgress.rankSpanXp} XP to ${finalProgress.nextRank}`
      : 'Maximum rank reached');
    const visualNext = createAssignmentElement('span', 'assignment-complete-rank-next-value', `${progress.rankProgressXp} / ${progress.rankSpanXp} XP to ${progress.nextRank}`);
    visualNext.setAttribute('aria-hidden', 'true');
    next.appendChild(visualNext);
    status.append(progressBar, next);
  } else {
    status.appendChild(createAssignmentElement('span', 'assignment-complete-rank-next is-maximum', 'Maximum rank reached'));
  }

  return status;
}

function buildAssignmentXpPhases() {
  const awardedXp = assignmentState.newlyAwardedXp ?? 0;
  if (awardedXp <= 0) return [];

  const quizXp = assignmentMode.mode === 'assignment' && assignmentState.totalQuestions > 0
    ? Math.min(assignmentState.totalQuestions, awardedXp)
    : awardedXp;
  const phases = [{ type: 'quiz', amount: quizXp }];
  let remainingXp = awardedXp - quizXp;
  if (assignmentMode.mode === 'assignment' && remainingXp > 0) {
    const weeklyXp = Math.min(10, remainingXp);
    phases.push({ type: 'weekly', amount: weeklyXp });
    remainingXp -= weeklyXp;
  }
  if (remainingXp > 0) phases.push({ type: 'streak', amount: remainingXp });
  return phases;
}

async function playAssignmentCompletionCelebration() {
  const wrapper = assignmentDetailRoot.querySelector('.assignment-complete');
  const progress = assignmentState.gamification;
  const previousProgress = assignmentState.previousGamification ?? progress;
  if (!wrapper || !progress || assignmentState.newlyAwardedXp <= 0) {
    return;
  }

  const xpValue = wrapper.querySelector('.assignment-complete-xp-value');
  const totalValue = wrapper.querySelector('.assignment-complete-total-value');
  const nextValue = wrapper.querySelector('.assignment-complete-rank-next-value');
  const fill = wrapper.querySelector('.assignment-complete-rank-fill');
  const rankUp = previousProgress.currentRank !== progress.currentRank;
  const xpPhases = buildAssignmentXpPhases();
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    if (xpValue) xpValue.textContent = `+${assignmentState.newlyAwardedXp} XP earned`;
    wrapper.querySelectorAll('.assignment-complete-bonus').forEach(bonus => {
      bonus.classList.add('is-revealed');
      bonus.removeAttribute('aria-hidden');
    });
    if (rankUp) {
      wrapper.querySelector('.assignment-rank-up')?.removeAttribute('aria-hidden');
      wrapper.classList.add('is-rank-complete', 'is-rank-up-revealed');
    }
    setAssignmentGamificationVisual(wrapper, progress);
    wrapper.classList.add('is-celebration-settled');
    return;
  }

  let earnedXp = 0;
  let lifetimeXp = previousProgress.totalXp;
  let displayedRankProgressXp = previousProgress.rankProgressXp;
  let displayedRankSpanXp = previousProgress.rankSpanXp;
  let displayedNextRank = previousProgress.nextRank;
  let rankUpPending = rankUp;

  const animateXpSegment = async amount => {
    if (amount <= 0) return;
    const nextEarnedXp = earnedXp + amount;
    const nextLifetimeXp = lifetimeXp + amount;
    const nextRankProgressXp = displayedRankProgressXp + amount;
    const duration = Math.max(650, Math.min(assignmentRankProgressAnimationDurationMs, 500 + amount * 24));
    await Promise.all([
      animateAssignmentNumber(xpValue, earnedXp, nextEarnedXp, value => `+${value} XP earned`, duration),
      animateAssignmentNumber(totalValue, lifetimeXp, nextLifetimeXp, value => `${value} lifetime XP`, duration),
      fill && displayedNextRank
        ? animateAssignmentRankProgress(fill, `${Math.min(nextRankProgressXp / Math.max(displayedRankSpanXp, 1), 1) * 100}%`, duration)
        : Promise.resolve(),
      nextValue && displayedNextRank
        ? animateAssignmentNumber(
            nextValue,
            displayedRankProgressXp,
            nextRankProgressXp,
            value => `${value} / ${displayedRankSpanXp} XP to ${displayedNextRank}`,
            duration)
        : Promise.resolve()
    ]);
    earnedXp = nextEarnedXp;
    lifetimeXp = nextLifetimeXp;
    displayedRankProgressXp = nextRankProgressXp;
  };

  await delay(180);

  for (const phase of xpPhases) {
    if (phase.type !== 'quiz') {
      const bonus = wrapper.querySelector(`[data-assignment-xp-phase="${phase.type}"]`);
      if (bonus) {
        bonus.classList.add('is-revealed');
        bonus.removeAttribute('aria-hidden');
      }
      wrapper.classList.add(`is-${phase.type}-celebrating`);
      await delay(assignmentXpPhaseRevealDelayMs);
    }

    let remainingXp = phase.amount;
    if (rankUpPending && fill && displayedNextRank) {
      const xpToNextRank = Math.max(0, displayedRankSpanXp - displayedRankProgressXp);
      if (remainingXp >= xpToNextRank) {
        await animateXpSegment(xpToNextRank);
        remainingXp -= xpToNextRank;
        const rankUpBadge = wrapper.querySelector('.assignment-rank-up');
        rankUpBadge?.removeAttribute('aria-hidden');
        wrapper.classList.add('is-rank-complete', 'is-rank-up-revealed', 'is-level-up-impact');
        await delay(assignmentRankUpCelebrationDurationMs);
        rankUpPending = false;
        displayedRankProgressXp = 0;
        displayedRankSpanXp = progress.rankSpanXp;
        displayedNextRank = progress.nextRank;

        if (progress.nextRank) {
          setAssignmentGamificationVisual(wrapper, { ...progress, totalXp: lifetimeXp, rankProgressXp: 0 }, 0);
          fill.classList.add('is-resetting');
          fill.style.width = '0%';
          await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
          fill.classList.remove('is-resetting');
        } else {
          setAssignmentGamificationVisual(wrapper, { ...progress, totalXp: lifetimeXp });
        }
      }
    }

    await animateXpSegment(remainingXp);
  }

  setAssignmentGamificationVisual(wrapper, progress);
  wrapper.classList.add('is-celebration-settled');
}

function setAssignmentGamificationVisual(wrapper, progress, displayedRankProgressXp = progress.rankProgressXp) {
  const rankName = wrapper.querySelector('.assignment-complete-rank-name');
  const totalValue = wrapper.querySelector('.assignment-complete-total-value');
  const progressBar = wrapper.querySelector('.assignment-complete-rank-bar');
  const fill = wrapper.querySelector('.assignment-complete-rank-fill');
  const next = wrapper.querySelector('.assignment-complete-rank-next');
  const nextValue = wrapper.querySelector('.assignment-complete-rank-next-value');
  if (rankName) rankName.textContent = progress.currentRank;
  if (totalValue) totalValue.textContent = `${progress.totalXp} lifetime XP`;

  if (progress.nextRank) {
    if (progressBar) {
      progressBar.setAttribute('aria-label', `Progress to ${progress.nextRank}`);
      progressBar.setAttribute('aria-valuemax', String(progress.rankSpanXp));
      progressBar.setAttribute('aria-valuenow', String(progress.rankProgressXp));
    }
    if (next) {
      next.classList.remove('is-maximum');
      next.setAttribute('aria-label', `${progress.rankProgressXp} / ${progress.rankSpanXp} XP to ${progress.nextRank}`);
      if (nextValue) nextValue.textContent = `${displayedRankProgressXp} / ${progress.rankSpanXp} XP to ${progress.nextRank}`;
    }
  } else {
    if (progressBar) {
      progressBar.setAttribute('aria-label', 'Maximum rank reached');
      progressBar.setAttribute('aria-valuemax', '1');
      progressBar.setAttribute('aria-valuenow', '1');
    }
    if (fill) fill.style.width = '100%';
    if (next) {
      next.classList.add('is-maximum');
      next.textContent = 'Maximum rank reached';
    }
  }
}

function animateAssignmentNumber(element, from, to, format, duration = assignmentXpAnimationDurationMs) {
  if (!element) return Promise.resolve();

  return new Promise(resolve => {
    let startedAt = 0;
    const update = timestamp => {
      startedAt ||= timestamp;
      const elapsed = Math.min((timestamp - startedAt) / duration, 1);
      const eased = 1 - Math.pow(1 - elapsed, 3);
      element.textContent = format(Math.round(from + (to - from) * eased));
      if (elapsed < 1) {
        requestAnimationFrame(update);
      } else {
        resolve();
      }
    };
    requestAnimationFrame(update);
  });
}

function animateAssignmentRankProgress(fill, width, duration = assignmentRankProgressAnimationDurationMs) {
  return new Promise(resolve => {
    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      fill.removeEventListener('transitionend', onTransitionEnd);
      window.clearTimeout(timeoutId);
      resolve();
    };
    const onTransitionEnd = event => {
      if (event.propertyName === 'width') finish();
    };
    const timeoutId = window.setTimeout(finish, duration + 100);

    fill.addEventListener('transitionend', onTransitionEnd);
    requestAnimationFrame(() => {
      fill.style.setProperty('--assignment-rank-progress-duration', `${duration}ms`);
      fill.style.width = width;
    });
  });
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
