using C0302_HoangThai.Service.S0302;
using ClosedXML.Excel;
using iText.Kernel.Pdf;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamerPDFCuController(PdfJobs jobs, PdfOcr ocr) : PdfJobController(jobs)
    {
        [HttpGet]
        public IActionResult Upload() => View();

        // =========================================================
        // POST: Doi ten nhieu file PDF / ZIP
        // =========================================================
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> ProcessFiles(List<IFormFile> pdfFiles, IFormFile? zipFile)
        {
            if (zipFile is { Length: > 0 })
                return RunJobAsync([zipFile], (job, inputs) => RenameUploadedPdfs(job, inputs, null));

            var files = (pdfFiles ?? []).Where(file => file.Length > 0 && file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Vui lòng chọn các file PDF hoặc một file ZIP."));

            var names = files.Select(file => file.FileName.Replace('\\', '/').Split('/')[^1]).ToArray();
            return RunJobAsync(files, (job, inputs) => RenameUploadedPdfs(job, inputs, names));
        }

        private PdfJobOutput RenameUploadedPdfs(PdfJob job, string[] inputs, string[]? names)
        {
            var output = Directory.CreateDirectory(Path.Combine(job.Directory, "output")).FullName;
            if (names is null)
            {
                var extracted = Directory.CreateDirectory(Path.Combine(job.Directory, "extracted")).FullName;
                job.ExtractZip(inputs[0], extracted);
                inputs = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
                names = inputs.Select(path => Path.GetFileName(path)).ToArray();
            }

            if (inputs.Length == 0)
                throw new PdfJobInputException("ZIP không có file PDF.");

            var log = new List<string>();
            job.ReportUnits(0, inputs.Length, "Đang đọc Invoice và AWB.");
            for (int i = 0; i < inputs.Length; i++)
            {
                try
                {
                    using var source = new PdfDocument(new PdfReader(inputs[i]));
                    using var scan = ocr.Open(inputs[i]);
                    int pages = source.GetNumberOfPages();
                    if (pages == 0)
                        throw new iText.Kernel.Exceptions.PdfException("PDF không có trang.");

                    var text = new System.Text.StringBuilder();
                    for (int page = 1; page <= pages; page++)
                    {
                        job.StoppingToken.ThrowIfCancellationRequested();
                        text.AppendLine(PdfOcrDocument.ReadText(source, page));
                    }

                    var (_, _, _, name) = ReadIdentifiers(text.ToString(), scan, 1);
                    string newName = PdfPageRanges.AvailableName(output, SanitizeFileName(name), ".pdf", firstSuffix: 1);
                    System.IO.File.Copy(inputs[i], Path.Combine(output, newName));
                    log.Add($"{names[i]} → {newName}");
                }
                catch (iText.Kernel.Exceptions.PdfException)
                {
                    log.Add($"{names[i]} → Không đọc được PDF.");
                }
                job.ReportUnits(i + 1, inputs.Length, "Đang đọc Invoice và AWB.");
            }

            System.IO.File.WriteAllLines(Path.Combine(output, "rename_log.txt"), log);
            return job.CreateZip(output, $"Renamed_PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");
        }

        // =========================================================
        // POST: Tach moi trang PDF thanh 1 file rieng
        // =========================================================
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> SplitEachPage(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Vui lòng chọn file PDF."));

            return RunJobAsync([file], (job, inputs) => SplitPages(job, inputs[0]));
        }

        private PdfJobOutput SplitPages(PdfJob job, string path)
        {
            using var source = new PdfDocument(new PdfReader(path));
            using var scan = ocr.Open(path);
            int total = source.GetNumberOfPages();
            if (total == 0)
                throw new PdfJobInputException("File PDF không có trang nào.");

            var outputFolder = Directory.CreateDirectory(Path.Combine(job.Directory, "output")).FullName;
            var rows = new List<(string FileName, string InvoiceNo, string AwbNo, string OriginalInvoiceNo, int PageNo)>();
            job.ReportUnits(0, total, "Đang đọc Invoice và AWB.");

            for (int page = 1; page <= total; page++)
            {
                string text = PdfOcrDocument.ReadText(source, page);
                var (invoice, awb, originalInvoice, name) = ReadIdentifiers(text, scan, page);
                string fileName = PdfPageRanges.WriteChunk(source, page, page, outputFolder, SanitizeFileName(name), firstSuffix: 2);
                rows.Add((fileName, invoice, awb, originalInvoice, page));
                job.ReportUnits(page, total, "Đang đọc Invoice và AWB.");
            }

            job.Report(90, "Đang tạo Mapping.xlsx.");
            BuildExcelEachPage(rows, Path.Combine(outputFolder, "Mapping.xlsx"));
            return job.CreateZip(outputFolder, $"TachTungTrang_{DateTime.Now:yyyyMMddHHmmss}.zip");
        }

        // =========================================================
        // PRIVATE HELPERS
        // =========================================================

        private (string Invoice, string Awb, string OriginalInvoice, string Name) ReadIdentifiers(string text, PdfOcrDocument scan, int page)
        {
            if (string.IsNullOrWhiteSpace(text))
                text = NormalizeOcrText(scan.RecognizePage(page, acceptUpright: HasConfidentPodIdentifiers));

            string invoice = ExtractInvoiceNumber(text);
            string awb = ExtractAwbNumber(text);
            string original = ExtractOriginalInvoiceNumber(text);

            string name = (invoice != "Unknown", awb != "Unknown") switch
            {
                (true, true) => $"{invoice}_{awb}",
                (true, false) => invoice,
                (false, true) => awb,
                _ => "Unknown"
            };
            return (invoice, awb, original, name);
        }

        private bool HasConfidentPodIdentifiers(string text, int confidence)
        {
            const int minimumConfidence = 90;
            if (confidence < minimumConfidence)
                return false;

            text = NormalizeOcrText(text);
            return ExtractInvoiceNumber(text, requireLabel: true) != "Unknown"
                && ExtractAwbNumber(text, requireLabel: true) != "Unknown";
        }

        /// <summary>
        /// Trich xuat "Original invoice #" trong bang Control Sheet (VD: 522602045).
        /// Cot nay nam trong bang co dinh dang:
        ///   Control Sheet
        ///   Original invoice #      522602045
        ///   Packing list #          ...
        /// </summary>
        private string ExtractOriginalInvoiceNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Unknown";

            // Pattern 1: "Original invoice #" ... so ngay sau (cung dong hoac xuong dong,
            // vi khi extract tu bang PDF, nhan va gia tri co the bi tach dong)
            var m = Regex.Match(text,
                @"Original\s*invoice\s*#?\s*:?\s*[\r\n]*\s*([A-Z0-9\-/]{5,})",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string num = CleanupExtractedText(m.Groups[1].Value);
                if (num.Length >= 5) return SanitizeFileName(num);
            }

            return "Unknown";
        }

        /// <summary>
        /// Trich xuat Invoice # (VN-0001071, SLO-0009716-17-18, ITA-0028194, ...)
        /// </summary>
        private string ExtractInvoiceNumber(string text, bool requireLabel = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Unknown";

            // Pattern 1: Invoice # ro rang
            var m = Regex.Match(text,
                @"Invoice\s*#?\s*:?\s*([A-Z]{2,5}-\d[\w\-/]*(?:\s+\d{1,4})?)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string num = CleanupExtractedText(MergeInvoiceTokens(m.Groups[1].Value));
                if (num.Length >= 5) return SanitizeFileName(num);
            }

            if (requireLabel) return "Unknown";

            // Pattern 2: Packing list #
            m = Regex.Match(text, @"Packing\s*[Ll]ist\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string num = CleanupExtractedText(m.Groups[1].Value);
                if (num.Length >= 5) return SanitizeFileName(num);
            }

            // Pattern 3: XXX-XXXXXXX (complex)
            m = Regex.Match(text, @"([A-Z]{2,5}-\d+(?:-\d+)*(?:/[A-Z]{2,5}-\d+(?:-\d+)*)*)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string num = CleanupExtractedText(m.Groups[1].Value);
                if (num.Length >= 7) return SanitizeFileName(num);
            }

            // Pattern 4: XXX-XXXXXX (don gian)
            m = Regex.Match(text, @"([A-Z]{2,4}[-\s]?\d{5,}(?:[-\s]?\d+)*)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string num = CleanupExtractedText(m.Groups[1].Value);
                if (num.Length >= 7) return SanitizeFileName(num);
            }

            return "Unknown";
        }

        /// <summary>
        /// Trich xuat so AWB / B/L no. (VD: 7304864534, 139443362435)
        /// Uu tien tim sau label "AWB" / "B/L", fallback day so >= 8 chu so.
        /// </summary>
        private string ExtractAwbNumber(string text, bool requireLabel = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "Unknown";

            text = text.Replace("\r", "");

            var lines = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .ToList();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].ToUpper();

                if (line.Contains("AWB") || line.Contains("B/L"))
                {
                    for (int j = i + 1; j <= Math.Min(i + 3, lines.Count - 1); j++)
                    {
                        string raw = lines[j].Trim();

                        // chỉ lấy token đầu tiên
                        string candidate =
                            raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                               .FirstOrDefault() ?? "";

                        candidate =
                            CleanupAwb(candidate);

                        if (IsValidAwb(candidate))
                            return SanitizeFileName(candidate);
                    }
                }
            }

            if (requireLabel) return "Unknown";

            // fallback
            var matches = Regex.Matches(
                text,
                @"(?<![A-Z0-9])([A-Z]{2,5}\d{5,}|\d{8,})(?![A-Z0-9])",
                RegexOptions.IgnoreCase);

            foreach (Match m in matches)
            {
                string value =
                    CleanupAwb(m.Groups[1].Value);

                if (IsValidAwb(value))
                    return SanitizeFileName(value);
            }

            return "Unknown";
        }
        private string CleanupAwb(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            value = value.Trim();

            // chỉ giữ A-Z 0-9 -
            value = Regex.Replace(
                value,
                @"[^A-Z0-9\-]",
                "",
                RegexOptions.IgnoreCase);

            return value.ToUpper();
        }
        private bool IsValidAwb(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.ToUpper();

            // quá ngắn
            if (value.Length < 5)
                return false;
            // reject quá dài
            if (value.Length > 50)
                return false;
            // phải có >= 4 số
            if (value.Count(char.IsDigit) < 4)
                return false;
            // phải có ít nhất 1 số
            if (!value.Any(char.IsDigit))
                return false;
            if (Regex.IsMatch(value, @"^CMVN", RegexOptions.IgnoreCase))
                return false;
            // blacklist OCR rác
            string[] blacklist =
            {
                "QUANTITY",
                "DESCRIPTION",
                "SDATALOGIC",
                "INVOICE",
                "PACKING",
                "SHIPPER",
                "CONSIGNEE",
                "TOTAL",
                "GOODS",
                "PRODUCT",
                "ITEM"
            };

            // chứa từ blacklist
            if (blacklist.Any(x => value.Contains(x)))
                return false;

            // toàn chữ => reject
            if (value.All(char.IsLetter))
                return false;

            return true;
        }
        //private string ExtractAwbNumber(string text)
        //{
        //    if (string.IsNullOrWhiteSpace(text)) return "Unknown";

        //    // Pattern 1: Sau label AWB hoac B/L
        //    var m = Regex.Match(text,
        //        @"(?:AWB|A\.W\.B|B/L|BL)\s*[/#\.]?\s*(?:no\.?)?\s*:?\s*(\d{6,})",
        //        RegexOptions.IgnoreCase);
        //    if (m.Success) return m.Groups[1].Value.Trim();

        //    // Pattern 2: Day so dai >= 8 chu so doc lap (khong dinh chu cai/dash)
        //    var matches = Regex.Matches(text, @"(?<![A-Z0-9\-/])(\d{8,})(?![A-Z0-9\-/])", RegexOptions.IgnoreCase);
        //    if (matches.Count > 0) return matches[0].Groups[1].Value.Trim();

        //    return "Unknown";
        //}

        /// <summary>
        /// Normalize cac loi OCR pho bien:
        /// - Gop so bi ngat: "VN-000107 1" -> "VN-0001071"
        /// - Sua chu nham so: O->0, I->1, Z->2, S->5 trong vung digit
        /// </summary>
        private string NormalizeOcrText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Fix 1: Gop token so le bi ngat sau invoice code
            text = Regex.Replace(text,
                @"(Invoice\s*#?\s*:?\s*)([A-Z]{2,5}-\d+)\s+(\d{1,4})(?=\s|$)",
                m => m.Groups[3].Value.Length <= 4
                    ? m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value
                    : m.Value,
                RegexOptions.IgnoreCase);

            // Fix 2: Normalize chu nham thanh so trong vung PREFIX-BODY
            text = Regex.Replace(text,
                @"([A-Z]{2,5}-)([A-Z0-9]{4,})",
                m => m.Groups[1].Value + NormalizeOcrDigits(m.Groups[2].Value),
                RegexOptions.IgnoreCase);

            return text;
        }

        /// <summary>
        /// Doi ki tu OCR hay nham sang so theo context (truoc/sau la so):
        /// O->0 | I->1 | Z->2 | S->5
        /// </summary>
        private string NormalizeOcrDigits(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            var sb = new System.Text.StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                bool prevDig = i > 0 && char.IsDigit(sb[i - 1]);
                bool nextDig = i < input.Length - 1 && char.IsDigit(input[i + 1]);

                sb.Append(char.ToUpper(c) switch
                {
                    'O' when prevDig || nextDig || (i == 0 && nextDig) => '0',
                    'I' when prevDig || nextDig => '1',
                    'Z' when prevDig || nextDig => '2',
                    'S' when prevDig || nextDig => '5',
                    _ => c
                });
            }
            return sb.ToString();
        }

        /// <summary>
        /// Gop so le bi OCR ngat: "VN-000107 1" -> "VN-0001071"
        /// </summary>
        private string MergeInvoiceTokens(string raw)
        {
            var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return raw;

            var sb = new System.Text.StringBuilder(parts[0]);
            for (int i = 1; i < parts.Length; i++)
            {
                if (Regex.IsMatch(parts[i], @"^\d{1,4}$"))
                    sb.Append(parts[i]);
                else
                    break;
            }
            return sb.ToString();
        }

        private string CleanupExtractedText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return "";
            string cleaned = Regex.Replace(rawText, @"\s+", "");
            cleaned = Regex.Replace(cleaned, @"[^A-Z0-9\-/]", "", RegexOptions.IgnoreCase);
            return cleaned.Trim('-', '/').ToUpper();
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "Unknown";
            var invalid = Path.GetInvalidFileNameChars();
            string safe = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return safe[..Math.Min(safe.Length, 150)];
        }

        private void BuildExcelEachPage(List<(string FileName, string InvoiceNo, string AwbNo, string OriginalInvoiceNo, int PageNo)> rows, string outputPath)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            ws.Cell(1, 1).Value = "Tên file cũ";
            ws.Cell(1, 2).Value = "Invoice #";
            ws.Cell(1, 3).Value = "AWB / B/L no.";
            ws.Cell(1, 4).Value = "Original invoice #";
            ws.Cell(1, 5).Value = "Số trang gốc";
            ws.Cell(1, 6).Value = "Tên mới";

            var h = ws.Row(1);
            h.Style.Font.Bold = true;
            h.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            h.Style.Font.FontColor = XLColor.White;

            for (int i = 0; i < rows.Count; i++)
            {
                ws.Cell(i + 2, 1).Value = rows[i].FileName;
                ws.Cell(i + 2, 2).Value = rows[i].InvoiceNo;
                ws.Cell(i + 2, 3).Value = rows[i].AwbNo;
                ws.Cell(i + 2, 4).Value = rows[i].OriginalInvoiceNo;
                ws.Cell(i + 2, 5).Value = rows[i].PageNo;
                // để trống cho user tự nhập
                ws.Cell(i + 2, 6).Value = "";
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(outputPath);
        }

        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public Task<IActionResult> RenameByExcel(IFormFile zipFile, IFormFile excelFile)
        {
            if (zipFile == null || zipFile.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Thiếu file ZIP"));

            if (excelFile == null || excelFile.Length == 0)
                return Task.FromResult<IActionResult>(BadRequest("Thiếu file Excel mapping"));

            return RunJobAsync([zipFile, excelFile], (job, inputs) => Rename(job, inputs[0], inputs[1]));
        }

        private PdfJobOutput Rename(PdfJob job, string zipPath, string excelPath)
        {
            string tempFolder = job.Directory;
            var extractFolder = Path.Combine(tempFolder, "extract");
            Directory.CreateDirectory(extractFolder);

            job.ExtractZip(zipPath, extractFolder);

            var mappings = new List<(string OldName, string NewName)>();

            using (var excel = System.IO.File.OpenRead(excelPath))
            using (var wb = new XLWorkbook(excel))
            {
                var ws = wb.Worksheet(1);

                int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

                for (int row = 2; row <= lastRow; row++)
                {
                    string oldName = ws.Cell(row, 1).GetString().Trim();
                    string newName = ws.Cell(row, 6).GetString().Trim();

                    if (string.IsNullOrWhiteSpace(oldName))
                        continue;

                    if (string.IsNullOrWhiteSpace(newName))
                        continue;

                    mappings.Add((oldName, newName));
                }
            }
            job.ReportUnits(0, mappings.Count, "Đang đổi tên tệp.", 20, 90);
            int renamed = 0;
            foreach (var map in mappings)
            {
                job.ReportUnits(renamed++, mappings.Count, "Đang đổi tên tệp.", 20, 90);
                string? oldPath = Directory
                    .GetFiles(extractFolder, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(x =>
                        Path.GetFileName(x)
                        .Equals(map.OldName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(oldPath))
                    continue;

                string folder = Path.GetDirectoryName(oldPath)!;
                string ext = Path.GetExtension(oldPath);

                string safeNewName = SanitizeFileName(map.NewName);

                if (!safeNewName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    safeNewName += ext;

                string target = PdfPageRanges.AvailableName(folder, Path.GetFileNameWithoutExtension(safeNewName),
                    Path.GetExtension(safeNewName), firstSuffix: 2);
                System.IO.File.Move(oldPath, Path.Combine(folder, target));
            }

            job.ReportUnits(mappings.Count, mappings.Count, "Đang đổi tên tệp.", 20, 90);
            return job.CreateZip(extractFolder, $"Renamed_{DateTime.Now:yyyyMMddHHmmss}.zip");
        }

    }
}
