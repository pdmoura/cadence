// Small progressive enhancements. The app works without JavaScript.
(function () {
  const body = document.body;
  const toggle = document.querySelector('[data-toggle-nav]');
  const scrim = document.querySelector('[data-scrim]');
  if (toggle && scrim) {
    toggle.addEventListener('click', () => body.classList.toggle('nav-open'));
    scrim.addEventListener('click', () => body.classList.remove('nav-open'));
  }

  document.querySelectorAll('[data-toast]').forEach((el) => {
    setTimeout(() => el.remove(), 5000);
  });

  document.querySelectorAll('[data-copy]').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const target = document.querySelector(btn.getAttribute('data-copy'));
      if (!target) return;
      try {
        await navigator.clipboard.writeText(target.textContent || '');
        const old = btn.textContent;
        btn.textContent = 'Copied';
        setTimeout(() => (btn.textContent = old), 1500);
      } catch { /* clipboard blocked: the text is still selectable */ }
    });
  });

  document.querySelectorAll('[data-confirm]').forEach((form) => {
    form.addEventListener('submit', (e) => {
      if (!window.confirm(form.getAttribute('data-confirm'))) e.preventDefault();
    });
  });

  // Table rows that link to a detail page.
  document.querySelectorAll('tr[data-href]').forEach((row) => {
    row.addEventListener('click', (e) => {
      if (e.target.closest('a, button, form')) return;
      window.location.href = row.getAttribute('data-href');
    });
  });
})();
