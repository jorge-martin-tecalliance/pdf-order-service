using pdf_extractor.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;


namespace pdf_extractor.Services
{
    public sealed class PdfExtractor : IPdfExtractor
    {
        public OrderDocument ExtractOrder(Stream pdfStream)
        {
            if (pdfStream is null) throw new ArgumentNullException(nameof(pdfStream));
            if (!pdfStream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(pdfStream));
            pdfStream.Position = 0;

            var order = new OrderDocument();
            using var doc = PdfDocument.Open(pdfStream);

            bool firstPageParsed = false;

            foreach (var page in doc.GetPages())
            {
                // Items -> always check every page
                foreach (var line in BuildLines(page))
                {
                    var item = TryParseLineItem(line);
                    if (item != null)
                    {
                        order.Items.Add(item);
                        Debug.WriteLine(item);
                    }
                }

                // For delivery, order, customer: only parse once (first page)
                if (!firstPageParsed)
                {
                    var delivery = TryParseDeliveryAddress(page);
                    if (delivery != null)
                    {
                        order.DeliveryAddress = delivery;
                        Debug.WriteLine(delivery);
                    }

                    var orderInfo = TryParseOrderInfo(page);
                    if (orderInfo != null)
                    {
                        order.OrderInfo = orderInfo;
                        Debug.WriteLine(orderInfo);
                    }

                    var customerInfo = TryParseCustomerInfo(page);
                    if (customerInfo != null)
                    {
                        order.CustomerInfo = customerInfo;
                        Debug.WriteLine(customerInfo);
                    }

                    firstPageParsed = true;
                }
            }

            return order;
        }

        // ---------- Build lines (top→bottom; inside each line left→right) ----------
        private sealed record Tok(string Text, double X, double Y);

        private static List<List<Tok>> BuildLines(Page page)
        {
            var lines = new List<List<Tok>>();
            var words = page.GetWords().Select(w =>
                new Tok(w.Text, w.BoundingBox.Left, (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0)).ToList();

            if (words.Count == 0) { lines.Add(new() { new Tok(page.Text ?? "", 0, 0) }); return lines; }

            // sort reading order
            words.Sort((a, b) =>
            {
                int cmpY = -a.Y.CompareTo(b.Y); // top first
                return cmpY != 0 ? cmpY : a.X.CompareTo(b.X);
            });

            const double yTol = 3.0; // a bit generous to avoid splitting one line
            var current = new List<Tok>();
            double? curY = null;

            void Flush()
            {
                if (current.Count == 0) return;
                current.Sort((a, b) => a.X.CompareTo(b.X));
                lines.Add(current);
                current = new List<Tok>();
                curY = null;
            }

            foreach (var t in words)
            {
                if (curY is null || Math.Abs(t.Y - curY.Value) <= yTol)
                {
                    current.Add(t);
                    curY ??= t.Y;
                }
                else
                {
                    Flush();
                    current.Add(t);
                    curY = t.Y;
                }
            }
            Flush();
            return lines;
        }

        // ---------- Parse a single line by columns ----------
        private static LineItem? TryParseLineItem(List<Tok> line)
        {
            if (line.Count < 8)
            {
                return null;
            }

            int i = 0;
            if (!IsPart(line[i].Text))
            {
                return null;
            }
            string part = line[i++].Text;

            // Look for description end pattern
            int descEnd = -1;
            for (int j = i; j <= line.Count - 6; j++)
            {
                if (IsInt(line[j].Text) && IsAlnum(line[j + 1].Text) && IsPctOrDec(line[j + 2].Text))
                {
                    descEnd = j;
                    break;
                }
            }
            if (descEnd == -1)
            {
                return null;
            }

            string description = string.Join(" ", line.Skip(i).Take(descEnd - i).Select(t => t.Text)).Trim();
            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            int qty = SafeInt(line[descEnd].Text);
            string dc = line[descEnd + 1].Text;
            decimal discountPct = SafePercent(line[descEnd + 2].Text);

            int k = descEnd + 3;
            var retail = TakeCurrency(line, ref k);
            var netUnit = TakeCurrency(line, ref k);
            var netSum = TakeCurrency(line, ref k);

            return new LineItem
            {
                PartNumber = part,
                Description = description,
                Quantity = qty,
                DiscountCode = dc,
                DiscountPct = discountPct,
                RetailPerUnit = retail.Value,
                NetPerUnit = netUnit.Value,
                NetSummary = netSum.Value
            };
        }

        // ---------- Parse Address ----------
        private DeliveryAddress? TryParseDeliveryAddress(Page page)
        {
            double maxX = 300;

            var lines = page.GetWords()
                .Where(w => w.BoundingBox.Left <= maxX)
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                .ToList();

            var startIndex = lines.FindIndex(l =>
                l.Contains("Delivery", StringComparison.OrdinalIgnoreCase) &&
                l.Contains("address", StringComparison.OrdinalIgnoreCase));

            if (startIndex == -1)
            {
                return null;
            }

            string? nameLine = null;
            string? companyLine = null;
            string? streetLine = null;
            string? cityStateZipLine = null;
            string? country = null;

            int found = 0;
            for (int i = startIndex + 1; i < lines.Count && found < 5; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (found == 0)
                        nameLine = line;
                    else if (found == 1)
                        companyLine = line;
                    else if (found == 2)
                        streetLine = line;
                    else if (found == 3)
                        cityStateZipLine = line;
                    else if (found == 4)
                        country = line;

                    found++;
                }
            }

            if (string.IsNullOrWhiteSpace(nameLine))
            {
                return null;
            }

            // Parse city, state, zip
            string? city = null, state = null, zip = null;
            if (!string.IsNullOrWhiteSpace(cityStateZipLine))
            {
                var parts = cityStateZipLine.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var left = parts[0].Trim();
                    // If it begins with digits, it's actually zip+city
                    if (char.IsDigit(left.FirstOrDefault()))
                    {
                        var tokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length > 1)
                        {
                            zip = tokens[0];
                            city = string.Join(" ", tokens.Skip(1));
                        }
                    }
                    else
                    {
                        city = left;
                    }
                }

