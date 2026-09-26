// Progressive enhancements. Every page works without JavaScript; this adds the nicer controls.
(function () {
  const root = document.documentElement;
  const body = document.body;

  // ── Navigation drawer (phones) ─────────────────────────────────────────
  const toggle = document.querySelector('[data-toggle-nav]');
  const scrim = document.querySelector('[data-scrim]');
  if (toggle && scrim) {
    toggle.addEventListener('click', () => body.classList.toggle('nav-open'));
    scrim.addEventListener('click', () => body.classList.remove('nav-open'));
  }

  // ── Theme: light / dark / system, remembered per browser ───────────────
  const THEME_KEY = 'cadence-theme';
  function readTheme() { try { return localStorage.getItem(THEME_KEY) || 'system'; } catch { return 'system'; } }
  function applyTheme(value) {
    if (value === 'light' || value === 'dark') root.setAttribute('data-theme', value);
    else root.removeAttribute('data-theme');
    document.querySelectorAll('[data-theme-set]').forEach((b) => b.setAttribute('aria-pressed', String(b.getAttribute('data-theme-set') === value)));
  }
  document.querySelectorAll('[data-theme-set]').forEach((b) => {
    b.addEventListener('click', () => {
      const value = b.getAttribute('data-theme-set');
      try { localStorage.setItem(THEME_KEY, value); } catch { /* private mode: still applies for this page */ }
      applyTheme(value);
    });
  });
  applyTheme(readTheme());

  // ── Toasts, copy buttons, confirmations, row links, loading states ─────
  document.querySelectorAll('[data-toast]').forEach((el) => setTimeout(() => el.remove(), 6000));

  document.querySelectorAll('[data-copy]').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const target = document.querySelector(btn.getAttribute('data-copy'));
      if (!target) return;
      const text = 'value' in target && target.value !== undefined && target.tagName !== 'PRE' ? target.value : target.textContent;
      try {
        await navigator.clipboard.writeText(text || '');
        const old = btn.textContent;
        btn.textContent = 'Copied';
        setTimeout(() => (btn.textContent = old), 1500);
      } catch { if (target.select) target.select(); }
    });
  });

  document.querySelectorAll('form[data-confirm]').forEach((form) => {
    form.addEventListener('submit', (e) => { if (!window.confirm(form.getAttribute('data-confirm'))) e.preventDefault(); });
  });

  document.querySelectorAll('tr[data-href]').forEach((row) => {
    row.addEventListener('click', (e) => {
      if (e.target.closest('a, button, form')) return;
      window.location.href = row.getAttribute('data-href');
    });
  });

  // Buttons that trigger slow work (AI research, connection tests) show a spinner.
  document.querySelectorAll('form[data-loading]').forEach((form) => {
    form.addEventListener('submit', () => {
      const btn = form.querySelector('button[type="submit"], button:not([type])');
      if (btn) { btn.classList.add('is-loading'); btn.setAttribute('aria-busy', 'true'); }
    });
  });

  // AI settings: show the fields for the chosen provider.
  document.querySelectorAll('form[data-provider]').forEach((form) => {
    form.addEventListener('change', (e) => {
      if (e.target.name === 'aiProvider') form.setAttribute('data-provider', e.target.value);
    });
  });

  // ── Dropdowns: replace <select class="select"> with an accessible listbox ─
  const CHEVRON = '<svg class="ic" viewBox="0 0 24 24" aria-hidden="true"><path d="m6 9 6 6 6-6"/></svg>';
  const CHECK = '<svg class="ic check" viewBox="0 0 24 24" aria-hidden="true"><path d="M20 6 9 17l-5-5"/></svg>';
  let uid = 0;
  let openDropdown = null;

  function escapeHtml(s) { return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

  function enhance(select) {
    if (select.dataset.ddReady || select.multiple) return;
    select.dataset.ddReady = '1';
    const id = 'dd' + (++uid);
    const customTrigger = select.dataset.ddTrigger === 'parent' ? select.parentElement : null;

    let wrap, trigger;
    if (customTrigger) {
      wrap = customTrigger;
      trigger = customTrigger;
      trigger.classList.add('dd-trigger');
      trigger.setAttribute('tabindex', '0');
      trigger.setAttribute('role', 'button');
      select.style.display = 'none';
    } else {
      wrap = document.createElement('div');
      wrap.className = 'dd' + (select.classList.contains('inline') || select.style.width === 'auto' ? ' inline' : '');
      select.parentNode.insertBefore(wrap, select);
      wrap.appendChild(select);
      select.classList.add('dd-native');
      select.tabIndex = -1;
      select.setAttribute('aria-hidden', 'true');
      trigger = document.createElement('button');
      trigger.type = 'button';
      trigger.className = 'dd-btn';
      trigger.id = select.id ? select.id + '-dd' : id + '-btn';
      if (select.id) {
        const label = document.querySelector(`label[for="${select.id}"]`);
        if (label) { label.htmlFor = trigger.id; trigger.setAttribute('aria-labelledby', (label.id ||= id + '-label') + ' ' + trigger.id); }
      }
      if (select.getAttribute('aria-label')) trigger.setAttribute('aria-label', select.getAttribute('aria-label'));
      trigger.innerHTML = '<span class="dd-value"></span>' + CHEVRON;
      wrap.appendChild(trigger);
    }
    trigger.setAttribute('aria-haspopup', 'listbox');
    trigger.setAttribute('aria-expanded', 'false');

    const list = document.createElement('ul');
    list.className = 'dd-list' + (select.dataset.ddPlacement === 'top' ? ' up' : '');
    list.id = id + '-list';
    list.setAttribute('role', 'listbox');
    list.tabIndex = -1;
    list.hidden = true;
    trigger.setAttribute('aria-controls', list.id);
    wrap.appendChild(list);

    const options = Array.from(select.options);
    options.forEach((opt, i) => {
      const li = document.createElement('li');
      li.className = 'dd-opt';
      li.id = `${id}-opt-${i}`;
      li.setAttribute('role', 'option');
      li.dataset.index = String(i);
      const avatar = opt.dataset.avatar ? `<span class="avatar sm" aria-hidden="true">${escapeHtml(opt.dataset.avatar)}</span>` : '';
      const main = escapeHtml(opt.dataset.title || opt.text);
      const sub = opt.dataset.sub ? `<span>${escapeHtml(opt.dataset.sub)}</span>` : '';
      li.innerHTML = `${avatar}<span class="dd-text"><b>${main}</b>${sub}</span>${CHECK}`;
      li.addEventListener('mousedown', (e) => e.preventDefault());
      li.addEventListener('click', () => choose(i));
      li.addEventListener('mousemove', () => setActive(i));
      list.appendChild(li);
    });

    let active = select.selectedIndex;
    function render() {
      const opt = select.options[select.selectedIndex];
      if (!customTrigger) {
        const v = trigger.querySelector('.dd-value');
        v.textContent = opt ? (opt.dataset.title || opt.text) : '';
      }
      list.querySelectorAll('.dd-opt').forEach((li, i) => li.setAttribute('aria-selected', String(i === select.selectedIndex)));
    }
    function setActive(i) {
      active = Math.max(0, Math.min(options.length - 1, i));
      list.querySelectorAll('.dd-opt').forEach((li, j) => li.classList.toggle('is-active', j === active));
      const el = list.children[active];
      if (el) { list.setAttribute('aria-activedescendant', el.id); el.scrollIntoView({ block: 'nearest' }); }
    }
    function open() {
      if (openDropdown && openDropdown !== close) openDropdown();
      list.hidden = false;
      trigger.setAttribute('aria-expanded', 'true');
      if (!customTrigger && select.dataset.ddPlacement !== 'top') {
        const r = trigger.getBoundingClientRect();
        list.classList.toggle('up', window.innerHeight - r.bottom < 260 && r.top > 300);
      }
      setActive(select.selectedIndex < 0 ? 0 : select.selectedIndex);
      list.focus({ preventScroll: true });
      openDropdown = close;
    }
    function close(focusTrigger) {
      list.hidden = true;
      trigger.setAttribute('aria-expanded', 'false');
      if (openDropdown === close) openDropdown = null;
      if (focusTrigger) trigger.focus();
    }
    function choose(i) {
      const changed = select.selectedIndex !== i;
      select.selectedIndex = i;
      render();
      close(true);
      if (changed) select.dispatchEvent(new Event('change', { bubbles: true }));
    }

    trigger.addEventListener('click', (e) => {
      if (e.target.closest('.dd-list')) return;
      list.hidden ? open() : close(true);
    });
    trigger.addEventListener('keydown', (e) => {
      if (e.target !== trigger) return;
      if (['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(e.key)) { e.preventDefault(); open(); }
    });
    let typed = '', typedAt = 0;
    list.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowDown') { e.preventDefault(); setActive(active + 1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(active - 1); }
      else if (e.key === 'Home') { e.preventDefault(); setActive(0); }
      else if (e.key === 'End') { e.preventDefault(); setActive(options.length - 1); }
      else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); choose(active); }
      else if (e.key === 'Escape') { e.preventDefault(); close(true); }
      else if (e.key === 'Tab') close(false);
      else if (e.key.length === 1) {
        const now = Date.now();
        typed = (now - typedAt > 700 ? '' : typed) + e.key.toLowerCase();
        typedAt = now;
        const hit = options.findIndex((o) => (o.dataset.title || o.text).toLowerCase().startsWith(typed));
        if (hit >= 0) setActive(hit);
      }
    });
    list.addEventListener('blur', (e) => { if (!wrap.contains(e.relatedTarget)) close(false); });
    select.addEventListener('change', render);
    render();
  }

  document.querySelectorAll('select.select, select[data-dd-trigger]').forEach(enhance);
  document.addEventListener('click', (e) => { if (openDropdown && !e.target.closest('.dd, .dd-trigger')) openDropdown(false); });
})();
