using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QRCoder;

namespace ImajinationAPI.Services
{
    public class TicketPdfService
    {
        public TicketPdfService()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public record TicketPdfData(
            Guid TicketId,
            string CustomerName,
            string EventTitle,
            string EventDate,
            string Venue,
            string City,
            string TierName,
            int Quantity,
            decimal TotalPrice,
            string OrderRef,
            bool IsUsed,
            IReadOnlySet<int>? UsedUnits = null
        );

        public byte[] GenerateTicketPdf(TicketPdfData data)
        {
            var qty = Math.Max(1, data.Quantity);
            var qrBytesList = Enumerable.Range(1, qty)
                .Select(unit => GenerateQrPng($"{data.TicketId}|{unit}"))
                .ToList();

            // Dark background colours — using Color.FromHex (no leading #)
            var bgDark    = Color.FromHex("0a0a0a");
            var bgPanel   = Color.FromHex("111111");
            var bgDivider = Color.FromHex("ffffff15");

            var document = Document.Create(container =>
            {
                for (int unit = 1; unit <= qty; unit++)
                {
                    var unitQr   = qrBytesList[unit - 1];
                    var unitNum  = unit;
                    var unitUsed = data.IsUsed || (data.UsedUnits?.Contains(unit) ?? false);

                    container.Page(page =>
                    {
                        page.Size(PageSizes.A5.Landscape());
                        page.Margin(0);
                        page.PageColor(bgDark);   // ← correct 2024 API (was Background)

                        page.Content().Row(row =>
                        {
                            // ── Left panel ──────────────────────────────────────────
                            row.RelativeItem(3).Background(bgDark).Padding(28).Column(col =>
                            {
                                // Header row: brand + ticket label
                                col.Item().Row(r =>
                                {
                                    r.RelativeItem().Text("Tugs!")
                                        .FontSize(10).Bold().FontColor("#e53e3e")
                                        .LetterSpacing(0.15f);
                                    r.AutoItem().Text(qty > 1 ? $"TICKET {unitNum} OF {qty}" : "DIGITAL TICKET")
                                        .FontSize(7).FontColor("#ffffff55")
                                        .LetterSpacing(0.15f);
                                });

                                col.Item().PaddingTop(14).Text(data.EventTitle)
                                    .FontSize(22).Bold().FontColor(Colors.White)
                                    .LineHeight(1.15f);

                                col.Item().PaddingTop(16).Column(details =>
                                {
                                    void DetailRow(string label, string value, string color = "#ffffff99")
                                    {
                                        details.Item().PaddingBottom(8).Row(r =>
                                        {
                                            r.ConstantItem(90).Text(label.ToUpperInvariant())
                                                .FontSize(7).FontColor("#ffffff44")
                                                .LetterSpacing(0.12f);
                                            r.RelativeItem().Text(value)
                                                .FontSize(10).Bold().FontColor(color);
                                        });
                                    }

                                    DetailRow("Date & Time", data.EventDate);
                                    DetailRow("Venue", data.Venue + (string.IsNullOrWhiteSpace(data.City) ? "" : $", {data.City}"));
                                    DetailRow("Ticket Holder", data.CustomerName);
                                    DetailRow("Tier", data.TierName);
                                    if (qty > 1)
                                        DetailRow("Ticket #", $"{unitNum} of {qty}");
                                    DetailRow("Total Paid", $"₱{data.TotalPrice:N2}", "#4ade80");
                                });

                                col.Item().PaddingTop(10)
                                    .Text(unitUsed ? "SCANNED / USED" : "VALID · PRESENT AT ENTRANCE")
                                    .FontSize(8).Bold()
                                    .FontColor(unitUsed ? "#f87171" : "#4ade80")
                                    .LetterSpacing(0.12f);

                                col.Item().PaddingTop(6)
                                    .Text($"Ref: {data.OrderRef.ToUpper()}-{unitNum}")
                                    .FontSize(7).FontColor("#ffffff33").LetterSpacing(0.1f);
                            });

                            // Thin divider
                            row.ConstantItem(1).Background(bgDivider);

                            // ── Right panel — QR ────────────────────────────────────
                            row.ConstantItem(158).Background(bgPanel).Padding(18).Column(col =>
                            {
                                col.Item().Extend().AlignMiddle().AlignCenter().Column(inner =>
                                {
                                    inner.Item().AlignCenter()
                                        .Width(110).Height(110)
                                        .Image(unitQr).FitArea();  // ← correct 2024 API (was Image(bytes, scaling))

                                    inner.Item().PaddingTop(10).AlignCenter()
                                        .Text("Scan at entrance")
                                        .FontSize(7).FontColor("#ffffff55")
                                        .LetterSpacing(0.1f);

                                    inner.Item().PaddingTop(6).AlignCenter()
                                        .Text($"{data.TicketId.ToString()[..8].ToUpper()}-{unitNum}")
                                        .FontSize(9).Bold().FontColor("#ffffff44")
                                        .LetterSpacing(0.1f);
                                });
                            });
                        });
                    });
                }
            });

            return document.GeneratePdf();
        }

        private static byte[] GenerateQrPng(string content)
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.H);
            var qrCode = new PngByteQRCode(qrData);
            return qrCode.GetGraphic(
                pixelsPerModule: 8,
                darkColorRgba:  new byte[] { 255, 255, 255, 255 },
                lightColorRgba: new byte[] { 17, 17, 17, 255 });
        }
    }
}
