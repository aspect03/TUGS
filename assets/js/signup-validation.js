(() => {
  const namePattern = /^[\p{L}][\p{L}\p{M} .'\u2019-]*$/u;
  const usernamePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{2,29}$/;
  const emailPattern = /^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$/;
  const isValidName = (value, max = 50, optional = false) => {
    const text = value.trim();
    return (optional && !text) || (text.length >= 1 && text.length <= max && namePattern.test(text));
  };
  const isValidEmail = value => {
    const text = value.trim(), at = text.indexOf('@');
    return text.length <= 254 && at > 0 && at <= 64 && emailPattern.test(text);
  };
  const form = document.getElementById('registerForm');
  if (!form) return;
  const fieldMessage = input => {
    const text = input.value.trim();
    if (['firstName', 'middleName', 'lastName', 'suffix'].includes(input.id) || input.classList.contains('member-name')) {
      const optional = ['middleName', 'suffix'].includes(input.id);
      const max = input.id === 'suffix' ? 10 : 50;
      return isValidName(text, max, optional) ? '' : `Use letters, spaces, apostrophes, periods or hyphens (1-${max} characters).`;
    }
    if (input.id === 'username') return usernamePattern.test(text) ? '' : 'Use 3-30 characters: letters, numbers, dots, underscores or hyphens. Start with a letter or number.';
    if (input.id === 'email') return isValidEmail(text) ? '' : 'Enter a complete email address, such as name@example.com.';
    if (['stageName', 'productionName'].includes(input.id)) return text.length && text.length <= input.maxLength ? '' : `Enter a name with 1-${input.maxLength} characters.`;
    return '';
  };
  function validateInput(input) {
    const message = fieldMessage(input);
    input.setCustomValidity(message);
    input.setAttribute('aria-invalid', String(Boolean(message)));
    if (input.id) {
      let error = document.getElementById(input.id + 'Error');
      if (!error) {
        error = document.createElement('p');
        error.id = input.id + 'Error';
        error.className = 'signup-field-error';
        error.setAttribute('aria-live', 'polite');
        input.insertAdjacentElement('afterend', error);
        input.setAttribute('aria-describedby', [input.getAttribute('aria-describedby'), error.id].filter(Boolean).join(' '));
      }
      error.textContent = message;
      error.hidden = !message;
    }
    return !message;
  }
  const fields = () => form.querySelectorAll('#firstName,#middleName,#lastName,#suffix,#username,#email,#stageName,#productionName,.member-name');
  function validateForm() {
    fields().forEach(input => { input.value = input.value.trim(); validateInput(input); });
    const members = [...form.querySelectorAll('.member-name')].map(input => input.value.trim()).filter(Boolean).join(', ');
    const firstMember = form.querySelector('.member-name');
    if (firstMember && members.length > 600) firstMember.setCustomValidity('Keep the full member list within 600 characters.');
    const valid = form.checkValidity();
    if (!valid) form.reportValidity();
    return valid;
  }
  window.SignupValidation = { isValidName, isValidEmail, validateForm };
  fields().forEach(input => {
    if (['firstName', 'middleName', 'lastName', 'suffix'].includes(input.id)) {
      const wrapper = document.createElement('div');
      const label = document.createElement('label');
      label.htmlFor = input.id;
      label.className = 'signup-name-label';
      label.textContent = input.getAttribute('aria-label');
      input.before(wrapper); wrapper.append(label, input);
    } else {
      const label = input.parentElement.querySelector('label');
      if (label) label.htmlFor = input.id;
    }
  });
  for (const [id, text] of [['username','3-30 characters. Letters, numbers, dots, underscores and hyphens.'],['email','Use an inbox you can open. We will ask for the verification code sent there.']]) {
    const input = document.getElementById(id), note = document.createElement('p');
    note.id = id + 'Hint'; note.className = 'signup-field-hint'; note.textContent = text;
    input.insertAdjacentElement('afterend', note); input.setAttribute('aria-describedby', note.id);
  }
  form.addEventListener('input', event => { if (event.target.matches('input:not([type="password"]):not([type="checkbox"])')) validateInput(event.target); });
  form.addEventListener('focusout', event => { if ([...fields()].includes(event.target)) { event.target.value = event.target.value.trim(); validateInput(event.target); } });
  document.getElementById('initiateBtn').addEventListener('click', event => {
    if (!validateForm()) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  form.addEventListener('submit', event => { event.preventDefault(); document.getElementById('initiateBtn').click(); });
})();
