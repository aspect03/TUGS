(function () {
  if (window.systemDialog) return;

  function normalizeMessage(message) {
    const text = String(message ?? '').trim();
    if (!text) return 'Something went wrong. Please try again.';

    const lowered = text.toLowerCase();
    const technicalPatterns = [
      'select ',
      'insert ',
      'update ',
      'delete ',
      ' from ',
      ' where ',
      'exception',
      'stack trace',
      'stacktrace',
      'npgsql',
      'sqlstate',
      'syntax error',
      'database error',
      'db fetch error',
      'checkout logic error',
      'a command is already in progress',
      'inner exception',
      ' at ',
      'microsoft.aspnetcore',
      'system.'
    ];

    if (technicalPatterns.some((pattern) => lowered.includes(pattern))) {
      return 'Unable to complete this request right now. Please try again in a moment.';
    }

    if (lowered.startsWith('server error:')) {
      return text.replace(/^server error:\s*/i, '').trim() || 'Unable to complete this request right now.';
    }

    if (lowered.startsWith('backend error:')) {
      return 'Unable to complete this request right now. Please try again in a moment.';
    }

    if (lowered.includes('failed to connect to the c# server') || lowered.includes('failed to connect to the server')) {
      return 'Unable to connect right now. Please check the server and try again.';
    }

    if (lowered.includes('is your c# api running')) {
      return 'Unable to connect right now. Please make sure the server is running.';
    }

    return text;
  }

  const style = document.createElement('style');
  style.textContent = `
    .system-dialog-overlay {
      position: fixed;
      inset: 0;
      z-index: 2000;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 1rem;
      background: rgba(0, 0, 0, 0.78);
      backdrop-filter: blur(10px);
    }
    .system-dialog-card {
      width: min(100%, 30rem);
      border-radius: 1.5rem;
      border: 1px solid rgba(255, 255, 255, 0.12);
      background:
        radial-gradient(circle at top, rgba(239, 68, 68, 0.14), transparent 55%),
        rgba(17, 17, 17, 0.96);
      box-shadow: 0 24px 70px rgba(0, 0, 0, 0.45);
      color: white;
      overflow: hidden;
    }
    .system-dialog-body {
      padding: 1.5rem 1.5rem 1rem;
    }
    .system-dialog-kicker {
      font-size: 0.65rem;
      font-weight: 700;
      letter-spacing: 0.22em;
      text-transform: uppercase;
      color: rgba(248, 113, 113, 0.95);
      margin-bottom: 0.5rem;
    }
    .system-dialog-title {
      font-size: 1.15rem;
      font-weight: 700;
      line-height: 1.3;
      margin: 0;
    }
    .system-dialog-message {
      margin-top: 0.85rem;
      font-size: 0.95rem;
      line-height: 1.7;
      color: rgba(255, 255, 255, 0.72);
      white-space: pre-wrap;
    }
    .system-dialog-input,
    .system-dialog-textarea {
      width: 100%;
      margin-top: 1rem;
      border-radius: 1rem;
      border: 1px solid rgba(255, 255, 255, 0.1);
      background: rgba(0, 0, 0, 0.45);
      color: white;
      padding: 0.9rem 1rem;
      outline: none;
      font-size: 0.92rem;
    }
    .system-dialog-textarea {
      min-height: 7rem;
      resize: vertical;
    }
    .system-dialog-input:focus,
    .system-dialog-textarea:focus {
      border-color: rgba(239, 68, 68, 0.5);
      box-shadow: 0 0 0 3px rgba(239, 68, 68, 0.15);
    }
    .system-dialog-actions {
      display: flex;
      justify-content: flex-end;
      gap: 0.75rem;
      padding: 0 1.5rem 1.5rem;
    }
    .system-dialog-btn {
      min-width: 7rem;
      border-radius: 9999px;
      padding: 0.8rem 1.15rem;
      font-size: 0.72rem;
      font-weight: 700;
      letter-spacing: 0.18em;
      text-transform: uppercase;
      transition: 0.2s ease;
      border: 1px solid transparent;
    }
    .system-dialog-btn:disabled {
      opacity: 0.65;
      cursor: not-allowed;
    }
    .system-dialog-btn-secondary {
      background: rgba(255, 255, 255, 0.05);
      border-color: rgba(255, 255, 255, 0.1);
      color: rgba(255, 255, 255, 0.82);
    }
    .system-dialog-btn-secondary:hover {
      background: rgba(255, 255, 255, 0.1);
    }
    .system-dialog-btn-primary {
      background: linear-gradient(135deg, #dc2626, #ef4444);
      color: white;
      box-shadow: 0 12px 28px rgba(220, 38, 38, 0.28);
    }
    .system-dialog-btn-primary:hover {
      filter: brightness(1.05);
    }
  `;
  document.head.appendChild(style);

  function buildDialog(options) {
    const overlay = document.createElement('div');
    overlay.className = 'system-dialog-overlay';

    const card = document.createElement('div');
    card.className = 'system-dialog-card';

    const body = document.createElement('div');
    body.className = 'system-dialog-body';

    if (options.kicker) {
      const kicker = document.createElement('p');
      kicker.className = 'system-dialog-kicker';
      kicker.textContent = options.kicker;
      body.appendChild(kicker);
    }

    const title = document.createElement('h3');
    title.className = 'system-dialog-title';
    title.textContent = options.title || 'Notice';
    body.appendChild(title);

    if (options.message) {
      const message = document.createElement('p');
      message.className = 'system-dialog-message';
      message.textContent = options.message;
      body.appendChild(message);
    }

    let input = null;
    if (options.input) {
      input = document.createElement(options.multiline ? 'textarea' : 'input');
      input.className = options.multiline ? 'system-dialog-textarea' : 'system-dialog-input';
      if (!options.multiline) input.type = 'text';
      input.placeholder = options.placeholder || '';
      input.value = options.defaultValue || '';
      body.appendChild(input);
    }

    const actions = document.createElement('div');
    actions.className = 'system-dialog-actions';

    card.appendChild(body);
    card.appendChild(actions);
    overlay.appendChild(card);
    document.body.appendChild(overlay);

    return { overlay, actions, input };
  }

  function closeDialog(overlay) {
    if (overlay && overlay.parentNode) overlay.parentNode.removeChild(overlay);
  }

  function alertDialog(message, options = {}) {
    return new Promise((resolve) => {
      const { overlay, actions } = buildDialog({
        kicker: options.kicker || 'System Message',
        title: options.title || 'Notice',
        message: normalizeMessage(message)
      });

      const ok = document.createElement('button');
      ok.type = 'button';
      ok.className = 'system-dialog-btn system-dialog-btn-primary';
      ok.textContent = options.okText || 'Okay';
      ok.addEventListener('click', () => {
        closeDialog(overlay);
        resolve();
      });
      actions.appendChild(ok);
      ok.focus();

      overlay.addEventListener('click', (event) => {
        if (event.target === overlay) {
          closeDialog(overlay);
          resolve();
        }
      });
    });
  }

  function confirmDialog(message, options = {}) {
    return new Promise((resolve) => {
      const { overlay, actions } = buildDialog({
        kicker: options.kicker || 'Confirm Action',
        title: options.title || 'Please Confirm',
        message: String(message ?? '')
      });

      const cancel = document.createElement('button');
      cancel.type = 'button';
      cancel.className = 'system-dialog-btn system-dialog-btn-secondary';
      cancel.textContent = options.cancelText || 'Cancel';
      cancel.addEventListener('click', () => {
        closeDialog(overlay);
        resolve(false);
      });

      const ok = document.createElement('button');
      ok.type = 'button';
      ok.className = 'system-dialog-btn system-dialog-btn-primary';
      ok.textContent = options.okText || 'Confirm';
      ok.addEventListener('click', () => {
        closeDialog(overlay);
        resolve(true);
      });

      actions.appendChild(cancel);
      actions.appendChild(ok);
      ok.focus();

      overlay.addEventListener('click', (event) => {
        if (event.target === overlay) {
          closeDialog(overlay);
          resolve(false);
        }
      });
    });
  }

  function promptDialog(message, options = {}) {
    return new Promise((resolve) => {
      const { overlay, actions, input } = buildDialog({
        kicker: options.kicker || 'Input Needed',
        title: options.title || 'Enter Details',
        message: String(message ?? ''),
        input: true,
        multiline: !!options.multiline,
        placeholder: options.placeholder || '',
        defaultValue: options.defaultValue || ''
      });

      const cancel = document.createElement('button');
      cancel.type = 'button';
      cancel.className = 'system-dialog-btn system-dialog-btn-secondary';
      cancel.textContent = options.cancelText || 'Cancel';
      cancel.addEventListener('click', () => {
        closeDialog(overlay);
        resolve(null);
      });

      const ok = document.createElement('button');
      ok.type = 'button';
      ok.className = 'system-dialog-btn system-dialog-btn-primary';
      ok.textContent = options.okText || 'Submit';
      ok.addEventListener('click', () => {
        const value = (input?.value || '').trim();
        if (options.required && !value) {
          input?.focus();
          return;
        }
        closeDialog(overlay);
        resolve(value);
      });

      actions.appendChild(cancel);
      actions.appendChild(ok);

      input?.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
          closeDialog(overlay);
          resolve(null);
        }
        if (!options.multiline && event.key === 'Enter') {
          event.preventDefault();
          ok.click();
        }
      });

      window.requestAnimationFrame(() => {
        input?.focus();
        input?.setSelectionRange?.(input.value.length, input.value.length);
      });

      overlay.addEventListener('click', (event) => {
        if (event.target === overlay) {
          closeDialog(overlay);
          resolve(null);
        }
      });
    });
  }

  window.systemDialog = {
    alert: alertDialog,
    confirm: confirmDialog,
    prompt: promptDialog,
    normalizeMessage
  };

  window.alert = function (message) {
    return alertDialog(message);
  };
})();
