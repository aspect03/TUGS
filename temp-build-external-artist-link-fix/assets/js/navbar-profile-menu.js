(function () {
  const ROLE_CONFIG = {
    Customer: {
      dashboardHref: '/pages/dashboards/CustomerDashboard.html',
      extraLinks: [
        {
          id: 'myTicketsLink',
          icon: 'ticket',
          label: 'My Tickets',
          href: '/pages/dashboards/CustomerDashboard.html?view=tickets',
          sessionValue: 'tickets'
        }
      ],
      profileHref: '/pages/dashboards/CustomerDashboard.html?view=profile',
      editProfileHref: '/pages/dashboards/CustomerDashboard.html?view=settings',
      sessionKey: 'customerActiveView',
      sessionValue: 'profile',
      editSessionValue: 'settings',
      dashboardSessionValue: 'dashboard'
    },
    Organizer: {
      dashboardHref: '/pages/dashboards/OrganizerDashboard.html',
      profileHref: '/pages/dashboards/OrganizerDashboard.html?view=settings',
      profileLabel: 'Settings',
      sessionKey: 'organizerActiveView',
      sessionValue: 'settings',
      dashboardSessionValue: 'dashboard'
    },
    Artist: {
      dashboardHref: '/pages/dashboards/ArtistDashboard.html',
      profileHref: '/pages/dashboards/ArtistDashboard.html?tab=profile-page',
      editProfileHref: '/pages/dashboards/ArtistDashboard.html?tab=edit-profile',
      sessionKey: 'artistActiveTab',
      sessionValue: 'profile-page',
      editSessionValue: 'edit-profile',
      dashboardSessionValue: 'dashboard'
    },
    Sessionist: {
      dashboardHref: '/pages/dashboards/SessionistDashboard.html',
      profileHref: '/pages/dashboards/SessionistDashboard.html?tab=profile-page',
      editProfileHref: '/pages/dashboards/SessionistDashboard.html?tab=edit-profile',
      sessionKey: 'sessionistActiveTab',
      sessionValue: 'profile-page',
      editSessionValue: 'edit-profile',
      dashboardSessionValue: 'dashboard'
    }
  };

  const MENU_LINK_CLASS = 'flex items-center gap-2 px-3 py-2 text-xs font-medium text-white/70 hover:text-white hover:bg-white/5 rounded-lg transition-colors';
  const LOGOUT_BUTTON_CLASS = 'w-full flex items-center gap-2 px-3 py-2 text-xs font-medium text-red-400 hover:text-red-300 hover:bg-red-500/10 rounded-lg transition-colors';

  function getRoleConfig(role) {
    return ROLE_CONFIG[role] || null;
  }

  function ensureActionGroup(dropdown) {
    let actionGroup = dropdown.querySelector('[data-navbar-action-group="true"]');
    if (actionGroup) return actionGroup;

    actionGroup = dropdown.querySelector('.p-2.flex.flex-col.gap-1');
    if (actionGroup) {
      actionGroup.dataset.navbarActionGroup = 'true';
      return actionGroup;
    }

    actionGroup = document.createElement('div');
    actionGroup.className = 'p-2 flex flex-col gap-1';
    actionGroup.dataset.navbarActionGroup = 'true';
    dropdown.appendChild(actionGroup);
    return actionGroup;
  }

  function ensureFooterGroup(dropdown) {
    let footerGroup = dropdown.querySelector('[data-navbar-footer-group="true"]');
    if (footerGroup) return footerGroup;

    footerGroup = Array.from(dropdown.children).find((child) => child.classList?.contains('border-t'));
    if (footerGroup) {
      footerGroup.dataset.navbarFooterGroup = 'true';
      return footerGroup;
    }

    footerGroup = document.createElement('div');
    footerGroup.className = 'p-2 border-t border-white/10';
    footerGroup.dataset.navbarFooterGroup = 'true';
    dropdown.appendChild(footerGroup);
    return footerGroup;
  }

  function createMenuLink(id, icon, label, href) {
    const link = document.createElement('a');
    link.id = id;
    link.href = href;
    link.className = MENU_LINK_CLASS;
    link.innerHTML = `<i data-lucide="${icon}" class="w-3.5 h-3.5"></i> ${label}`;
    return link;
  }

  function createLogoutButton() {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = LOGOUT_BUTTON_CLASS;
    button.innerHTML = '<i data-lucide="log-out" class="w-3.5 h-3.5"></i> Sign out';
    button.addEventListener('click', () => {
      if (typeof window.handleLogout === 'function') {
        window.handleLogout();
        return;
      }
      localStorage.clear();
      window.location.href = '/pages/auth/login.html';
    });
    return button;
  }

  function wireMenuLink(link, href, sessionKey, sessionValue) {
    link.href = href;
    link.addEventListener('click', (event) => {
      if (sessionKey && sessionValue) {
        sessionStorage.setItem(sessionKey, sessionValue);
      }

      const currentPath = window.location.pathname;
      const targetUrl = new URL(href, window.location.origin);
      const targetPath = targetUrl.pathname;
      const isSameDashboard = currentPath === targetPath && sessionValue;
      const canSwitchTab = typeof window.switchTab === 'function';
      const canSwitchView = typeof window.setActiveView === 'function';

      if (isSameDashboard && (canSwitchTab || canSwitchView)) {
        event.preventDefault();
        if (canSwitchView) {
          window.setActiveView(sessionValue);
        } else {
          window.switchTab(sessionValue);
        }
        window.history.replaceState({}, document.title, targetUrl.toString());
        document.getElementById('profileDropdown')?.classList.remove('show');
      }
    });
  }

  function normalizeDropdownMenu(dropdown, role) {
    const config = getRoleConfig(role);
    if (!config) return;

    const actionGroup = ensureActionGroup(dropdown);
    const footerGroup = ensureFooterGroup(dropdown);

    actionGroup.innerHTML = '';
    footerGroup.innerHTML = '';

    const dashboardLink = createMenuLink('dashboardLink', 'layout-dashboard', config.dashboardLabel || 'Dashboard', config.dashboardHref);
    const profileLink = createMenuLink('profilePageLink', 'user-round', config.profileLabel || 'Profile Page', config.profileHref);

    wireMenuLink(dashboardLink, config.dashboardHref, config.sessionKey, config.dashboardSessionValue);
    wireMenuLink(profileLink, config.profileHref, config.sessionKey, config.sessionValue);

    actionGroup.appendChild(dashboardLink);

    if (Array.isArray(config.extraLinks)) {
      config.extraLinks.forEach((item) => {
        const extraLink = createMenuLink(item.id, item.icon, item.label, item.href);
        wireMenuLink(extraLink, item.href, config.sessionKey, item.sessionValue);
        actionGroup.appendChild(extraLink);
      });
    }

    actionGroup.appendChild(profileLink);

    if (config.editProfileHref) {
      const editProfileLink = createMenuLink('editProfileLink', 'settings', config.editProfileLabel || 'Edit Profile', config.editProfileHref);
      wireMenuLink(editProfileLink, config.editProfileHref, config.sessionKey, config.editSessionValue);
      actionGroup.appendChild(editProfileLink);
    }

    footerGroup.appendChild(createLogoutButton());
  }

  const DISPLAY_NAME_ROLES = new Set(['Artist', 'Sessionist']);
  const DISPLAY_NAME_ENDPOINTS = { Artist: 'artist', Sessionist: 'sessionist', Customer: 'customer', Organizer: 'organizer' };

  function applyNameToNav(name) {
    const dropdownName = document.getElementById('dropdownName');
    const profileInitial = document.getElementById('profileInitial') || document.getElementById('profileInitialNav');
    if (dropdownName && name) dropdownName.textContent = name;
    if (profileInitial && name) profileInitial.textContent = name.charAt(0).toUpperCase();
  }

  function resolveDisplayName(role, profile) {
    if (!profile) return null;
    if (DISPLAY_NAME_ROLES.has(role)) {
      return profile.displayName || profile.stageName || profile.firstName || null;
    }
    return profile.displayName || profile.firstName || profile.productionName || null;
  }

  function fetchAndSyncProfileData(role, userId) {
    const endpoint = DISPLAY_NAME_ENDPOINTS[role];
    if (!endpoint || !userId) return;

    fetch(`/api/${endpoint}/${userId}`, { cache: 'no-store' })
      .then((res) => (res.ok ? res.json() : null))
      .then((profile) => {
        if (!profile) return;

        const displayName = resolveDisplayName(role, profile);
        if (displayName) {
          localStorage.setItem('userDisplayName', displayName);
          applyNameToNav(displayName);
        }

        const pic = profile.profilePicture;
        if (pic) {
          const navProfilePic = document.getElementById('navProfilePic');
          const profileInitial = document.getElementById('profileInitial') || document.getElementById('profileInitialNav');
          localStorage.setItem('userProfilePicture', pic);
          if (navProfilePic) {
            navProfilePic.src = pic;
            navProfilePic.classList.remove('hidden');
            profileInitial?.classList.add('hidden');
          }
        }
      })
      .catch(() => {});
  }

  function syncProfileIdentity(role) {
    const authLoggedIn = document.getElementById('authLoggedIn');
    const authLoggedOut = document.getElementById('authLoggedOut');
    const dropdown = document.getElementById('profileDropdown');
    const dropdownName = document.getElementById('dropdownName');
    const dropdownRole = document.getElementById('dropdownRole');
    const profileInitial = document.getElementById('profileInitial') || document.getElementById('profileInitialNav');
    const navProfilePic = document.getElementById('navProfilePic');
    const config = getRoleConfig(role);

    if (!dropdown) return;

    if (!config) {
      authLoggedIn?.classList.add('hidden');
      authLoggedOut?.classList.remove('hidden');
      return;
    }

    authLoggedOut?.classList.add('hidden');
    authLoggedIn?.classList.remove('hidden');

    const displayName = localStorage.getItem('userDisplayName') || localStorage.getItem('userFirstName') || localStorage.getItem('firstName') || 'User';
    const profilePicture = localStorage.getItem('userProfilePicture') || '';

    if (dropdownName) dropdownName.textContent = displayName;
    if (dropdownRole) dropdownRole.textContent = role.toUpperCase();
    if (profileInitial) profileInitial.textContent = displayName.charAt(0).toUpperCase();

    if (navProfilePic) {
      if (profilePicture) {
        navProfilePic.src = profilePicture;
        navProfilePic.classList.remove('hidden');
        profileInitial?.classList.add('hidden');
      } else {
        navProfilePic.classList.add('hidden');
        profileInitial?.classList.remove('hidden');
      }
    }

    normalizeDropdownMenu(dropdown, role);
  }

  function initNavbarProfileMenu() {
    const dropdown = document.getElementById('profileDropdown');
    if (!dropdown) return;

    dropdown.classList.add('top-full');

    const role = localStorage.getItem('userRole');
    const userId = localStorage.getItem('userId');

    syncProfileIdentity(role);
    fetchAndSyncProfileData(role, userId);

    window.addEventListener('imajination:session-cleared', () => {
      syncProfileIdentity(localStorage.getItem('userRole'));
    });

    if (window.lucide?.createIcons) {
      window.lucide.createIcons();
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initNavbarProfileMenu);
  } else {
    initNavbarProfileMenu();
  }
})();
