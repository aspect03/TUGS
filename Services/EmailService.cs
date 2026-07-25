using System.Net;
using System.Net.Mail;
using Npgsql;

namespace ImajinationAPI.Services
{
    public class EmailService
    {
        private readonly string _smtpServer;
        private readonly int _port;
        private readonly string _senderEmail;
        private readonly string _senderName;
        private readonly string _username;
        private readonly string _password;
        private readonly bool _enabled;
        private readonly ILogger<EmailService> _logger;
        private readonly string _connectionString;

        public EmailService(IConfiguration config, ILogger<EmailService> logger)
        {
            _logger = logger;
            _smtpServer = config["EmailSettings:SmtpServer"] ?? "";
            _port = int.TryParse(config["EmailSettings:Port"]?.ToString(), out var p) ? p : 587;
            _senderEmail = config["EmailSettings:SenderEmail"] ?? "";
            _senderName = config["EmailSettings:SenderName"] ?? "Tugs!";
            _username = config["EmailSettings:Username"] ?? "";
            _password = config["EmailSettings:Password"] ?? "";
            _enabled = !string.IsNullOrWhiteSpace(_smtpServer) && !string.IsNullOrWhiteSpace(_senderEmail);
            _connectionString = config.GetConnectionString("SupabaseConnection")
                ?? config["ConnectionStrings__SupabaseConnection"]
                ?? config["ConnectionStrings:SupabaseConnection"]
                ?? "";
        }

        // Creates its own fresh connection — safe for fire-and-forget calls
        private async Task<NpgsqlConnection> OpenFreshConnectionAsync()
        {
            var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            return conn;
        }

        // ── Public send methods ────────────────────────────────────────────────

        public async Task SendBookingRequestEmailAsync(
            NpgsqlConnection? connection,
            Guid targetUserId,
            string customerName,
            string targetRole,
            string eventTitle,
            DateTime? eventDate,
            string? location,
            decimal? budget)
        {
            // Always use a fresh connection — caller's connection may be disposed (fire-and-forget)
            var (toEmail, toFirst, _) = await GetUserContactFreshAsync(targetUserId);
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var dateStr = eventDate.HasValue
                ? eventDate.Value.ToLocalTime().ToString("dddd, MMMM d, yyyy 'at' h:mm tt")
                : "Date TBA";

            var budgetStr = budget > 0 ? $"₱{budget:N0}" : "Open / To discuss";

            var body = BuildEmailHtml(
                preheader: $"{customerName} wants to book you for {eventTitle}",
                headerLabel: "New Booking Request",
                title: $"You have a new booking request",
                greeting: $"Hi {toFirst},",
                paragraphs: new[]
                {
                    $"<strong>{customerName}</strong> has submitted a booking request for your services on Tugs!.",
                    BuildInfoTable(new[]
                    {
                        ("Event", eventTitle),
                        ("Date", dateStr),
                        ("Location", string.IsNullOrWhiteSpace(location) ? "TBA" : location),
                        ("Budget", budgetStr),
                        ("Role", targetRole),
                    }),
                    "Log in to review the request and confirm or decline. Requests left unanswered may expire."
                },
                ctaText: "Review Request",
                ctaUrl: "https://imajination.onrender.com/pages/bookings/messages.html",
                accentColor: "#e53e3e"
            );

            await SendAsync(toEmail, $"{toFirst}", $"New booking request from {customerName} · Imajination", body);
        }

