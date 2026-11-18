using C0302_HoangThai.Service.S0302;
using Microsoft.AspNetCore.Mvc;
using iText.Kernel.Pdf;
using ClosedXML.Excel;
using System.Text.RegularExpressions;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamer3PageController(PdfJobs jobs, PdfOcr ocr) : PdfJobController(jobs)
    {
        // Mỗi tờ khai = 3 trang
        private const int PagesPerDoc = 3;

        [HttpGet]
        public IActionResult Upload() => View();

        // ════════════════════════════════════════════════════════════
        //  BƯỚC 1: Nhận 1 PDF lớn → tách theo dãy/số trang → xuất Excel
        //  Ưu tiên: pageRanges > pagesPerFile > mặc định 3 trang
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> Step1_SplitAndExcel(IFormFile bigPdf, int? pagesPerFile, string? pageRanges)
        {
            if (bigPdf == null || bigPdf.Length == 0 || !bigPdf.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IActionResult>(BadRequest("Vui lòng chọn file .pdf."));
            if (pagesPerFile is < 1 or > 1000)
                return Task.FromResult<IActionResult>(BadRequest("Số trang mỗi file phải từ 1 đến 1000."));

            return RunJobAsync([bigPdf], (job, inputs) => SplitRanges(job, inputs[0], pagesPerFile, pageRanges));
        }

        private PdfJobOutput SplitRanges(PdfJob job, string path, int? pagesPerFile, string? pageRanges)
        {
            using var source = new PdfDocument(new PdfReader(path));
            using var scan = ocr.Open(path);
            int totalPages = source.GetNumberOfPages();
            var ranges = string.IsNullOrWhiteSpace(pageRanges)
                ? PdfPageRanges.BySize(totalPages, pagesPerFile ?? PagesPerDoc)
                : PdfPageRanges.FromStarts(pageRanges, totalPages);
            var outputFolder = Directory.CreateDirectory(Path.Combine(job.Directory, "output")).FullName;
            var rows = new List<(string ChunkName, string Stk1, string Stk2, string Stk3, string TenDatFile)>();
            int total = ranges.Sum(range => Math.Min(3, range.End - range.Start + 1));
            int completed = 0;
            job.ReportUnits(completed, total, "Đang đọc số tờ khai.");

            for (int i = 0; i < ranges.Count; i++)
            {
                var (start, end) = ranges[i];
                string[] numbers = ["", "", ""];
                for (int offset = 0; offset < Math.Min(3, end - start + 1); offset++)
                {
                    double crop = offset == 1 ? 0.35 : 0.22;
                    numbers[offset] = scan.ReadDeclaration(source, start + offset, crop, ExtractSoToKhai);
                    job.ReportUnits(++completed, total, "Đang đọc số tờ khai.");
                }

                string name = VoteSoToKhai(numbers[0], numbers[1], numbers[2], i + 1);
                string fileName = PdfPageRanges.WriteChunk(source, start, end, outputFolder, SanitizeFileName(name), firstSuffix: 1);
                rows.Add((fileName, numbers[0], numbers[1], numbers[2], name));
            }

            job.Report(90, "Đang tạo Mapping.xlsx.");
            BuildExcel(rows, Path.Combine(outputFolder, "Mapping.xlsx"));
            return job.CreateZip(outputFolder, $"Step1_ToKhai_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        }

        // ════════════════════════════════════════════════════════════
        //  BƯỚC 2: ZIP chunks + Excel đã điền Tên mới → ZIP PDF đổi tên
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> Step2_RenameFiles(IFormFile pdfZip, IFormFile mappingExcel)
        {
            if (pdfZip == null || pdfZip.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Thiếu file ZIP chứa PDF chunks."));
            if (mappingExcel == null || mappingExcel.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Thiếu file Excel mapping."));

            return RunJobAsync([pdfZip, mappingExcel], (job, inputs) => Rename(job, inputs[0], inputs[1]));
        }

        private PdfJobOutput Rename(PdfJob job, string zipPath, string excelPath)
        {
            string tempFolder = job.Directory;
            // Giải nén ZIP
            var pdfFolder = Path.Combine(tempFolder, "pdfs");
            Directory.CreateDirectory(pdfFolder);
            job.ExtractZip(zipPath, pdfFolder);

            // Đọc Excel

            // Dict: FileName -> (TenMoi, TenDatFile)
            var mapping = new Dictionary<string, (string TenMoi, string TenDatFile)>(
                StringComparer.OrdinalIgnoreCase);

            using (var excel = System.IO.File.OpenRead(excelPath))
            using (var wb = new XLWorkbook(excel))
            {
                var ws = wb.Worksheet(1);
                int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                for (int r = 2; r <= lastRow; r++)
                {
                    string orig = ws.Cell(r, 1).GetString().Trim();
                    string tenDatFile = ws.Cell(r, 5).GetString().Trim(); // Col E: TenDatFile (voting)
                    string tenMoi = ws.Cell(r, 6).GetString().Trim();     // Col F: TenMoi (user điền)

                    if (string.IsNullOrEmpty(orig)) continue;
                    mapping[orig] = (tenMoi, tenDatFile);
                }
            }

            var outFolder = Path.Combine(tempFolder, "renamed");
            Directory.CreateDirectory(outFolder);

            var sourceFiles = Directory.GetFiles(pdfFolder);
            job.ReportUnits(0, sourceFiles.Length, "Đang đổi tên tệp.", 20, 90);
            int renamed = 0;
            foreach (var srcFile in sourceFiles)
            {
                job.ReportUnits(renamed++, sourceFiles.Length, "Đang đổi tên tệp.", 20, 90);
                string originalName = Path.GetFileName(srcFile);
                if (originalName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    continue; // bỏ qua Excel nếu nằm trong ZIP

                string newName;
                if (mapping.TryGetValue(originalName, out var info))
                {
                    string tenDatFile = string.IsNullOrWhiteSpace(info.TenDatFile)
                        ? Path.GetFileNameWithoutExtension(originalName)
                        : info.TenDatFile;

                    if (!string.IsNullOrWhiteSpace(info.TenMoi))
                        // TenMoi - TenDatFile.pdf
                        newName = $"{info.TenMoi}_{tenDatFile}.pdf";
                    else
                        // Không điền TenMoi → dùng TenDatFile (kết quả voting)
                        newName = $"{tenDatFile}.pdf";
                }
                else
                {
                    newName = originalName;
                }

                string finalName = SanitizeFileName(Path.GetFileNameWithoutExtension(newName));
                string target = PdfPageRanges.AvailableName(outFolder, finalName, ".pdf", firstSuffix: 1);
                System.IO.File.Copy(srcFile, Path.Combine(outFolder, target));
            }

            job.ReportUnits(sourceFiles.Length, sourceFiles.Length, "Đang đổi tên tệp.", 20, 90);
            return job.CreateZip(outFolder, $"Renamed_PDFs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        }

        // ════════════════════════════════════════════════════════════
        //  Voting majority
        // ════════════════════════════════════════════════════════════
        private string VoteSoToKhai(string s1, string s2, string s3, int unknownIndex)
        {
            var candidates = new List<(string Value, int Priority)>();
            if (!string.IsNullOrEmpty(s1)) candidates.Add((s1, 1));
            if (!string.IsNullOrEmpty(s2)) candidates.Add((s2, 2));
            if (!string.IsNullOrEmpty(s3)) candidates.Add((s3, 3));

            if (candidates.Count == 0) return $"unknown_{unknownIndex}";
            if (candidates.Count == 1) return candidates[0].Value;

            (string Rep, int BestPriority) winner = (string.Empty, int.MaxValue);

            for (int i = 0; i < candidates.Count; i++)
                for (int j = i + 1; j < candidates.Count; j++)
                    if (candidates[i].Value == candidates[j].Value)
                    {
                        int best = Math.Min(candidates[i].Priority, candidates[j].Priority);
                        string rep = candidates[i].Priority <= candidates[j].Priority
                            ? candidates[i].Value : candidates[j].Value;
                        if (best < winner.BestPriority)
                            winner = (rep, best);
                    }

            // Không có cặp exact → ưu tiên trang 1
            return string.IsNullOrEmpty(winner.Rep)
                ? candidates.OrderBy(c => c.Priority).First().Value
                : winner.Rep;
        }

        // ════════════════════════════════════════════════════════════
        //  Tìm Số tờ khai trong OCR text
        // ════════════════════════════════════════════════════════════
        private string ExtractSoToKhai(string ocrText)
        {
            if (string.IsNullOrEmpty(ocrText)) return string.Empty;

            string clean = NormalizeOCRText(ocrText);
            var lines = clean.Split('\n');
            var rawNumbers = new List<string>();

            // ── Ưu tiên 1: dòng có nhãn "so to khai" ──────────────────
            //   Trang 1: "Số tờ khai  308487879400  Số tờ khai đầu tiên"
            //   Trang 2: "4. Số tờ khai :  308487879400"
            foreach (var line in lines)
            {
                bool isToKhaiLine =
                    Regex.IsMatch(line, @"s[o0].{0,4}t[o0].{0,4}khai", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(line, @"\d\.\s*s.{0,5}t.{0,5}khai", RegexOptions.IgnoreCase);

                if (!isToKhaiLine) continue;

                foreach (Match m in Regex.Matches(line, @"[0-9OIlSB]{10,15}"))
                    rawNumbers.Add(NormalizeDigits(m.Value));
            }

            // ── Ưu tiên 2: quét toàn trang, bỏ dòng nhiễu ─────────────
            if (!rawNumbers.Any())
            {
                foreach (var line in lines)
                {
                    string lower = line.ToLower();
                    bool badLine =
                        lower.Contains("invoice") || lower.Contains("packing") ||
                        lower.Contains("hawb") || lower.Contains("tracking") ||
                        lower.Contains("bill to") || lower.Contains("ship to") ||
                        lower.Contains("vat") || lower.Contains("dhl") ||
                        lower.Contains("fedex") || lower.Contains("ups");
                    if (badLine) continue;

                    foreach (Match m in Regex.Matches(line, @"[0-9OIlSB]{10,15}"))
                        rawNumbers.Add(NormalizeDigits(m.Value));
                }
            }

            foreach (var n in rawNumbers.Distinct())
                if (IsValidSoToKhai(n)) return n;

            return string.Empty;
        }

        // ════════════════════════════════════════════════════════════
        //  Build Excel — 6 cột
        // ════════════════════════════════════════════════════════════
        private void BuildExcel(
            List<(string ChunkName, string Stk1, string Stk2, string Stk3, string TenDatFile)> rows,
            string outputPath)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            ws.Cell(1, 1).Value = "Tên file gốc (chunk)";
            ws.Cell(1, 2).Value = "Số tờ khai (trang 1)";
            ws.Cell(1, 3).Value = "Số tờ khai (trang 2)";
            ws.Cell(1, 4).Value = "Số tờ khai (trang 3)";
            ws.Cell(1, 5).Value = "TenDatFile (voting)";
            ws.Cell(1, 6).Value = "TenMoi (để trống = dùng TenDatFile)";

            var hdr = ws.Range(1, 1, 1, 6);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            hdr.Style.Font.FontColor = XLColor.White;
            hdr.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            for (int i = 0; i < rows.Count; i++)
            {
                int r = i + 2;
                var (name, stk1, stk2, stk3, tenDatFile) = rows[i];

                ws.Cell(r, 1).Value = name;
                ws.Cell(r, 2).Value = stk1;
                ws.Cell(r, 3).Value = stk2;
                ws.Cell(r, 4).Value = stk3;
                ws.Cell(r, 5).Value = tenDatFile;
                ws.Cell(r, 6).Value = ""; // Người dùng điền

                ws.Cell(r, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

                bool allUnknown = string.IsNullOrEmpty(stk1)
                               && string.IsNullOrEmpty(stk2)
                               && string.IsNullOrEmpty(stk3);
                if (allUnknown || tenDatFile.StartsWith("unknown_"))
                {
                    ws.Cell(r, 5).Style.Font.FontColor = XLColor.Red;
                    ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE0E0");
                }
            }

            ws.Column(1).Width = 28;
            ws.Column(2).Width = 22;
            ws.Column(3).Width = 22;
            ws.Column(4).Width = 22;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 36;
            ws.SheetView.Freeze(1, 0);

            if (rows.Count > 0)
            {
                var dataRange = ws.Range(1, 1, rows.Count + 1, 6);
                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            wb.SaveAs(outputPath);
        }

        // ════════════════════════════════════════════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════

        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12) return false;
            if (v.StartsWith("2024") || v.StartsWith("2025") || v.StartsWith("2026")) return false;
            if (v.StartsWith("0000")) return false;
            return true;
        }

        private string NormalizeOCRText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return Regex.Replace(text, @"[ \t]+", " ");
        }

        private string NormalizeDigits(string value) =>
            value
                .Replace("O", "0").Replace("o", "0")
                .Replace("I", "1").Replace("l", "1")
                .Replace("|", "1").Replace("B", "8")
                .Replace("S", "5").Replace(" ", "")
                .Replace(".", "").Replace(",", "");

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";
            char[] invalid = [.. Path.GetInvalidFileNameChars(), .. "<>:\"/\\|?*"];
            string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return safe[..Math.Min(safe.Length, 150)];
        }
    }
}