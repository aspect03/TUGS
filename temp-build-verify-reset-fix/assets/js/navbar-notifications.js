(() => {
  let navbarNotifications = [];
  let refreshHandle = null;

  function $(id) {
    return document.getElementById(id);
  }

  function getCurrentUserId() {
    return (localStorage.getItem('userId') || '').trim();
  }

  function closeNavbarNotificationBell() {
    $('globalNotificationBellDropdown')?.classList.add('hidden');
  }

  function formatRelativeTime(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return 'Just now';
    const diffMinutes = Math.max(0, Math.round((Date.now() - date.getTime()) / 60000));
    if (diffMinutes < 1) return 'Just now';
    if (diffMinutes < 60) return `${diffMinutes}m ago`;
    const diffHours = Math.round(diffMinutes / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.round(diffHours / 24);
    return `${diffDays}d ago`;
  }

  function escapeHtml(value) {
    return String(value ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  async function markNavbarNotificationRead(notificationId) {
    if (!notificationId) return;
    try {
      await fetch(`/api/notification/${notificationId}/read`, { method: 'POST' });
    } catch {
      // Ignore read failures and keep navigation working.
    }
  }

  function getNavbarNotificationHref(notification) {
    if (!notification) return '';
    if (notification.relatedType === 'booking' && notification.relatedId) {
      return `/pages/bookings/messages.html?bookingId=${notification.relatedId}`;
    }
    if (notification.relatedType === 'event' && notification.relatedId) {
      return `/pages/details/EventDetailPage.html?id=${notification.relatedId}`;
    }
    if (notification.relatedType === 'ticket' && notification.relatedId) {
      return '/pages/dashboards/CustomerDashboard.html?view=tickets';
    }
    if (notification.relatedType === 'community_post') {
      return '/pages/browse/Community.html';
    }
    return '';
  }

  async function handleNavbarNotificationClick(index) {
    const notification = navbarNotifications[index];
    if (!notification) return;

    closeNavbarNotificationBell();
    if (!notification.isRead) {
      await markNavbarNotificationRead(notification.id);
    }

    const href = getNavbarNotificationHref(notification);
    if (href) {
      window.location.href = href;
      return;
    }

    await fetchNavbarNotifications();
  }

  function renderNavbarNotifications(notifications) {
    const badge = $('globalNotificationBellBadge');
    const count = $('globalNotificationBellCount');
    const list = $('globalNotificationBellList');
    if (!badge || !count || !list) return;

    navbarNotifications = Array.isArray(notifications) ? notifications : [];
    const unreadCount = navbarNotifications.filter(item => !item.isRead).length;

    badge.textContent = String(unreadCount);
    count.textContent = `${unreadCount} new`;

    if (unreadCount > 0) {
      badge.classList.remove('hidden');
      badge.classList.add('inline-flex');
    } else {
      badge.classList.add('hidden');
      badge.classList.remove('inline-flex');
    }

    if (navbarNotifications.length === 0) {
      list.innerHTML = `
        <div class="p-4 text-center text-sm text-white/50">
          No notifications yet.
        </div>
      `;
      return;
    }

    list.innerHTML = navbarNotifications.map((notification, index) => `
      <button type="button" data-index="${index}" class="global-bell-item w-full text-left p-3 bg-black/30 border ${notification.isRead ? 'border-white/5' : 'border-red-500/20'} rounded-xl hover:border-white/20 transition-colors">
        <div class="flex items-start justify-between gap-3">
          <div>
            <p class="text-sm font-semibold text-white">${escapeHtml(notification.title || 'Notification')}</p>
            <p class="mt-1 text-xs text-white/60 leading-relaxed">${escapeHtml(notification.message || '')}</p>
          </div>
          <span class="shrink-0 text-[10px] uppercase tracking-widest ${notification.isRead ? 'text-white/25' : 'text-red-300'}">${notification.isRead ? 'Read' : 'New'}</span>
        </div>
        <p class="mt-2 text-[10px] uppercase tracking-widest text-white/35">${escapeHtml(formatRelativeTime(notification.createdAt))}</p>
      </button>
    `).join('');

    list.querySelectorAll('.global-bell-item').forEach(button => {
      button.addEventListener('click', () => {
        const index = Number(button.dataset.index);
        handleNavbarNotificationClick(index);
      });
    });

    if (window.lucide?.createIcons) {
      window.lucide.createIcons();
    }
  }

  async function fetchNavbarNotifications() {
    const userId = getCurrentUserId();
    if (!userId) {
      renderNavbarNotifications([]);
      return;
    }

    try {
      const response = await fetch(`/api/notification/user/${userId}`);
      if (!response.ok) throw new Error('Failed to load notifications.');
      const notifications = await response.json();
      renderNavbarNotifications(notifications);
    } catch {
      renderNavbarNotifications([]);
    }
  }

  function initNavbarNotificationBell() {
    const btn = $('globalNotificationBellBtn');
    const dropdown = $('globalNotificationBellDropdown');
    if (!btn || !dropdown) return;

    btn.addEventListener('click', (event) => {
      event.stopPropagation();
      dropdown.classList.toggle('hidden');
    });

    document.addEventListener('click', (event) => {
      if (!btn.contains(event.target) && !dropdown.contains(event.target)) {
        closeNavbarNotificationBell();
      }
    });

    fetchNavbarNotifications();
    if (refreshHandle) clearInterval(refreshHandle);
    refreshHandle = window.setInterval(fetchNavbarNotifications, 30000);
  }

  window.initGlobalNavbarNotifications = initNavbarNotificationBell;
})();