        public async Task SendBookingConfirmedEmailAsync(
            NpgsqlConnection? connection,
            Guid customerId,
            string targetName,
            string targetRole,
            string eventTitle,
            DateTime? eventDate,
            string? location,
            decimal? budget)
        {
            var (toEmail, toFirst, _) = await GetUserContactFreshAsync(customerId);
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var dateStr = eventDate.HasValue
                ? eventDate.Value.ToLocalTime().ToString("dddd, MMMM d, yyyy 'at' h:mm tt")
                : "Date TBA";

            var body = BuildEmailHtml(
                preheader: $"Your booking with {targetName} for {eventTitle} is confirmed.",
                headerLabel: "Booking Confirmed",
                title: "Your booking is confirmed!",
                greeting: $"Hi {toFirst},",
                paragraphs: new[]
                {
                    $"Great news — <strong>{targetName}</strong> has confirmed your booking. Here are the details:",
                    BuildInfoTable(new[]
                    {
                        ("Event", eventTitle),
                        ("Artist / Talent", $"{targetName} ({targetRole})"),
                        ("Date", dateStr),
                        ("Location", string.IsNullOrWhiteSpace(location) ? "TBA" : location),
                        ("Budget", budget > 0 ? $"₱{budget:N0}" : "Agreed separately"),
                    }),
                    "You can message your talent directly through the app. Make sure to coordinate all the details before the event day."
                },
                ctaText: "Open Booking",
                ctaUrl: "https://imajination.onrender.com/pages/bookings/messages.html",
                accentColor: "#38a169"
            );

            await SendAsync(toEmail, toFirst, $"Booking confirmed: {eventTitle} · Imajination", body);
        }

        public async Task SendBookingCompletedEmailAsync(
            NpgsqlConnection? connection,
            Guid customerId,
            Guid targetUserId,
            string targetName,
            string eventTitle)
        {
            // Email to customer — prompt to leave a review
            var (custEmail, custFirst, _) = await GetUserContactFreshAsync(customerId);
            if (!string.IsNullOrWhiteSpace(custEmail))
            {
                var custBody = BuildEmailHtml(
                    preheader: $"Your booking for {eventTitle} has been completed.",
                    headerLabel: "Booking Completed",
                    title: "Booking successfully completed",
                    greeting: $"Hi {custFirst},",
                    paragraphs: new[]
                    {
                        $"Your booking for <strong>{eventTitle}</strong> with <strong>{targetName}</strong> has been marked as completed and settled.",
                        "We hope it was an amazing experience! Sharing your feedback helps other customers discover great talent on Tugs!.",
                    },
                    ctaText: "Leave a Review",
                    ctaUrl: $"https://imajination.onrender.com/pages/browse/Artists.html",
                    accentColor: "#805ad5"
                );
                await SendAsync(custEmail, custFirst, $"Booking completed — how was {targetName}? · Imajination", custBody);
            }

            // Email to talent — completion confirmed
            var (talentEmail, talentFirst, _) = await GetUserContactFreshAsync(targetUserId);
            if (!string.IsNullOrWhiteSpace(talentEmail))
            {
                var talentBody = BuildEmailHtml(
                    preheader: $"Booking for {eventTitle} is fully completed.",
                    headerLabel: "Booking Completed",
                    title: "Booking completed & settled",
                    greeting: $"Hi {talentFirst},",
                    paragraphs: new[]
                    {
                        $"Your booking for <strong>{eventTitle}</strong> has been fully confirmed as completed by both parties.",
                        "Any applicable escrow funds have been released. Check your dashboard for your earnings summary.",
                    },
                    ctaText: "View Dashboard",
                    ctaUrl: "https://imajination.onrender.com/pages/dashboards/ArtistDashboard.html",
                    accentColor: "#38a169"
                );
                await SendAsync(talentEmail, talentFirst, $"Booking completed: {eventTitle} · Imajination", talentBody);
            }
        }

