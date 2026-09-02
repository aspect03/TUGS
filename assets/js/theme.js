/**
 * Imajination Theme Manager  — runs in <head>, before CSS renders.
 * - No transitions on initial load (prevents dark→light flash on every page load).
 * - Smooth transitions only when the user actively toggles.
 * - Hard-reloads the page when the Service Worker activates a new version.
 */

(function () {
  'use strict';

  var STORAGE_KEY = 'imajination-theme';
  var LIGHT_CLASS  = 'light-mode';

  // ── Read stored preference (default: light) ───────────────────────────────
  function getTheme() {
    try { return localStorage.getItem(STORAGE_KEY) || 'light'; } catch { return 'light'; }
  }

  // ── Apply theme immediately without animation ─────────────────────────────
  function applyTheme(theme) {
    var html = document.documentElement;
    // Block all CSS transitions while applying initial theme (avoids FOUC flash)
    html.style.setProperty('--theme-transition', 'none');

    if (theme === 'light') {
      html.classList.add(LIGHT_CLASS);
      html.setAttribute('data-theme', 'light');
    } else {
      html.classList.remove(LIGHT_CLASS);
      html.setAttribute('data-theme', 'dark');
    }

    // Re-enable transitions after one paint so user interactions feel smooth
    requestAnimationFrame(function () {
      requestAnimationFrame(function () {
        html.style.removeProperty('--theme-transition');
      });
    });
  }

  // ── Persist + apply with smooth transition (user-initiated only) ──────────
  function setTheme(theme) {
    try { localStorage.setItem(STORAGE_KEY, theme); } catch {}
    var html = document.documentElement;
    html.classList.add('theme-transitioning');
    if (theme === 'light') {
      html.classList.add(LIGHT_CLASS);
      html.setAttribute('data-theme', 'light');
    } else {
      html.classList.remove(LIGHT_CLASS);
      html.setAttribute('data-theme', 'dark');
    }
    setTimeout(function () { html.classList.remove('theme-transitioning'); }, 350);
    updateToggleUI(theme);
  }

  function toggleTheme() {
    setTheme(getTheme() === 'dark' ? 'light' : 'dark');
  }

  // ── Sync toggle button labels/icons ──────────────────────────────────────
  function updateToggleUI(theme) {
    try {
      document.querySelectorAll('[data-theme-toggle]').forEach(function (btn) {
        var sun   = btn.querySelector('[data-theme-sun]');
        var moon  = btn.querySelector('[data-theme-moon]');
        var label = btn.querySelector('[data-theme-label]');
        if (sun)   sun.style.display   = theme === 'dark'  ? '' : 'none';
        if (moon)  moon.style.display  = theme === 'light' ? '' : 'none';
        if (label) label.textContent   = theme === 'dark'  ? 'Light Mode' : 'Dark Mode';
      });
    } catch {}
  }

  // ── Hard-reload when Service Worker activates a new build ────────────────
  if ('serviceWorker' in navigator) {
    navigator.serviceWorker.addEventListener('message', function (event) {
      if (event.data && event.data.type === 'SW_UPDATED') {
        // Wait a tick so the SW finishes claiming clients, then force-reload
        setTimeout(function () { window.location.reload(true); }, 200);
      }
    });
  }

  // ── Apply immediately (before CSS renders) ────────────────────────────────
  applyTheme(getTheme());

  // Re-sync toggle buttons once the DOM is ready
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () { updateToggleUI(getTheme()); });
  } else {
    updateToggleUI(getTheme());
  }

  // ── Register / update Service Worker ─────────────────────────────────────
  if ('serviceWorker' in navigator) {
    window.addEventListener('load', function () {
      navigator.serviceWorker.register('/sw.js')
        .then(function (reg) {
          // Check for updates every 60 minutes
          setInterval(function () { reg.update(); }, 60 * 60 * 1000);
        })
        .catch(function (err) { console.warn('[SW] Registration failed:', err); });
    });
  }

  // ── Global API ────────────────────────────────────────────────────────────
  window.ImajiTheme = {
    get: getTheme,
    set: setTheme,
    toggle: toggleTheme,
    apply: applyTheme
  };
})();
