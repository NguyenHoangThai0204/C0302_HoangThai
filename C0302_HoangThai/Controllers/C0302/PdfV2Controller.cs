using C0302_HoangThai.Service.S0302;
using Microsoft.AspNetCore.Mvc;

using iText.Kernel.Pdf;

using ClosedXML.Excel;

using System.Text.RegularExpressions;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfV2Controller(PdfJobs jobs, PdfOcr ocr) : PdfJobController(jobs)
    {
        // =========================================================
        // GET
        // =========================================================

        [HttpGet]
        public IActionResult Upload() => View();

        // =========================================================
        // POST: Tach PDF theo day so trang do user nhap
        // =========================================================

        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> SplitByRange(IFormFile file, string pageRanges)
        {
            if (file == null || file.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Vui lòng chọn file PDF."));
            if (string.IsNullOrWhiteSpace(pageRanges))
                return Task.FromResult<IActionResult>(BadRequest("Vui lòng nhập dãy số trang."));

            return RunJobAsync([file], (job, inputs) => SplitRanges(job, inputs[0], pageRanges));
        }

        private PdfJobOutput SplitRanges(PdfJob job, string path, string pageRanges)
        {
            using var source = new PdfDocument(new PdfReader(path));
            using var scan = ocr.Open(path);
            var ranges = PdfPageRanges.FromStarts(pageRanges, source.GetNumberOfPages());
            var outputFolder = Directory.CreateDirectory(Path.Combine(job.Directory, "output")).FullName;
            var rows = new List<(string FileName, string SoToKhai1, string SoToKhai2, string SoToKhai3,
                string TenDatFile, int StartPage, int EndPage)>();
            int total = ranges.Sum(range => Math.Min(3, range.End - range.Start + 1));
            int completed = 0;
            job.ReportUnits(completed, total, "Đang đọc số tờ khai.");

            for (int i = 0; i < ranges.Count; i++)
            {
                var (start, end) = ranges[i];
                string[] numbers = ["", "", ""];
                for (int offset = 0; offset < Math.Min(3, end - start + 1); offset++)
                {
                    numbers[offset] = scan.ReadDeclaration(source, start + offset, 0.22, ExtractSoToKhai);
                    job.ReportUnits(++completed, total, "Đang đọc số tờ khai.");
                }

                string name = VoteSoToKhai(numbers[0], numbers[1], numbers[2], i + 1);
                string fileName = PdfPageRanges.WriteChunk(source, start, end, outputFolder, SanitizeFileName(name), firstSuffix: 2);
                rows.Add((fileName, numbers[0], numbers[1], numbers[2], name, start, end));
            }

            job.Report(90, "Đang tạo Mapping.xlsx.");
            BuildExcel(rows, Path.Combine(outputFolder, "Mapping.xlsx"));
            return job.CreateZip(outputFolder, $"KetQua_{DateTime.Now:yyyyMMddHHmmss}.zip");
        }

        // =========================================================
        // POST: Doi ten PDF
        // Ghep: TenMoi - TenDatFile.pdf
        // =========================================================

        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> RenameFiles(IFormFile pdfZip, IFormFile mappingExcel)
        {
            if (pdfZip == null || pdfZip.Length == 0 || mappingExcel == null || mappingExcel.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Vui long chon du 2 file."));

            return RunJobAsync([pdfZip, mappingExcel], (job, inputs) => Rename(job, inputs[0], inputs[1]));
        }

        private PdfJobOutput Rename(PdfJob job, string zipPath, string excelPath)
        {
            string tempFolder = job.Directory;
            // Doc Excel mapping

            // Dict: TenFileCu -> (TenMoi, TenDatFile)
            var mapping = new Dictionary<string, (string TenMoi, string TenDatFile)>(
                StringComparer.OrdinalIgnoreCase);

            using (var excel = System.IO.File.OpenRead(excelPath))
            using (var wb = new XLWorkbook(excel))
            {
                var ws = wb.Worksheet(1);
                int row = 2;

                while (true)
                {
                    // Col 1: Ten file  Col 6: TenMoi  Col 7: TenDatFile
                    var cellFile = ws.Cell(row, 1).GetString().Trim();
                    var cellTenDatFile = ws.Cell(row, 6).GetString().Trim();
                    var cellTenMoi = ws.Cell(row, 7).GetString().Trim();

                    if (string.IsNullOrEmpty(cellFile)) break;

                    if (!string.IsNullOrEmpty(cellTenMoi))
                        mapping[cellFile] = (cellTenMoi, cellTenDatFile);

                    row++;
                }
            }

            // Giai nen ZIP + doi ten + nen lai
            string extractDir = Path.Combine(tempFolder, "extracted");
            Directory.CreateDirectory(extractDir);

            job.ExtractZip(zipPath, extractDir);

            string outDir = Path.Combine(tempFolder, "renamed");
            Directory.CreateDirectory(outDir);

            var sourceFiles = Directory.GetFiles(extractDir);
            job.ReportUnits(0, sourceFiles.Length, "Đang đổi tên tệp.", 20, 90);
            int renamed = 0;
            foreach (var srcFile in sourceFiles)
            {
                job.ReportUnits(renamed++, sourceFiles.Length, "Đang đổi tên tệp.", 20, 90);
                string originalName = Path.GetFileName(srcFile);
                string newName;

                if (originalName.EndsWith(".xlsx",
                        StringComparison.OrdinalIgnoreCase))
                {
                    newName = originalName;
                }
                else if (mapping.TryGetValue(originalName, out var info))
                {
                    // TenMoi - TenDatFile.pdf
                    // Neu TenDatFile trong Excel bi bo trong -> dung ten file goc
                    string tenDatFile = string.IsNullOrEmpty(info.TenDatFile)
                        ? Path.GetFileNameWithoutExtension(originalName)
                        : info.TenDatFile;

                    newName = $"{info.TenMoi}_{tenDatFile}.pdf";
                }
                else
                {
                    newName = originalName;
                }

                string safeName = SanitizeFileName(newName);
                string target = PdfPageRanges.AvailableName(outDir, Path.GetFileNameWithoutExtension(safeName),
                    Path.GetExtension(safeName), firstSuffix: 2);
                System.IO.File.Copy(srcFile, Path.Combine(outDir, target));
            }

            job.ReportUnits(sourceFiles.Length, sourceFiles.Length, "Đang đổi tên tệp.", 20, 90);
            return job.CreateZip(outDir, $"Renamed_{DateTime.Now:yyyyMMddHHmmss}.zip");
        }

        // =========================================================
        // VOTING MAJORITY
        // =========================================================

        private string VoteSoToKhai(string s1, string s2, string s3, int unknownIndex)
        {
            var candidates = new List<(string Value, int Priority)>();
            if (!string.IsNullOrEmpty(s1)) candidates.Add((s1, 1));
            if (!string.IsNullOrEmpty(s2)) candidates.Add((s2, 2));
            if (!string.IsNullOrEmpty(s3)) candidates.Add((s3, 3));

            if (candidates.Count == 0)
                return $"unknown_{unknownIndex}";

            if (candidates.Count == 1)
                return candidates[0].Value;

            // Tìm cặp EXACT match (hoặc chỉ sai 1 ký tự do OCR)
            (string Rep, int BestPriority) winner = (string.Empty, int.MaxValue);

            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    if (candidates[i].Value == candidates[j].Value)
                    {
                        int bestPriority = Math.Min(candidates[i].Priority, candidates[j].Priority);
                        string rep = candidates[i].Priority <= candidates[j].Priority
                            ? candidates[i].Value
                            : candidates[j].Value;

                        if (bestPriority < winner.BestPriority)
                            winner = (rep, bestPriority);
                    }
                }
            }

            return string.IsNullOrEmpty(winner.Rep)
                ? $"unknown_{unknownIndex}"
                : winner.Rep;
        }

        // =========================================================
        // Tim so to khai tu OCR text (giong file goc)
        // =========================================================

        private string ExtractSoToKhai(string ocrText)
        {
            if (string.IsNullOrEmpty(ocrText))
                return string.Empty;

            string clean = NormalizeOCRText(ocrText);
            var lines = clean.Split('\n');

            var rawNumbers = new List<string>();

            // Uu tien dong co "so to khai"
            foreach (var line in lines)
            {
                bool isToKhaiLine = Regex.IsMatch(
                    line,
                    @"so\s*to\s*khai|phu\s*luc.*to\s*khai|to\s*khai\s*:?\s*[0-9]",
                    RegexOptions.IgnoreCase);

                if (!isToKhaiLine) continue;

                var nums = Regex.Matches(line, @"[0-9OIlSB]{10,15}");

                foreach (Match m in nums)
                    rawNumbers.Add(NormalizeDigits(m.Value));
            }

            // Fallback: quet toan trang (bo qua dong co nhieu)
            if (!rawNumbers.Any())
            {
                foreach (var line in lines)
                {
                    string lower = line.ToLower();

                    bool badLine =
                        lower.Contains("invoice") ||
                        lower.Contains("packing") ||
                        lower.Contains("hawb") ||
                        lower.Contains("tracking") ||
                        lower.Contains("bill to") ||
                        lower.Contains("ship to") ||
                        lower.Contains("vat") ||
                        lower.Contains("dhl") ||
                        lower.Contains("fedex") ||
                        lower.Contains("ups");

                    if (badLine) continue;

                    var nums = Regex.Matches(line, @"[0-9OIlSB]{10,15}");

                    foreach (Match m in nums)
                        rawNumbers.Add(NormalizeDigits(m.Value));
                }
            }

            foreach (var n in rawNumbers.Distinct())
            {
                if (IsValidSoToKhai(n))
                    return n;
            }

            return string.Empty;
        }

        // =========================================================
        // BuildExcel
        // Col 1: Ten file
        // Col 2: So to khai trang 1
        // Col 3: So to khai trang 2
        // Col 4: So to khai trang 3
        // Col 5: Trang bat dau
        // Col 6: TenMoi      <- nguoi dung dien
        // Col 7: TenDatFile  <- auto dien = ket qua voting (co the sua)
        // =========================================================

        private void BuildExcel(
                List<(string FileName,
                      string SoToKhai1,
                      string SoToKhai2,
                      string SoToKhai3,
                      string TenDatFile,
                      int StartPage,
                      int EndPage)> rows,
                string outputPath)
            {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            // Headers
            ws.Cell(1, 1).Value = "Ten file";
            ws.Cell(1, 2).Value = "So to khai (trang 1)";
            ws.Cell(1, 3).Value = "So to khai (trang 2)";
            ws.Cell(1, 4).Value = "So to khai (trang 3)";
            ws.Cell(1, 5).Value = "Trang bat dau";
            ws.Cell(1, 6).Value = "TenDatFile";          // nguoi dung dien
            ws.Cell(1, 7).Value = "TenMoi";      // = ket qua voting, co the sua

            var headerRow = ws.Row(1);
            headerRow.Style.Font.Bold = true;
            headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            headerRow.Style.Font.FontColor = XLColor.White;

            // Highlight cot nguoi dung can dien
            //ws.Column(6).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");
            //ws.Column(7).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");

            for (int i = 0; i < rows.Count; i++)
            {
                int r = i + 2;
                var row = rows[i];

                ws.Cell(r, 1).Value = row.FileName;
                ws.Cell(r, 2).Value = row.SoToKhai1;
                ws.Cell(r, 3).Value = row.SoToKhai2;
                ws.Cell(r, 4).Value = row.SoToKhai3;
                ws.Cell(r, 5).Value = row.StartPage;
                ws.Cell(r, 6).Value = row.TenDatFile;             // TenMoi: nguoi dung dien
                ws.Cell(r, 7).Value = ""; // TenDatFile: auto
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(outputPath);
        }

        // =========================================================
        // Helpers (giong file goc)
        // =========================================================

        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12) return false;

            if (!v.StartsWith("10") && !v.StartsWith("11"))
                return false;

            if (v.StartsWith("2024") || v.StartsWith("2025") || v.StartsWith("2026"))
                return false;

            if (v.StartsWith("030") || v.StartsWith("010"))
                return false;

            return true;
        }

        private bool IsLooseSimilar(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) ||
                string.IsNullOrWhiteSpace(b)) return false;

            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) diff++;

            if (diff <= 2) return true;

            bool samePrefix5 = a.Substring(0, 5) == b.Substring(0, 5);
            bool sameSuffix5 = a.Substring(a.Length - 5) == b.Substring(b.Length - 5);

            return samePrefix5 && sameSuffix5 && diff <= 3;
        }

        private string NormalizeOCRText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return Regex.Replace(text, @"[ \t]+", " ");
        }

        private string NormalizeDigits(string value)
        {
            return value
                .Replace("O", "0").Replace("o", "0")
                .Replace("I", "1").Replace("l", "1")
                .Replace("|", "1").Replace("B", "8")
                .Replace("S", "5").Replace(" ", "")
                .Replace(".", "").Replace(",", "");
        }

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";

            char[] invalid = [.. Path.GetInvalidFileNameChars(), .. "<>:\"/\\|?*"];
            string safe = new string(
                name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

            return safe[..Math.Min(safe.Length, 150)];
        }
    }
}