        public async Task SendBookingCancelledEmailAsync(
            NpgsqlConnection? connection,
            Guid recipientId,
            string cancelledByName,
            string eventTitle,
            bool isRefundIssued)
        {
            var (toEmail, toFirst, _) = await GetUserContactFreshAsync(recipientId);
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var refundNote = isRefundIssued
                ? "A refund has been initiated for any payments made. Please allow 3-7 business days for processing."
                : "No charges were applied to this booking.";

            var body = BuildEmailHtml(
                preheader: $"Your booking for {eventTitle} has been cancelled.",
                headerLabel: "Booking Cancelled",
                title: "Booking has been cancelled",
                greeting: $"Hi {toFirst},",
                paragraphs: new[]
                {
                    $"Unfortunately, the booking for <strong>{eventTitle}</strong> was cancelled by <strong>{cancelledByName}</strong>.",
                    refundNote,
                    "You can browse other available artists and sessionists on Imajination and create a new booking anytime."
                },
                ctaText: "Browse Artists",
                ctaUrl: "https://imajination.onrender.com/pages/browse/Artists.html",
                accentColor: "#e53e3e"
            );

            await SendAsync(toEmail, toFirst, $"Booking cancelled: {eventTitle} · Imajination", body);
        }

        public async Task SendTicketReceiptEmailAsync(
            NpgsqlConnection? connection,
            Guid customerId,
            string eventTitle,
            int quantity,
            decimal totalPrice,
            DateTime? eventDate,
            string? eventLocation,
            string? city,
            string ticketId)
        {
            var (toEmail, toFirst, _) = await GetUserContactFreshAsync(customerId);
            if (string.IsNullOrWhiteSpace(toEmail)) return;

            var dateStr = eventDate.HasValue
                ? eventDate.Value.ToLocalTime().ToString("dddd, MMMM d, yyyy 'at' h:mm tt")
                : "Date TBA";
            var locationStr = string.IsNullOrWhiteSpace(eventLocation) ? "TBA" : eventLocation;
            if (!string.IsNullOrWhiteSpace(city)) locationStr = $"{city} — {locationStr}";

            var body = BuildEmailHtml(
                preheader: $"Your ticket for {eventTitle} is confirmed. See you there!",
                headerLabel: "Ticket Confirmed",
                title: "You're going to the show!",
                greeting: $"Hi {toFirst},",
                paragraphs: new[]
                {
                    $"Your ticket payment for <strong>{eventTitle}</strong> has been confirmed. Keep this email as your receipt.",
                    BuildInfoTable(new[]
                    {
                        ("Event", eventTitle),
                        ("Date", dateStr),
                        ("Venue", locationStr),
                        ("Tickets", quantity == 1 ? "1 ticket" : $"{quantity} tickets"),
                        ("Total Paid", $"₱{totalPrice:N2}"),
                        ("Order Reference", ticketId.Length >= 8 ? ticketId[..8].ToUpper() : ticketId.ToUpper()),
                    }),
                    "Your digital ticket with QR code is available in your Imajination account under <strong>My Tickets</strong>. Present it at the entrance for scanning."
                },
                ctaText: "View My Ticket",
                ctaUrl: "https://imajination.onrender.com/pages/dashboards/CustomerDashboard.html",
                accentColor: "#e53e3e"
            );

            await SendAsync(toEmail, toFirst, $"Ticket confirmed: {eventTitle} · Imajination", body);
        }

        public async Task SendEventReminderEmailAsync(
            string toEmail,
            string toName,
            string eventTitle,
            DateTime eventDate,
            string? location,
            string? city,
            int hoursBeforeEvent)
        {
            var when = hoursBeforeEvent <= 24 ? "tomorrow" : $"in {hoursBeforeEvent / 24} days";
            var dateStr = eventDate.ToLocalTime().ToString("dddd, MMMM d, yyyy 'at' h:mm tt");
            var locationStr = string.IsNullOrWhiteSpace(location) ? "TBA" : location;
            if (!string.IsNullOrWhiteSpace(city)) locationStr = $"{city} — {locationStr}";

            var body = BuildEmailHtml(
                preheader: $"Reminder: {eventTitle} is {when}!",
                headerLabel: $"Event Reminder · {hoursBeforeEvent}h",
                title: $"{eventTitle} is {when}!",
                greeting: $"Hi {toName},",
                paragraphs: new[]
                {
                    $"Just a friendly reminder that <strong>{eventTitle}</strong> is coming up soon.",
                    BuildInfoTable(new[]
                    {
                        ("Event", eventTitle),
                        ("Date", dateStr),
                        ("Venue", locationStr),
                    }),
                    "Make sure to bring your digital ticket QR code on your phone or as a screenshot. Doors open 30 minutes before the show."
                },
                ctaText: "View My Ticket",
                ctaUrl: "https://imajination.onrender.com/pages/dashboards/CustomerDashboard.html",
                accentColor: "#d69e2e"
            );

            await SendAsync(toEmail, toName, $"Reminder: {eventTitle} is {when} · Imajination", body);
        }

