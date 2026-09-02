(function () {
  function setStatus(container, text, tone) {
    const status = container?.querySelector('[data-push-status]');
    if (!status) return;
    status.textContent = text;
    status.className = `text-xs ${tone || 'text-white/45'}`;
  }

  function getUserId() {
    return (localStorage.getItem('userId') || '').trim();
  }

  function subscriptionToPayload(userId, subscription) {
    const json = subscription.toJSON();
    return { userId, endpoint: json.endpoint, p256dh: json.keys?.p256dh, auth: json.keys?.auth };
  }

  async function enablePush(container, button) {
    const userId = getUserId();
    if (!userId) return;
    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
      setStatus(container, 'Push is unavailable in this browser.', 'text-amber-300');
      return;
    }

    button.disabled = true;
    try {
      const permission = await Notification.requestPermission();
      if (permission !== 'granted') {
        setStatus(container, 'Notifications are blocked or not allowed yet.', 'text-amber-300');
        return;
      }

      const registration = await navigator.serviceWorker.register('/push-service-worker.js');
      let subscription = await registration.pushManager.getSubscription();
      if (!subscription) {
        let publicKey = container.dataset.vapidPublicKey || window.TUGS_VAPID_PUBLIC_KEY || '';
        if (!publicKey) {
          const keyRes = await fetch('/api/push/public-key');
          const keyData = await keyRes.json().catch(() => ({}));
          publicKey = keyData.publicKey || '';
        }
        if (!publicKey) {
          setStatus(container, 'Permission is enabled. Add a VAPID public key to create push subscriptions.', 'text-amber-300');
          return;
        }
        const padding = '='.repeat((4 - publicKey.length % 4) % 4);
        const base64 = (publicKey + padding).replace(/-/g, '+').replace(/_/g, '/');
        const rawKey = Uint8Array.from(atob(base64), char => char.charCodeAt(0));
        subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: rawKey });
      }

      const res = await fetch('/api/push/subscribe', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(subscriptionToPayload(userId, subscription))
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(data.message || 'Could not enable push notifications.');
      setStatus(container, 'Push notifications are enabled on this browser.', 'text-emerald-300');
    } catch (error) {
      setStatus(container, error.message || 'Could not enable push notifications.', 'text-red-300');
    } finally {
      button.disabled = false;
    }
  }

  async function disablePush(container, button) {
    const userId = getUserId();
    if (!userId) return;
    button.disabled = true;
    try {
      let endpoint = '';
      const registration = 'serviceWorker' in navigator ? await navigator.serviceWorker.getRegistration('/push-service-worker.js') : null;
      const subscription = registration ? await registration.pushManager.getSubscription() : null;
      if (subscription) {
        endpoint = subscription.endpoint;
        await subscription.unsubscribe();
      }
      await fetch('/api/push/unsubscribe', {
        method: 'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId, endpoint })
      });
      setStatus(container, 'Push notifications are disabled for this browser.', 'text-white/45');
    } catch {
      setStatus(container, 'Could not disable push notifications right now.', 'text-red-300');
    } finally {
      button.disabled = false;
    }
  }

  window.initPushNotificationControls = async function (containerId) {
    const container = document.getElementById(containerId);
    const userId = getUserId();
    if (!container || !userId) return;
    const enableBtn = container.querySelector('[data-push-enable]');
    const disableBtn = container.querySelector('[data-push-disable]');
    enableBtn?.addEventListener('click', () => enablePush(container, enableBtn));
    disableBtn?.addEventListener('click', () => disablePush(container, disableBtn));
    try {
      const res = await fetch(`/api/push/user/${encodeURIComponent(userId)}/status`);
      const data = await res.json().catch(() => ({}));
      if (res.ok && data.subscribed) setStatus(container, `${data.subscriptionCount || 1} browser subscription active.`, 'text-emerald-300');
    } catch {}
  };
})();
