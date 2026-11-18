using C0302_HoangThai.Service.S0302;
using Microsoft.AspNetCore.Mvc;

using iText.Kernel.Pdf;

using ClosedXML.Excel;

using System.Text.RegularExpressions;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamerPDFNSNNController(PdfJobs jobs, PdfOcr ocr) : PdfJobController(jobs)
    {
        private const string NSNN_SUFFIX = "_NSNN";
        private const string DEFAULT_DUOI_FILE = "NSNN";

        [HttpGet]
        public IActionResult Upload() => View();

        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> Step1_SplitAndExcel(IFormFile bigPdf, string? pageRanges, int? pagesToRead, string? readPosition)
        {
            if (bigPdf == null || bigPdf.Length == 0 || !bigPdf.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IActionResult>(BadRequest("Vui lòng chọn file .pdf."));
            if (string.IsNullOrWhiteSpace(pageRanges))
                return Task.FromResult<IActionResult>(BadRequest("Vui lòng nhập dãy số trang bắt đầu."));

            int count = pagesToRead is > 0 ? pagesToRead.Value : 3;
            if (count > 20)
                return Task.FromResult<IActionResult>(BadRequest("Số trang cần đọc phải từ 1 đến 20."));

            string position = readPosition?.Trim().ToLowerInvariant() switch
            {
                "bottom" => "bottom",
                "both" => "both",
                _ => "top"
            };

            return RunJobAsync([bigPdf], (job, inputs) => SplitRanges(job, inputs[0], pageRanges, count, position));
        }

        private PdfJobOutput SplitRanges(PdfJob job, string path, string pageRanges, int count, string position)
        {
            using var source = new PdfDocument(new PdfReader(path));
            using var scan = ocr.Open(path);
            var ranges = PdfPageRanges.FromStarts(pageRanges, source.GetNumberOfPages());
            var outputFolder = Directory.CreateDirectory(Path.Combine(job.Directory, "output")).FullName;
            var rows = new List<(string ChunkName, List<string> Stks, string TenDatFile)>();
            int total = ranges.Sum(range => Math.Min(position == "both" ? count * 2 : count, range.End - range.Start + 1));
            int completed = 0;
            job.ReportUnits(completed, total, "Đang đọc số tờ khai.");

            for (int i = 0; i < ranges.Count; i++)
            {
                var (start, end) = ranges[i];
                int pages = Math.Min(count, end - start + 1);
                var first = Enumerable.Range(start, pages);
                var last = Enumerable.Range(end - pages + 1, pages);
                var selected = position switch
                {
                    "bottom" => last,
                    "both" => first.Union(last),
                    _ => first
                };

                var numbers = new List<string>();
                foreach (int page in selected)
                {
                    numbers.Add(scan.ReadDeclaration(source, page, 1, ExtractSoToKhai));
                    job.ReportUnits(++completed, total, "Đang đọc số tờ khai.");
                }
                while (numbers.Count < count)
                    numbers.Add("");

                string name = VoteSoToKhai(numbers, i + 1) + NSNN_SUFFIX;
                string fileName = PdfPageRanges.WriteChunk(source, start, end, outputFolder, SanitizeFileName(name), firstSuffix: 1);
                rows.Add((fileName, numbers, name));
            }

            job.Report(90, "Đang tạo Mapping.xlsx.");
            BuildExcel(rows, count, position, Path.Combine(outputFolder, "Mapping.xlsx"));
            return job.CreateZip(outputFolder, $"Step1_NSNN_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        }

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
            var pdfFolder = Path.Combine(tempFolder, "pdfs");
            Directory.CreateDirectory(pdfFolder);
            job.ExtractZip(zipPath, pdfFolder);

            var mapping = new Dictionary<string, (string TenMoi, string DuoiFile)>(
                StringComparer.OrdinalIgnoreCase);

            using (var excel = System.IO.File.OpenRead(excelPath))
            using (var wb = new XLWorkbook(excel))
            {
                var ws = wb.Worksheet("Mapping");
                int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
                int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

                int colFileName = -1, colTenMoi = -1, colDuoiFile = -1;

                for (int c = 1; c <= lastCol; c++)
                {
                    string h = ws.Cell(1, c).GetString()?.Trim() ?? "";
                    if (h.StartsWith("Tên file", StringComparison.OrdinalIgnoreCase)) colFileName = c;
                    if (h.StartsWith("TenMoi", StringComparison.OrdinalIgnoreCase)) colTenMoi = c;
                    if (h.StartsWith("DuoiFile", StringComparison.OrdinalIgnoreCase)) colDuoiFile = c;
                }

                if (colFileName < 0 || colTenMoi < 0)
                    throw new PdfJobInputException("File Excel không đúng định dạng.");

                for (int r = 2; r <= lastRow; r++)
                {
                    string orig = ws.Cell(r, colFileName).GetString().Trim();
                    string tenMoi = ws.Cell(r, colTenMoi).GetString().Trim();
                    string? duoiFile = colDuoiFile > 0
                        ? ws.Cell(r, colDuoiFile).GetString()?.Trim()
                        : null;
                    // Không có cột / để trống -> mặc định NSNN
                    if (string.IsNullOrWhiteSpace(duoiFile)) duoiFile = DEFAULT_DUOI_FILE;

                    if (string.IsNullOrEmpty(orig)) continue;
                    mapping[orig] = (tenMoi, duoiFile);
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
                    continue;

                string newName;
                if (mapping.TryGetValue(originalName, out var info))
                {
                    // ✅ Tên mới = TenMoi + "_" + DuoiFile  (vd: 108712318960_NSNN.pdf)
                    // ✅ TenMoi để trống -> unknown_[DuoiFile].pdf
                    string prefix = !string.IsNullOrWhiteSpace(info.TenMoi) ? info.TenMoi : "unknown";
                    newName = $"{prefix}_{info.DuoiFile}.pdf";
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
            return job.CreateZip(outFolder, $"Renamed_NSNN_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        }

        private string ExtractSoToKhai(string ocrText)
        {
            if (string.IsNullOrEmpty(ocrText)) return string.Empty;

            string clean = NormalizeOCRText(ocrText);

            // FIX: bỏ "Số tờ khai đầu tiên <số>" (tờ khai chia nhỏ/tách luồng có 2 số trên
            // cùng 1 dòng: số tờ khai thật + số tờ khai đầu tiên). Nếu không bỏ, OCR đọc
            // "tờ" thành "td" nên nhãn thứ nhất không khớp, nhãn thứ hai khớp => lấy nhầm.
            clean = Regex.Replace(clean,
                @"(?:d[aâ]u|đầu)\s*ti[eê]n[ :.\-]*[0-9OIlSB]{11,12}", " ",
                RegexOptions.IgnoreCase);

            // 1. Neo vào chữ "khai" (chữ "Số", "tờ" hay bị OCR sai dấu nên không dùng).
            // Lấy số đứng NGAY SAU chữ "khai", theo thứ tự từ trên xuống: số đầu tiên hợp lệ
            // chính là số tờ khai. "khai đầu tiên <số>" có chữ "đầu tiên" chen giữa nên
            // không khớp. Cho phép OCR đọc "khai" thành "khal"/"kha1".
            foreach (Match lm in Regex.Matches(clean,
                @"kha[il1][ :.\-]{0,5}([0-9OIlSB]{11,12})\b", RegexOptions.IgnoreCase))
            {
                string stk = PadStk(NormalizeDigits(lm.Groups[1].Value));
                if (IsValidSoToKhai(stk)) return stk;
            }

            // 2. Tìm số đứng ngay trước ngày tháng (VD: 11087171980013/10/2026)
            // iText thường nối STT + STK + Ngày thành 1 chuỗi số dài
            var dateMatch = Regex.Match(clean, @"\b([0-9OIlSB]{11,12})\d{2}/\d{2}/\d{4}", RegexOptions.IgnoreCase);
            if (dateMatch.Success)
            {
                string stk = PadStk(NormalizeDigits(dateMatch.Groups[1].Value));
                if (IsValidSoToKhai(stk)) return stk;
            }

            // 3. Tìm CDF No
            var cdfMatch = Regex.Match(clean, @"cdf\s*no[\s\S]{0,20}?\b([0-9OIlSB]{11,12})\b", RegexOptions.IgnoreCase);
            if (cdfMatch.Success)
            {
                string stk = PadStk(NormalizeDigits(cdfMatch.Groups[1].Value));
                if (IsValidSoToKhai(stk)) return stk;
            }

            // 4. Fallback: Quét toàn bộ text tìm số 11-12 chữ số (bỏ qua các số quen thuộc như MST)
            // FIX: ưu tiên số bắt đầu bằng 10/11 (số tờ khai hải quan) để tránh
            // nhầm với mã vận đơn FedEx/DHL (VD: 384066982672) trên trang biên bản.
            var candidates = Regex.Matches(clean, @"\b([0-9OIlSB]{11,12})\b")
                .Cast<Match>()
                .Select(m => PadStk(NormalizeDigits(m.Value)))
                .Where(IsValidSoToKhai)
                .ToList();

            var preferred = candidates.FirstOrDefault(c => c.StartsWith("10") || c.StartsWith("11"));
            if (preferred != null) return preferred;

            return candidates.FirstOrDefault() ?? string.Empty;
        }

        private string VoteSoToKhai(List<string> stks, int unknownIndex)
        {
            var candidates = new List<(string Value, int Priority)>();
            for (int i = 0; i < stks.Count; i++)
                if (!string.IsNullOrEmpty(stks[i]))
                    candidates.Add((stks[i], i + 1));

            if (candidates.Count == 0) return $"unknown_{unknownIndex}";
            if (candidates.Count == 1) return candidates[0].Value;

            var groups = new List<(string Rep, List<int> Priorities)>();
            foreach (var (value, priority) in candidates)
            {
                bool merged = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    if (IsLooseSimilar(groups[g].Rep, value))
                    {
                        groups[g].Priorities.Add(priority);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    groups.Add((value, new List<int> { priority }));
            }

            var best = groups
                .OrderByDescending(g => g.Priorities.Count)
                .ThenBy(g => g.Priorities.Min())
                .First();

            return best.Rep;
        }

        private bool IsLooseSimilar(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) diff++;

            if (diff <= 2) return true;

            bool samePrefix5 = a.Substring(0, 5) == b.Substring(0, 5);
            bool sameSuffix5 = a.Substring(a.Length - 5) == b.Substring(b.Length - 5);
            return samePrefix5 && sameSuffix5 && diff <= 3;
        }

        private void BuildExcel(
            List<(string ChunkName, List<string> Stks, string TenDatFile)> rows,
            int N, string position, string outputPath)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            int colTenMoi = N + 2;     // để trống, người dùng tự lookup
            int colDuoiFile = N + 3;   // mặc định NSNN
            int lastCol = colDuoiFile;

            ws.Cell(1, 1).Value = "Tên file gốc (chunk)";
            for (int i = 0; i < N; i++)
                ws.Cell(1, 2 + i).Value = $"Số tờ khai #{i + 1}";
            ws.Cell(1, colTenMoi).Value = "TenMoi";
            ws.Cell(1, colDuoiFile).Value = "DuoiFile";

            var hdr = ws.Range(1, 1, 1, lastCol);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            hdr.Style.Font.FontColor = XLColor.White;
            hdr.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            hdr.Style.Alignment.WrapText = true;

            for (int r = 0; r < rows.Count; r++)
            {
                int row = r + 2;
                var (name, stks, tenDatFile) = rows[r];

                ws.Cell(row, 1).Value = name;
                for (int i = 0; i < N; i++)
                    ws.Cell(row, 2 + i).Value = i < stks.Count ? stks[i] : "";
                ws.Cell(row, colTenMoi).Value = "";
                ws.Cell(row, colDuoiFile).Value = DEFAULT_DUOI_FILE;

                ws.Cell(row, colTenMoi).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

                bool allEmpty = stks.All(string.IsNullOrEmpty);
                if (allEmpty || tenDatFile.StartsWith("unknown_"))
                {
                    ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE0E0");
                }
            }

            ws.Column(1).Width = 28;
            for (int i = 0; i < N; i++)
                ws.Column(2 + i).Width = 20;
            ws.Column(colTenMoi).Width = 28;
            ws.Column(colDuoiFile).Width = 14;
            ws.SheetView.Freeze(1, 1);

            if (rows.Count > 0)
            {
                var dataRange = ws.Range(1, 1, rows.Count + 1, lastCol);
                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            var info = wb.AddWorksheet("Info");
            info.Cell(1, 1).Value = "Chế độ đọc:";
            info.Cell(1, 2).Value = position switch
            {
                "bottom" => "Đọc từ CUỐI file",
                "both" => "Đọc cả ĐẦU & CUỐI file",
                _ => "Đọc từ ĐẦU file"
            };
            info.Cell(2, 1).Value = "Số trang đọc mỗi file:";
            info.Cell(2, 2).Value = N;
            info.Cell(3, 1).Value = "Tổng số file:";
            info.Cell(3, 2).Value = rows.Count;
            info.Cell(4, 1).Value = "Thời gian:";
            info.Cell(4, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            wb.SaveAs(outputPath);
        }

        // Chuẩn hóa độ dài STK thành 12 chữ số
        private string PadStk(string v)
        {
            // Nếu là 11 số và bắt đầu bằng 10 hoặc 11 -> tự động thêm số 0 vào cuối
            if (v.Length == 11 && (v.StartsWith("10") || v.StartsWith("11")))
                return v + "0";
            return v;
        }

        // Bắt buộc STK phải đúng 12 chữ số
        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12) return false;
            if (v.StartsWith("2024") || v.StartsWith("2025") || v.StartsWith("2026")) return false;
            if (v.StartsWith("0000")) return false;
            if (v.StartsWith("030") || v.StartsWith("010")) return false;
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