        public async Task SendWelcomeEmailAsync(string toEmail, string firstName, string role)
        {
            var dashboardUrl = role switch
            {
                "Artist" => "https://imajination.onrender.com/pages/dashboards/ArtistDashboard.html",
                "Sessionist" => "https://imajination.onrender.com/pages/dashboards/SessionistDashboard.html",
                "Organizer" => "https://imajination.onrender.com/pages/dashboards/OrganizerDashboard.html",
                _ => "https://imajination.onrender.com/pages/dashboards/CustomerDashboard.html"
            };

            var roleNote = role switch
            {
                "Artist" => "Complete your profile, set your availability, and wait for booking requests to start rolling in.",
                "Sessionist" => "Update your profile with your instruments and genres so organizers and artists can find you.",
                "Organizer" => "Head to your dashboard to create your first event and start selling tickets.",
                _ => "Browse artists, sessionists, and upcoming events near you."
            };

            var body = BuildEmailHtml(
                preheader: $"Welcome to Imajination, {firstName}! Your account is ready.",
                headerLabel: "Welcome to Imajination",
                title: $"Welcome, {firstName}!",
                greeting: $"Hi {firstName},",
                paragraphs: new[]
                {
                    $"Your <strong>{role}</strong> account on Imajination is all set up and ready to go.",
                    roleNote,
                    "If you have any questions, reach out via the in-app community or contact our support team."
                },
                ctaText: "Go to My Dashboard",
                ctaUrl: dashboardUrl,
                accentColor: "#e53e3e"
            );

            await SendAsync(toEmail, firstName, $"Welcome to Imajination, {firstName}!", body);
        }

        public async Task SendWaitlistSpotEmailAsync(string toEmail, string firstName, string eventTitle, string eventUrl)
        {
            var body = BuildEmailHtml(
                preheader: $"A spot just opened for {eventTitle} — act fast!",
                headerLabel: "Waitlist Alert",
                title: "A ticket is now available!",
                greeting: $"Hi {firstName},",
                paragraphs: new[]
                {
                    $"You're at the top of the waitlist for <strong>{eventTitle}</strong> and a ticket just became available!",
                    "⚡ You have <strong>30 minutes</strong> to purchase before the spot is offered to the next person in line.",
                    "Head to the event page now to secure your ticket."
                },
                ctaText: "Get My Ticket Now",
                ctaUrl: eventUrl,
                accentColor: "#d69e2e"
            );
            await SendAsync(toEmail, firstName, $"⚡ A ticket for {eventTitle} is available! · Imajination", body);
        }

