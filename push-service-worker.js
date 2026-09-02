self.addEventListener('push', (event) => {
  let payload = { title: 'Tugs notification', body: 'You have a new update.' };
  try { payload = event.data ? event.data.json() : payload; } catch { payload.body = event.data ? event.data.text() : payload.body; }
  event.waitUntil(self.registration.showNotification(payload.title || 'Tugs notification', {
    body: payload.body || 'You have a new update.',
    icon: payload.icon || '/assets/images/logo.png',
    badge: payload.badge || '/assets/images/logo.png',
    data: { url: payload.url || '/' }
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const targetUrl = event.notification.data?.url || '/';
  event.waitUntil(self.clients.openWindow(targetUrl));
});
