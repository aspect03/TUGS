(function () {
  function formatDateTime(value) {
    if (!value) return 'Not enabled';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return 'Enabled';
    return date.toLocaleString('en-US', {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: 'numeric',
      minute: '2-digit'
    });
  }

  function buildCardHtml(title) {
    return `
      <div class="rounded-[24px] border border-white/10 bg-black/20 p-6 space-y-5">
        <div class="flex flex-col md:flex-row md:items-start md:justify-between gap-4">
          <div>
            <h3 class="text-sm font-bold uppercase tracking-widest text-white/50">${title}</h3>
            <p class="text-sm text-white/55 mt-3">Protect this account with an authenticator app code after password sign-in.</p>
          </div>
          <div class="shrink-0">
            <span data-mfa-badge class="inline-flex items-center gap-2 px-3 py-2 rounded-full border border-white/10 bg-white/5 text-[10px] font-bold uppercase tracking-widest text-white/60">Checking status</span>
          </div>
        </div>
        <div class="grid grid-cols-1 md:grid-cols-2 gap-4 text-xs">
          <div class="rounded-2xl border border-white/10 bg-white/5 p-4">
            <p class="text-white/35 uppercase tracking-widest">Status</p>
            <p data-mfa-status class="text-white font-semibold mt-2">Loading MFA status...</p>
          </div>
          <div class="rounded-2xl border border-white/10 bg-white/5 p-4">
            <p class="text-white/35 uppercase tracking-widest">Enrolled</p>
            <p data-mfa-enrolled class="text-white font-semibold mt-2">Not enabled</p>
          </div>
        </div>
        <div data-mfa-setup class="hidden rounded-2xl border border-amber-500/20 bg-amber-500/10 p-5 space-y-4">
          <div>
            <p class="text-[10px] uppercase tracking-widest text-amber-200/80">Setup in Progress</p>
            <p class="text-sm text-white/75 mt-2">Scan the QR with your authenticator app or enter the manual key, then submit the 6-digit code.</p>
          </div>
          <div class="grid grid-cols-1 lg:grid-cols-[180px_minmax(0,1fr)] gap-4">
            <div class="rounded-2xl border border-white/10 bg-black/30 p-3 flex items-center justify-center min-h-[180px]">
              <img data-mfa-qr alt="MFA QR code" class="max-w-full max-h-[160px] rounded-xl hidden">
              <p data-mfa-qr-empty class="text-xs text-white/40 text-center">QR preview will appear here</p>
            </div>
            <div class="space-y-4">
              <div>
                <label class="block text-[10px] font-bold text-white/60 uppercase tracking-[0.18em] mb-2">Manual Key</label>
                <input data-mfa-manual type="text" readonly class="w-full px-4 py-3 bg-black/40 border border-white/10 rounded-xl text-sm outline-none text-white">
              </div>
              <div>
                <label class="block text-[10px] font-bold text-white/60 uppercase tracking-[0.18em] mb-2">Authenticator Code</label>
                <input data-mfa-code type="text" inputmode="numeric" maxlength="6" placeholder="Enter 6-digit code" class="w-full px-4 py-3 bg-black/40 border border-white/10 rounded-xl text-sm outline-none text-white focus:border-red-500">
              </div>
              <div class="flex flex-wrap gap-3">
                <button data-mfa-confirm type="button" class="px-4 py-3 bg-red-600 text-white text-xs font-bold uppercase tracking-widest rounded-xl hover:bg-red-500 transition-colors">Confirm MFA</button>
                <button data-mfa-cancel type="button" class="px-4 py-3 bg-white/5 border border-white/10 text-white text-xs font-bold uppercase tracking-widest rounded-xl hover:bg-white/10 transition-colors">Cancel Setup</button>
              </div>
            </div>
          </div>
        </div>
        <div class="flex flex-wrap gap-3">
          <button data-mfa-enable type="button" class="px-4 py-3 bg-emerald-500/15 border border-emerald-500/25 text-emerald-300 text-xs font-bold uppercase tracking-widest rounded-xl hover:bg-emerald-500/20 transition-colors">Enable MFA</button>
          <button data-mfa-disable type="button" class="hidden px-4 py-3 bg-red-500/10 border border-red-500/20 text-red-300 text-xs font-bold uppercase tracking-widest rounded-xl hover:bg-red-500/15 transition-colors">Disable MFA</button>
        </div>
        <p data-mfa-message class="text-xs text-white/45">MFA is optional until you enable it.</p>
      </div>
    `;
  }

  async function safeJson(response) {
    return response.json().catch(() => ({}));
  }

  function getAlertFn() {
    return window.showSystemAlert
      ? (message, title) => window.showSystemAlert(message, title)
      : (message) => Promise.resolve(window.alert(message));
  }

  function getPromptFn() {
    return window.systemDialog?.prompt
      ? (message, options) => window.systemDialog.prompt(message, options)
      : (message) => Promise.resolve(window.prompt(message));
  }

  window.initDashboardMfaSettings = function initDashboardMfaSettings(config = {}) {
    const container = document.getElementById(config.containerId);
    if (!container) return;

    container.innerHTML = buildCardHtml(config.title || 'Multi-Factor Authentication');

    const badge = container.querySelector('[data-mfa-badge]');
    const status = container.querySelector('[data-mfa-status]');
    const enrolled = container.querySelector('[data-mfa-enrolled]');
    const setupPanel = container.querySelector('[data-mfa-setup]');
    const qr = container.querySelector('[data-mfa-qr]');
    const qrEmpty = container.querySelector('[data-mfa-qr-empty]');
    const manual = container.querySelector('[data-mfa-manual]');
    const code = container.querySelector('[data-mfa-code]');
    const message = container.querySelector('[data-mfa-message]');
    const enableBtn = container.querySelector('[data-mfa-enable]');
    const disableBtn = container.querySelector('[data-mfa-disable]');
    const confirmBtn = container.querySelector('[data-mfa-confirm]');
    const cancelBtn = container.querySelector('[data-mfa-cancel]');
    const alertFn = getAlertFn();
    const promptFn = getPromptFn();

    let enabled = false;
    let setupToken = '';

    function setMessage(text, tone = 'text-white/45') {
      message.className = `text-xs ${tone}`;
      message.textContent = text;
    }

    function setEnabledState(nextEnabled, enrolledAt) {
      enabled = !!nextEnabled;
      badge.textContent = enabled ? 'MFA Enabled' : 'MFA Disabled';
      badge.className = `inline-flex items-center gap-2 px-3 py-2 rounded-full border text-[10px] font-bold uppercase tracking-widest ${enabled ? 'border-emerald-500/25 bg-emerald-500/10 text-emerald-300' : 'border-white/10 bg-white/5 text-white/60'}`;
      status.textContent = enabled ? 'Authenticator app required at login' : 'Password-only sign-in';
      enrolled.textContent = enabled ? formatDateTime(enrolledAt) : 'Not enabled';
      enableBtn.classList.toggle('hidden', enabled);
      disableBtn.classList.toggle('hidden', !enabled);
      if (enabled) {
        setupPanel.classList.add('hidden');
        setupToken = '';
        code.value = '';
      }
    }

    async function refreshStatus() {
      setMessage('Checking MFA status...');
      try {
        const response = await fetch('/api/auth/mfa/status');
        const data = await safeJson(response);
        if (!response.ok) {
          throw new Error(data.message || 'Failed to load MFA status.');
        }
        setEnabledState(data.enabled, data.enrolledAt);
        setMessage(data.enabled ? 'This account is protected with a time-based authenticator code.' : 'MFA is optional until you enable it.');
      } catch (error) {
        setMessage(error.message || 'Failed to load MFA status.', 'text-red-400');
      }
    }

    async function startSetup() {
      enableBtn.disabled = true;
      setMessage('Starting MFA setup...');
      try {
        const response = await fetch('/api/auth/mfa/enroll', { method: 'POST' });
        const data = await safeJson(response);
        if (!response.ok) {
          throw new Error(data.message || 'Failed to start MFA setup.');
        }

        setupToken = data.setupToken || '';
        manual.value = data.manualEntryKey || '';
        code.value = '';
        if (data.otpAuthUri) {
          qr.src = `https://api.qrserver.com/v1/create-qr-code/?size=180x180&data=${encodeURIComponent(data.otpAuthUri)}`;
          qr.classList.remove('hidden');
          qrEmpty.classList.add('hidden');
        }
        setupPanel.classList.remove('hidden');
        setMessage('Scan the QR or enter the manual key, then confirm with the 6-digit code.');
      } catch (error) {
        setMessage(error.message || 'Failed to start MFA setup.', 'text-red-400');
      } finally {
        enableBtn.disabled = false;
      }
    }

    async function confirmSetup() {
      const enteredCode = code.value.trim();
      if (!setupToken || !enteredCode) {
        setMessage('Enter the 6-digit authenticator code to finish setup.', 'text-amber-300');
        return;
      }

      confirmBtn.disabled = true;
      setMessage('Confirming MFA...');
      try {
        const response = await fetch('/api/auth/mfa/verify-setup', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ setupToken, code: enteredCode })
        });
        const data = await safeJson(response);
        if (!response.ok) {
          throw new Error(data.message || 'Failed to confirm MFA setup.');
        }

        await refreshStatus();
        await alertFn(data.message || 'MFA enabled successfully.', 'MFA Enabled');
      } catch (error) {
        setMessage(error.message || 'Failed to confirm MFA setup.', 'text-red-400');
      } finally {
        confirmBtn.disabled = false;
      }
    }

    async function disableMfa() {
      const enteredCode = await promptFn(
        'Enter the 6-digit authenticator code to disable MFA.',
        {
          title: 'Disable MFA',
          kicker: 'Account Security',
          placeholder: '6-digit authenticator code',
          okText: 'Disable',
          cancelText: 'Cancel',
          required: true
        }
      );
      if (!enteredCode) return;

      disableBtn.disabled = true;
      setMessage('Disabling MFA...');
      try {
        const response = await fetch('/api/auth/mfa/disable', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ code: enteredCode })
        });
        const data = await safeJson(response);
        if (!response.ok) {
          throw new Error(data.message || 'Failed to disable MFA.');
        }

        await refreshStatus();
        await alertFn(data.message || 'MFA disabled successfully.', 'MFA Disabled');
      } catch (error) {
        setMessage(error.message || 'Failed to disable MFA.', 'text-red-400');
      } finally {
        disableBtn.disabled = false;
      }
    }

    enableBtn.addEventListener('click', startSetup);
    confirmBtn.addEventListener('click', confirmSetup);
    cancelBtn.addEventListener('click', () => {
      setupToken = '';
      code.value = '';
      manual.value = '';
      qr.src = '';
      qr.classList.add('hidden');
      qrEmpty.classList.remove('hidden');
      setupPanel.classList.add('hidden');
      setMessage('MFA setup cancelled. You can start again any time.');
    });
    disableBtn.addEventListener('click', disableMfa);

    refreshStatus();
  };
})();