        public async Task SendTicketTransferEmailAsync(string toEmail, string firstName, string fromName, string eventTitle, string acceptUrl)
        {
            var body = BuildEmailHtml(
                preheader: $"{fromName} is transferring their ticket for {eventTitle} to you.",
                headerLabel: "Ticket Transfer",
                title: "You've received a ticket transfer!",
                greeting: $"Hi {firstName},",
                paragraphs: new[]
                {
                    $"<strong>{fromName}</strong> has transferred their ticket for <strong>{eventTitle}</strong> to you on Tugs!.",
                    "Click the button below to accept the ticket. This transfer link expires in 24 hours — if you don't accept it, the ticket will return to the original holder.",
                },
                ctaText: "Accept Ticket Transfer",
                ctaUrl: acceptUrl,
                accentColor: "#805ad5"
            );
            await SendAsync(toEmail, firstName, $"Ticket transfer: {eventTitle} · Imajination", body);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        // Overload that accepts an existing connection (for awaited calls)
        public async Task<(string email, string firstName, string lastName)> GetUserContactAsync(
            NpgsqlConnection connection, Guid userId)
        {
            const string sql = @"
                SELECT COALESCE(email,''), COALESCE(firstname,''), COALESCE(lastname,'')
                FROM users WHERE id = @id LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@id", userId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            return ("", "", "");
        }

        // Overload that creates its own connection — safe for fire-and-forget calls
        private async Task<(string email, string firstName, string lastName)> GetUserContactFreshAsync(Guid userId)
        {
            await using var conn = await OpenFreshConnectionAsync();
            return await GetUserContactAsync(conn, userId);
        }

        public Task SendRawAsync(string toEmail, string subject, string htmlBody)
            => SendAsync(toEmail, "", subject, htmlBody);

        public Task SendPartnerStatusEmailAsync(
            string toEmail, string organizerName, string eventName,
            string newStatus, string? adminNotes)
        {
            return newStatus switch
            {
                "Approved" => SendAsync(toEmail, organizerName,
                    $"Your Event Proposal Has Been Approved — {eventName}",
                    BuildEmailHtml(
                        preheader: $"Great news! {eventName} has been approved by the Tugs! team.",
                        headerLabel: "Proposal Approved",
                        title: "You're in! 🎉",
                        greeting: $"Hi {organizerName},",
                        paragraphs: new[]
                        {
                            $"Your event proposal <strong>{System.Net.WebUtility.HtmlEncode(eventName)}</strong> has been <strong style=\"color:#16a34a;\">approved</strong> by the Tugs! team.",
                            !string.IsNullOrWhiteSpace(adminNotes)
                                ? $"<div style=\"background:#f0fdf4;border-left:4px solid #16a34a;border-radius:0 8px 8px 0;padding:14px 18px;margin:4px 0;\"><p style=\"margin:0;font-size:13px;font-weight:700;color:#15803d;text-transform:uppercase;letter-spacing:0.08em;\">Note from the team</p><p style=\"margin:6px 0 0;font-size:14px;color:#166534;\">{System.Net.WebUtility.HtmlEncode(adminNotes)}</p></div>"
                                : "<p style=\"margin:0 0 18px 0;font-size:15px;line-height:1.75;color:#4b5563;\">Our team will reach out shortly to discuss the next steps — event setup, ticket pricing, and launch timeline.</p>",
                            "In the meantime, if you have any questions, simply reply to this email."
                        },
                        ctaText: "Visit Tugs!",
                        ctaUrl: "https://imajination.onrender.com",
                        accentColor: "#16a34a"
                    )),

                "Rejected" => SendAsync(toEmail, organizerName,
                    $"Update on Your Event Proposal — {eventName}",
                    BuildEmailHtml(
                        preheader: $"An update on your proposal for {eventName}.",
                        headerLabel: "Proposal Update",
                        title: "Thank you for reaching out",
                        greeting: $"Hi {organizerName},",
                        paragraphs: new[]
                        {
                            $"We appreciate you submitting your event proposal <strong>{System.Net.WebUtility.HtmlEncode(eventName)}</strong> to Tugs!.",
                            !string.IsNullOrWhiteSpace(adminNotes)
                                ? $"<div style=\"background:#fff7ed;border-left:4px solid #ea580c;border-radius:0 8px 8px 0;padding:14px 18px;margin:4px 0;\"><p style=\"margin:0;font-size:13px;font-weight:700;color:#c2410c;text-transform:uppercase;letter-spacing:0.08em;\">Reason</p><p style=\"margin:6px 0 0;font-size:14px;color:#9a3412;\">{System.Net.WebUtility.HtmlEncode(adminNotes)}</p></div>"
                                : "After careful review, your proposal doesn't fit our current program at this time.",
                            "We encourage you to submit again in the future — we'd love to work with you."
                        },
                        ctaText: "Browse Tugs! Events",
                        ctaUrl: "https://imajination.onrender.com",
                        accentColor: "#e53e3e"
                    )),

                "In Review" => SendAsync(toEmail, organizerName,
                    $"We're Reviewing Your Proposal — {eventName}",
                    BuildEmailHtml(
                        preheader: $"Your proposal for {eventName} is under review.",
                        headerLabel: "Under Review",
                        title: "We're reviewing your proposal",
                        greeting: $"Hi {organizerName},",
                        paragraphs: new[]
                        {
                            $"Your event proposal <strong>{System.Net.WebUtility.HtmlEncode(eventName)}</strong> is now <strong style=\"color:#2563eb;\">under review</strong> by our team.",
                            "We'll get back to you within 1–2 business days with a decision."
                        },
                        ctaText: "Visit Tugs!",
                        ctaUrl: "https://imajination.onrender.com",
                        accentColor: "#2563eb"
                    )),

                _ => Task.CompletedTask
            };
        }

        private async Task SendAsync(string toEmail, string toName, string subject, string htmlBody)
        {
            if (!_enabled)
            {
                _logger.LogDebug("[EmailService] Email disabled or not configured — skipping '{Subject}' to {Email}", subject, toEmail);
                return;
            }

            if (string.IsNullOrWhiteSpace(toEmail))
                return;

            try
            {
                using var smtp = new SmtpClient(_smtpServer, _port)
                {
                    EnableSsl = true,
                    Credentials = new NetworkCredential(_username, _password),
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 15_000
                };

                using var msg = new MailMessage
                {
                    From = new MailAddress(_senderEmail, _senderName),
                    Subject = subject,
                    IsBodyHtml = true,
                    Body = htmlBody
                };
                msg.To.Add(new MailAddress(toEmail, toName));

                await smtp.SendMailAsync(msg);
                _logger.LogInformation("[EmailService] Sent '{Subject}' → {Email}", subject, toEmail);
            }
            catch (Exception ex)
            {
                // Never let email failures break the main request flow
                _logger.LogWarning(ex, "[EmailService] Failed to send '{Subject}' to {Email}", subject, toEmail);
            }
        }

        // ── HTML Template Builder ─────────────────────────────────────────────

        private static string BuildInfoTable(IEnumerable<(string label, string value)> rows)
        {
            var cells = string.Join("", rows.Select(r => $@"
                <tr>
                  <td style=""padding:10px 16px;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.1em;color:#9ca3af;white-space:nowrap;vertical-align:top;width:110px;border-bottom:1px solid #f3f4f6;"">{r.label}</td>
                  <td style=""padding:10px 16px 10px 0;font-size:14px;color:#111827;font-weight:600;line-height:1.4;border-bottom:1px solid #f3f4f6;"">{System.Net.WebUtility.HtmlEncode(r.value)}</td>
                </tr>"));

            return $@"<table cellpadding=""0"" cellspacing=""0"" border=""0"" style=""width:100%;margin:20px 0;border-radius:12px;overflow:hidden;background:#f9fafb;border:1px solid #e5e7eb;"">
                        <tbody>{cells}</tbody>
                      </table>";
        }

        private static string BuildEmailHtml(
            string preheader,
            string headerLabel,
            string title,
            string greeting,
            string[] paragraphs,
            string ctaText,
            string ctaUrl,
            string accentColor = "#e53e3e")
        {
            var paragraphHtml = string.Join("", paragraphs.Select(p =>
                p.TrimStart().StartsWith("<table", StringComparison.OrdinalIgnoreCase)
                    ? p
                    : $@"<p style=""margin:0 0 18px 0;font-size:15px;line-height:1.75;color:#4b5563;"">{p}</p>"));

            var year = DateTime.UtcNow.Year;
            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>{System.Net.WebUtility.HtmlEncode(title)}</title>
</head>
<body style=""margin:0;padding:0;background:#f3f4f6;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Helvetica Neue',Arial,sans-serif;"">
  <div style=""display:none;max-height:0;overflow:hidden;font-size:1px;color:#f3f4f6;"">{System.Net.WebUtility.HtmlEncode(preheader)} &#8203;&#8203;&#8203;</div>

  <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""background:#f3f4f6;padding:40px 16px;"">
    <tr><td align=""center"">
      <table cellpadding=""0"" cellspacing=""0"" border=""0"" style=""max-width:600px;width:100%;"">

        <!-- Brand header -->
        <tr>
          <td style=""background:#111827;border-radius:16px 16px 0 0;overflow:hidden;"">
            <!-- Red accent stripe -->
            <div style=""height:5px;background:{accentColor};""></div>
            <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"" style=""padding:22px 36px;"">
              <tr>
                <td style=""vertical-align:middle;"">
                  <table cellpadding=""0"" cellspacing=""0"" border=""0"">
                    <tr>
                      <td style=""font-size:24px;font-weight:900;color:#ffffff;letter-spacing:0.05em;"">Tugs!</td>
                    </tr>
                  </table>
                </td>
                <td align=""right"" style=""vertical-align:middle;"">
                  <span style=""display:inline-block;font-size:10px;font-weight:700;text-transform:uppercase;letter-spacing:0.18em;color:{accentColor};border:1.5px solid {accentColor}60;padding:5px 14px;border-radius:50px;"">{System.Net.WebUtility.HtmlEncode(headerLabel)}</span>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <!-- Title band -->
        <tr>
          <td style=""background:#1f2937;padding:28px 36px 32px;"">
            <h1 style=""margin:0 0 8px 0;font-size:26px;font-weight:800;color:#ffffff;line-height:1.25;letter-spacing:-0.02em;"">{System.Net.WebUtility.HtmlEncode(title)}</h1>
            <p style=""margin:0;font-size:14px;color:#9ca3af;"">{System.Net.WebUtility.HtmlEncode(greeting)}</p>
          </td>
        </tr>

        <!-- Body -->
        <tr>
          <td style=""background:#ffffff;padding:36px 36px 28px;"">
            {paragraphHtml}
          </td>
        </tr>

        <!-- CTA -->
        <tr>
          <td style=""background:#ffffff;padding:0 36px 40px;"">
            <table cellpadding=""0"" cellspacing=""0"" border=""0"">
              <tr>
                <td style=""border-radius:10px;overflow:hidden;"">
                  <a href=""{ctaUrl}"" style=""display:block;background:{accentColor};color:#ffffff;font-size:13px;font-weight:700;text-transform:uppercase;letter-spacing:0.14em;padding:15px 32px;text-decoration:none;border-radius:10px;"">{System.Net.WebUtility.HtmlEncode(ctaText)} &rarr;</a>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <!-- Footer -->
        <tr>
          <td style=""background:#111827;border-radius:0 0 16px 16px;padding:28px 36px;"">
            <table cellpadding=""0"" cellspacing=""0"" border=""0"" width=""100%"">
              <tr>
                <td>
                  <p style=""margin:0 0 4px 0;font-size:13px;font-weight:800;color:#ffffff;letter-spacing:0.04em;"">Tugs!</p>
                  <p style=""margin:0;font-size:12px;color:#6b7280;"">Talent Booking &amp; Live Events · Metro Manila</p>
                </td>
                <td align=""right"" style=""vertical-align:top;"">
                  <p style=""margin:0;font-size:11px;color:#4b5563;"">&copy; {year} Tugs!</p>
                </td>
              </tr>
            </table>
            <p style=""margin:20px 0 0 0;font-size:11px;color:#374151;line-height:1.6;border-top:1px solid #1f2937;padding-top:18px;"">
              You received this because you have an account or submitted a request on Tugs!. If this was unexpected, you can safely ignore it.
            </p>
          </td>
        </tr>

      </table>
    </td></tr>
  </table>
</body>
</html>";
        }
    }
}
