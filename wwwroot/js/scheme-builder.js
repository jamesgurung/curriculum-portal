const schemeBuilder = (() => {
  const storageKey = `scheme-builder:${courseId}:${unitId}`;
  let state = loadState();

  function loadState() {
    try {
      const saved = localStorage.getItem(storageKey);
      if (!saved) return { content: '' };
      const parsed = JSON.parse(saved);
      return { content: typeof parsed?.content === 'string' ? parsed.content : '' };
    } catch {
      return { content: '' };
    }
  }

  function normaliseText(text) {
    return String(text ?? '')
      .replace(/\r/g, '')
      .replace(/[ \t]+\n/g, '\n')
      .replace(/\n{3,}/g, '\n\n')
      .trim();
  }

  function setText(id, value) {
    const element = document.getElementById(id);
    if (!element) return;
    element.textContent = value;
  }

  function render() {
    setText('scheme-unit-title', unit.title ?? '');
    setText('scheme-term', unit.term ? `Year ${unit.yearGroup} ${unit.term} Term` : `Year ${unit.yearGroup} (Term not configured)`);
    setText('scheme-why-this', unit.whyThis ?? '');
    setText('scheme-why-now', unit.whyNow ?? '');

    const content = document.getElementById('scheme-builder-content');
    if (!content) return;
    content.textContent = state.content;
    content.dataset.text = state.content;
    updateEmptyState();
  }

  function updateEmptyState() {
    const emptyMessage = document.getElementById('scheme-builder-empty');
    if (!emptyMessage) return;
    emptyMessage.classList.toggle('hide', state.content.length > 0 || (tab === 'scheme' && editMode));
  }

  function handlePaste(event) {
    event.preventDefault();
    const text = event.clipboardData.getData('text/plain');
    const selection = window.getSelection();
    if (!selection.rangeCount) return;
    const range = selection.getRangeAt(0);
    range.deleteContents();
    const node = document.createTextNode(text);
    range.insertNode(node);
    range.setStartAfter(node);
    range.collapse(true);
    selection.removeAllRanges();
    selection.addRange(range);
  }

  function handleInput(event) {
    state.content = normaliseText(event.target.innerText);
    event.target.dataset.text = state.content;
    updateEmptyState();
  }

  function setEditMode(isActive) {
    const content = document.getElementById('scheme-builder-content');
    if (!content) return;
    content.removeEventListener('input', handleInput);
    content.removeEventListener('blur', handleInput);
    content.removeEventListener('paste', handlePaste);
    content.contentEditable = isActive;
    content.classList.toggle('editable', isActive);
    if (isActive) {
      content.textContent = content.dataset.text ?? state.content;
      content.addEventListener('input', handleInput);
      content.addEventListener('blur', handleInput);
      content.addEventListener('paste', handlePaste);
    } else {
      content.textContent = state.content;
    }
    updateEmptyState();
  }

  async function save() {
    const content = document.getElementById('scheme-builder-content');
    if (content) {
      state.content = normaliseText(content.innerText);
      content.dataset.text = state.content;
    }
    localStorage.setItem(storageKey, JSON.stringify(state));
    render();
  }

  return { render, save, setEditMode };
})();

window.schemeBuilder = schemeBuilder;
