(() => {
  const sharedTermsHtml = `
    <div class="space-y-2"><h4 class="text-white font-bold text-sm">1. Acceptance</h4><p>By creating an Imajination account, you agree to these Terms. If you do not agree, do not use the platform. We may update these Terms; continued use after notice means acceptance of the updated Terms.</p></div>
    <div class="space-y-2"><h4 class="text-white font-bold text-sm">2. Account, Privacy &amp; Security</h4><ul class="space-y-1.5"><li class="flex gap-2"><span class="text-emerald-400 shrink-0">✓</span><span>Use a real email address that you control. Email verification is required before an account can be created.</span></li><li class="flex gap-2"><span class="text-emerald-400 shrink-0">✓</span><span>We process account and verification data only to operate and protect the platform. We do not sell personal data.</span></li><li class="flex gap-2"><span class="text-emerald-400 shrink-0">✓</span><span>Your birthday is used for age eligibility and may not be changed after signup without review.</span></li></ul></div>
    <div class="space-y-2"><h4 class="text-white font-bold text-sm">3. Community &amp; Platform Rules</h4><ul class="space-y-1.5"><li class="flex gap-2"><span class="text-red-400 shrink-0">✕</span><span>You must be at least <strong class="text-white">13 years old</strong>; users under 18 need parent or guardian consent.</span></li><li class="flex gap-2"><span class="text-red-400 shrink-0">✕</span><span>No false identity, impersonation, multiple-account abuse, harassment, hate speech, unlawful activity, unauthorized access, or harmful content.</span></li><li class="flex gap-2"><span class="text-red-400 shrink-0">✕</span><span>Events may be All Ages, 13+, 16+, or 18+. Users may not buy, receive, transfer into, or enter events above their verified age.</span></li></ul></div>
    <div class="space-y-2"><h4 class="text-white font-bold text-sm">4. Events, Bookings &amp; Payments</h4><p>Listings must be accurate. Tickets, bookings, payments, refunds, and disputes are subject to the stated event or booking terms and applicable law. Payments are processed by PayMongo; Imajination does not store card credentials. Misrepresentation, fraudulent payment activity, or bypassing platform fees may lead to suspension.</p></div>
    <div class="space-y-2"><h4 class="text-white font-bold text-sm">5. Reports &amp; Moderation</h4><p>We may investigate reports and remove content, events, or accounts that violate these Terms or applicable law. Serious safety, fraud, child-protection, or cybercrime concerns may be referred to the appropriate authorities.</p></div>
    <div class="space-y-2"><h4 class="text-white font-bold text-sm">6. Applicable Philippine Laws</h4><p>These Terms operate alongside Philippine law, including <strong class="text-white">R.A. No. 10173</strong> (Data Privacy Act), <strong class="text-white">R.A. No. 8792</strong> (Electronic Commerce Act), <strong class="text-white">R.A. No. 7394</strong> (Consumer Act), <strong class="text-white">R.A. No. 10175</strong> (Cybercrime Prevention Act), <strong class="text-white">R.A. No. 11313</strong> (Safe Spaces Act), <strong class="text-white">R.A. Nos. 7610 and 11930</strong> (child protection), and <strong class="text-white">R.A. No. 9995</strong> (Anti-Photo and Video Voyeurism Act). Nothing in these Terms removes rights or obligations provided by law. Read the <a href="/pages/auth/terms.html" target="_blank" rel="noopener" class="text-red-400 underline">full Terms and Conditions</a> for details.</p></div>`;

  document.addEventListener('DOMContentLoaded', () => {
    const area = document.getElementById('termsScrollArea');
    const acceptButton = document.getElementById('acceptTermsBtn');
    if (!area || !acceptButton) return;

    area.innerHTML = sharedTermsHtml;
    acceptButton.disabled = true;
    acceptButton.textContent = 'Scroll to the end to accept';

    area.addEventListener('scroll', () => {
      if (area.scrollTop + area.clientHeight < area.scrollHeight - 8) return;
      acceptButton.disabled = false;
      acceptButton.textContent = 'I Accept';
    });
  });
})();