                if (parts.Length > 1)
                {
                    var stateZip = parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (stateZip.Length > 0) state = stateZip[0];
                    if (stateZip.Length > 1 && zip == null) zip = stateZip[1];
                }
            }

            return new DeliveryAddress
            {
                RecipientName = nameLine,
                CompanyName = companyLine,
                Street = streetLine,
                City = city,
                State = state,
                ZipCode = zip,
                Country = country
            };
        }

        // ---------- Parse Order Info ----------
        private OrderInfo? TryParseOrderInfo(Page page)
        {
            // Get all text from the page
            var pageText = string.Join(" ", page.GetWords().Select(w => w.Text));

            // Regular expressions that look for specific words in the document
            var orderNumberMatch = Regex.Match(pageText, @"Order\s*Number\s*[:\-]?\s*(\S+)", RegexOptions.IgnoreCase);
            var orderDateMatch = Regex.Match(pageText, @"Date\s*of\s*Order\s*[:\-]?\s*([0-9\/\-\.]+)", RegexOptions.IgnoreCase);
            var orderClassMatch = Regex.Match(pageText, @"Order\s*Class\s*[:\-]?\s*(\w+)", RegexOptions.IgnoreCase);
            var deliveryWayMatch = Regex.Match(pageText, @"Delivery\s*Way\s*[:\-]?\s*(\w+)", RegexOptions.IgnoreCase);
            var paymentMethodMatch = Regex.Match(pageText, @"Payment\s*Method\s*[:\-]?\s*(\w+)", RegexOptions.IgnoreCase);
            var itemsAmountMatch = Regex.Match(pageText, @"Items\s*[:\-]?\s*(\d+)", RegexOptions.IgnoreCase);
            var netWeightMatch = Regex.Match(pageText, @"Net\s*weight\s*\(lbs\)\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);

            string? orderNumber = orderNumberMatch.Success ? orderNumberMatch.Groups[1].Value : null;
            string? orderDate = orderDateMatch.Success ? orderDateMatch.Groups[1].Value : null;
            string? orderClass = orderClassMatch.Success ? orderClassMatch.Groups[1].Value : null;
            string? deliveryWay = deliveryWayMatch.Success ? deliveryWayMatch.Groups[1].Value : null;
            string? paymentMethod = paymentMethodMatch.Success ? paymentMethodMatch.Groups[1].Value : null;
            string? itemsAmount = itemsAmountMatch.Success ? itemsAmountMatch.Groups[1].Value : null;
            string? netWeight = netWeightMatch.Success ? netWeightMatch.Groups[1].Value : null;

            return new OrderInfo
            {
                OrderNumber = orderNumber,
                OrderDate = orderDate,
                OrderClass = orderClass,
                DeliveryWay = deliveryWay,
                PaymentMethod = paymentMethod,
                ItemsAmount = itemsAmount,
                NetWeight = netWeight
            };
        }

        // ---------- Parse Order Info ----------
        private CustomerInfo? TryParseCustomerInfo(Page page)
        {
            // Define region of interest (ROI)
            double leftMin = page.Width * 0.50;    // right half
            double topMin = page.Height * 0.50;    // top 50%

            var regionLines = page.GetWords()
                .Where(w => w.BoundingBox.Left >= leftMin &&
                            w.BoundingBox.Top >= topMin)
                .GroupBy(w => Math.Round(w.BoundingBox.Top, 0))
                .OrderByDescending(g => g.Key)
                .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))
                .ToList();

            var regionText = string.Join(Environment.NewLine, regionLines);

            //Debug.WriteLine("=== REGION TEXT START ===");
            //Debug.WriteLine(regionText);
            //Debug.WriteLine("=== REGION TEXT END ===");

            // Join multi-line values (e.g. Customer George / Reece)
            string? dmsNumber = null, customer = null, company = null, email = null, phone = null, ctdId = null;

            for (int i = 0; i < regionLines.Count; i++)
            {
                string line = regionLines[i].Trim();

                // DMS Number
                if (line.StartsWith("DMS customer", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(' ');
                    if (parts.Length > 2) dmsNumber = parts.Last();
                }

                // Customer (after "number" line)
                if (line.Equals("number", StringComparison.OrdinalIgnoreCase) && i + 1 < regionLines.Count)
                {
                    if (regionLines[i + 1].StartsWith("Customer", StringComparison.OrdinalIgnoreCase))
                    {
                        var nameParts = new List<string>();

                        string customerLine = regionLines[i + 1].Replace("Customer", "").Trim();
                        if (!string.IsNullOrWhiteSpace(customerLine))
                            nameParts.Add(customerLine);

                        for (int j = i + 2; j < regionLines.Count; j++)
                        {
                            string next = regionLines[j].Trim();
                            if (next.StartsWith("Company", StringComparison.OrdinalIgnoreCase) ||
                                next.StartsWith("Email", StringComparison.OrdinalIgnoreCase) ||
                                next.StartsWith("Phone", StringComparison.OrdinalIgnoreCase) ||
                                next.StartsWith("CTDI", StringComparison.OrdinalIgnoreCase))
                                break;

                            nameParts.Add(next);
                        }

                        customer = string.Join(" ", nameParts);
                    }
                }

                // Company
                if (line.StartsWith("Company", StringComparison.OrdinalIgnoreCase))
                {
                    company = line.Replace("Company", "").Trim();
                }

                // Email
                if (line.StartsWith("Email", StringComparison.OrdinalIgnoreCase))
                {
                    email = line.Replace("Email", "").Trim();
                }

                // Phone
                if (line.StartsWith("Phone", StringComparison.OrdinalIgnoreCase))
                {
                    phone = line.Replace("Phone", "").Trim();
                }

                // CTDI ID
                if (line.StartsWith("CTDI ID", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(' ');
                    if (parts.Length > 2) ctdId = parts.Last();
                }
            }

            return new CustomerInfo
            {
                DmsNumber = dmsNumber,
                Customer = customer,
                Company = company,
                Email = email,
                Phone = phone,
                CtdiId = ctdId,
            };
        }


        // ---------- helpers ----------
        private static bool IsPart(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{5,}$");
        private static bool IsInt(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+$");
        private static bool IsAlnum(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z0-9]+$");
        private static bool IsPctOrDec(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+(?:\.\d+)?%?$");

        private static int SafeInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static decimal SafePercent(string s)
        {
            s = s.Trim().TrimEnd('%');
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }

        // Consume $ + number, or $number, or ( $ number ) as negative; remove commas
        private static decimal? TakeCurrency(List<Tok> line, ref int idx)
        {
            if (idx >= line.Count) return null;

            bool neg = false;
            string t = line[idx].Text;

            if (t == "(") { neg = true; idx++; if (idx >= line.Count) return null; t = line[idx].Text; }
            if (t.StartsWith("($")) { neg = true; t = t[2..]; }
            else if (t.StartsWith("$(")) { neg = true; t = t[2..]; }

            if (t == "$") { idx++; if (idx >= line.Count) return null; t = line[idx].Text; }
            if (t.StartsWith("$")) t = t[1..];

            // close paren after number
            string next = (idx + 1 < line.Count) ? line[idx + 1].Text : "";
            bool closeParen = next == ")";
            t = t.Replace(",", "");

            if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var val)) return null;

            idx++; if (closeParen) idx++; // skip ")"
            return neg ? -val : val;
        }
    }
}