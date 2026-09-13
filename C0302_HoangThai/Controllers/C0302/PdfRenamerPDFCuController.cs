using ClosedXML.Excel;
using Docnet.Core;
using Docnet.Core.Models;
using DocumentFormat.OpenXml.InkML;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Utils;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Tesseract;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamerPDFCuController : Controller
    {
        [HttpGet]
        public IActionResult Upload() => View();

        // =========================================================
        // POST: Doi ten nhieu file PDF / ZIP
        // =========================================================
        [HttpPost]
        public IActionResult ProcessFiles(List<IFormFile> pdfFiles, IFormFile zipFile)
        {
            var debugLogs = new List<string>();
            var fileInfos = new List<RenamedFileInfo>();
            try
            {
                var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempFolder);
                var extractedFolder = Path.Combine(tempFolder, "extracted");
                Directory.CreateDirectory(extractedFolder);

                if (zipFile != null && zipFile.Length > 0)
                {
                    var zipPath = Path.Combine(tempFolder, zipFile.FileName);
                    using (var stream = new FileStream(zipPath, FileMode.Create))
                        zipFile.CopyTo(stream);
                    ZipFile.ExtractToDirectory(zipPath, extractedFolder);

                    foreach (var pdfPath in Directory.GetFiles(extractedFolder, "*.pdf", SearchOption.AllDirectories))
                        ProcessSinglePdf(pdfPath, tempFolder, fileInfos, debugLogs);
                }
                else if (pdfFiles != null && pdfFiles.Count > 0)
                {
                    foreach (var pdfFile in pdfFiles)
                    {
                        if (pdfFile.Length == 0 || !pdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var tempPdfPath = Path.Combine(extractedFolder, pdfFile.FileName);
                        using (var stream = new FileStream(tempPdfPath, FileMode.Create))
                            pdfFile.CopyTo(stream);
                        ProcessSinglePdf(tempPdfPath, tempFolder, fileInfos, debugLogs);
                    }
                }
                else
                {
                    ViewBag.Error = "Vui long chon file PDF hoac file ZIP";
                    return View("Upload");
                }

                System.IO.File.WriteAllLines(Path.Combine(tempFolder, "rename_log.txt"), debugLogs);
                var outputZipPath = Path.Combine(Path.GetTempPath(), $"Renamed_PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");
                ZipFile.CreateFromDirectory(tempFolder, outputZipPath);
                try { Directory.Delete(extractedFolder, true); } catch { }

                ViewBag.Success = $"Da doi ten thanh cong {fileInfos.Count} file PDF!";
                ViewBag.Files = fileInfos;
                ViewBag.ZipPath = outputZipPath;
                ViewBag.TotalFiles = fileInfos.Count;
                ViewBag.DebugLogs = debugLogs;
                return View("Upload");
            }
            catch (Exception ex)
            {
                ViewBag.Error = $"Loi xu ly: {ex.Message}\n{ex.StackTrace}";
                ViewBag.DebugLogs = debugLogs;
                return View("Upload");
            }
        }

        // =========================================================
        // POST: Tach moi trang PDF thanh 1 file rieng
        // =========================================================
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult SplitEachPage(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File khong hop le.");

            string tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            try
            {
                string pdfPath = Path.Combine(tempFolder, file.FileName);
                using (var fs = new FileStream(pdfPath, FileMode.Create))
                    file.CopyTo(fs);

                int totalPages;
                using (var reader = new PdfReader(pdfPath))
                using (var pdfDoc = new PdfDocument(reader))
                    totalPages = pdfDoc.GetNumberOfPages();

                string outFolder = Path.Combine(tempFolder, "output");
                Directory.CreateDirectory(outFolder);
                var excelRows = new List<(string FileName, string InvoiceNo, string AwbNo, string OriginalInvoiceNo, int PageNo)>();

                using (var reader = new PdfReader(pdfPath))
                using (var srcDoc = new PdfDocument(reader))
                {
                    for (int pageNo = 1; pageNo <= totalPages; pageNo++)
                    {
                        string subPath = Path.Combine(tempFolder, $"page_{pageNo}.pdf");
                        using (var writer = new PdfWriter(subPath))
                        using (var subDoc = new PdfDocument(writer))
                            new PdfMerger(subDoc).Merge(srcDoc, pageNo, pageNo);

                        var (invoiceNo, awbNo, originalInvoiceNo, fileName) = ExtractFileNameFromPdf(subPath);

                        Console.WriteLine($"[SplitEachPage] Trang {pageNo} | Invoice=[{invoiceNo}] | AWB=[{awbNo}] | OriginalInvoice=[{originalInvoiceNo}] | File=[{fileName}]");

                        string destName = $"{SanitizeFileName(fileName)}.pdf";
                        string destPath = Path.Combine(outFolder, destName);
                        int suffix = 2;
                        while (System.IO.File.Exists(destPath))
                        {
                            destName = $"{SanitizeFileName(fileName)}_{suffix++}.pdf";
                            destPath = Path.Combine(outFolder, destName);
                        }

                        System.IO.File.Copy(subPath, destPath);
                        excelRows.Add((destName, invoiceNo, awbNo, originalInvoiceNo, pageNo));
                    }
                }

                string excelPath = Path.Combine(outFolder, "Mapping.xlsx");
                BuildExcelEachPage(excelRows, excelPath);

                string zipPath = Path.Combine(tempFolder, $"TachTungTrang_{DateTime.Now:yyyyMMddHHmmss}.zip");
                ZipFile.CreateFromDirectory(outFolder, zipPath);
                return PhysicalFile(zipPath, "application/zip", Path.GetFileName(zipPath));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        // =========================================================
        // PRIVATE HELPERS
        // =========================================================

        private void ProcessSinglePdf(string inputPath, string outputFolder, List<RenamedFileInfo> fileInfos, List<string> debugLogs)
        {
            try
            {
                string originalName = Path.GetFileName(inputPath);
                var (_, _, originalInvoiceNo, fileName) = ExtractFileNameFromPdf(inputPath);

                string outputPath = Path.Combine(outputFolder, $"{fileName}.pdf");
                int counter = 1;
                while (System.IO.File.Exists(outputPath))
                    outputPath = Path.Combine(outputFolder, $"{fileName}_{counter++}.pdf");

                System.IO.File.Copy(inputPath, outputPath, true);
                fileInfos.Add(new RenamedFileInfo
                {
                    OriginalName = originalName,
                    NewName = Path.GetFileName(outputPath),
                    FilePath = outputPath,
                    ExtractedCode = fileName,
                    OriginalInvoiceNo = originalInvoiceNo
                });
                debugLogs.Add($"{originalName} -> {Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                debugLogs.Add($"Loi xu ly {Path.GetFileName(inputPath)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Tra ve (invoiceNo, awbNo, tenFile).
        /// tenFile = "INVOICE_AWB" neu co ca 2, hoac chi "INVOICE", hoac "Unknown".
        /// verbose=true: in chi tiet OCR ra console khi can debug.
        /// </summary>
        private (string invoiceNo, string awbNo, string originalInvoiceNo, string fileName) ExtractFileNameFromPdf(string pdfPath, bool verbose = false)
        {
            try
            {
                string text = "";

                // Buoc 1: Thu doc text layer (gop toan bo cac trang, vi "Original invoice #"
                // thuong nam o trang Control Sheet - co the la trang 2 tro di)
                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader))
                {
                    int pageCount = pdfDoc.GetNumberOfPages();
                    var sbText = new System.Text.StringBuilder();
                    for (int p = 1; p <= pageCount; p++)
                        sbText.AppendLine(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(p), new SimpleTextExtractionStrategy()));
                    text = sbText.ToString();
                }

                // Buoc 2: Fallback OCR neu text layer trong
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = PerformOCR(pdfPath, verbose);
                    text = NormalizeOcrText(text);
                }

                string invoiceNo = ExtractInvoiceNumber(text);
                string awbNo = ExtractAwbNumber(text);
                string originalInvoiceNo = ExtractOriginalInvoiceNumber(text);

                // Ten file: "SLO-0009716-17-18_7304864534"
                string fileName = (invoiceNo != "Unknown", awbNo != "Unknown") switch
                {
                    (true, true) => $"{invoiceNo}_{awbNo}",
                    (true, false) => invoiceNo,
                    (false, true) => awbNo,
                    _ => "Unknown"
                };

                return (invoiceNo, awbNo, originalInvoiceNo, fileName);
            }
            catch (Exception ex)
            {
                if (verbose) Console.WriteLine($"  [ERR] Doc PDF: {ex.Message}");
                return ("Unknown", "Unknown", "Unknown", "Unknown");
            }
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
        private string ExtractInvoiceNumber(string text)
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
        private string ExtractAwbNumber(string text)
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

        /// <summary>
        /// OCR trang dau tien cua PDF bang Tesseract.
        /// verbose=true: in chi tiet ra console de debug.
        /// </summary>
        private string PerformOCR(string pdfPath, bool verbose = false)
        {
            try
            {
                using var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2160, 3840));
                using var pageReader = docReader.GetPageReader(0);
                var rawBytes = pageReader.GetImage();
                int width = pageReader.GetPageWidth(), height = pageReader.GetPageHeight();

                var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
                if (!System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
                {
                    if (verbose) Console.WriteLine("  [ERR] Thieu tessdata/eng.traineddata");
                    return "";
                }

                using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-/#:.,() ");

                string bestText = "";
                float bestConf = 0;

                foreach (var angle in new[] { 0, 90, 180, 270 })
                {
                    using var img = Image.LoadPixelData<Rgba32>(rawBytes, width, height);
                    img.Mutate(x =>
                    {
                        if (angle > 0) x.Rotate(angle);
                        x.Grayscale().Contrast(1.2f);
                    });
                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms); ms.Position = 0;
                    using var pix = Pix.LoadFromMemory(ms.ToArray());
                    using var page = engine.Process(pix);
                    if (page.GetMeanConfidence() > bestConf)
                    {
                        bestConf = page.GetMeanConfidence();
                        bestText = page.GetText();
                    }
                }

                // Mo cmt de debug OCR neu can:
                // if (verbose) Console.WriteLine($"  OCR conf={bestConf:F2} | text={bestText[..Math.Min(200,bestText.Length)]}");

                return bestText ?? "";
            }
            catch (Exception ex)
            {
                if (verbose) Console.WriteLine($"  [ERR] OCR: {ex.Message}");
                return "";
            }
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

        [HttpGet]
        public IActionResult DownloadZip(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return NotFound("File khong ton tai hoac da bi xoa.");
            var bytes = System.IO.File.ReadAllBytes(filePath);
            try { System.IO.File.Delete(filePath); } catch { }
            return File(bytes, "application/zip", Path.GetFileName(filePath));
        }
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult RenameByExcel(IFormFile zipFile, IFormFile excelFile)
        {
            if (zipFile == null || zipFile.Length == 0)
                return BadRequest("Thiếu file ZIP");

            if (excelFile == null || excelFile.Length == 0)
                return BadRequest("Thiếu file Excel mapping");

            string tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            string extractFolder = Path.Combine(tempFolder, "extract");
            Directory.CreateDirectory(extractFolder);

            try
            {
                // =========================
                // Save ZIP
                // =========================
                string zipPath = Path.Combine(tempFolder, zipFile.FileName);

                using (var fs = new FileStream(zipPath, FileMode.Create))
                    zipFile.CopyTo(fs);

                ZipFile.ExtractToDirectory(zipPath, extractFolder);

                // =========================
                // Save Excel
                // =========================
                string excelPath = Path.Combine(tempFolder, excelFile.FileName);

                using (var fs = new FileStream(excelPath, FileMode.Create))
                    excelFile.CopyTo(fs);
                // =========================
                // Read Mapping
                // =========================
                var mappings = new List<(string OldName, string NewName)>();

                using (var wb = new XLWorkbook(excelPath))
                {
                    var ws = wb.Worksheet(1);

                    int lastRow = ws.LastRowUsed().RowNumber();

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
                // =========================
                // Rename Files
                // =========================
                foreach (var map in mappings)
                {
                    string oldPath = Directory
                        .GetFiles(extractFolder, "*", SearchOption.AllDirectories)
                        .FirstOrDefault(x =>
                            Path.GetFileName(x)
                            .Equals(map.OldName, StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrEmpty(oldPath))
                        continue;

                    string folder = Path.GetDirectoryName(oldPath);
                    string ext = Path.GetExtension(oldPath);

                    string safeNewName = SanitizeFileName(map.NewName);

                    if (!safeNewName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        safeNewName += ext;

                    string newPath = Path.Combine(folder, safeNewName);

                    int counter = 2;

                    while (System.IO.File.Exists(newPath))
                    {
                        string fileNameOnly = Path.GetFileNameWithoutExtension(safeNewName);
                        newPath = Path.Combine(folder, $"{fileNameOnly}_{counter++}{ext}");
                    }

                    System.IO.File.Move(oldPath, newPath);
                }

                // =========================
                // Export ZIP mới
                // =========================
                string outZip = Path.Combine(
                    tempFolder,
                    $"Renamed_{DateTime.Now:yyyyMMddHHmmss}.zip");

                ZipFile.CreateFromDirectory(extractFolder, outZip);

                return PhysicalFile(
                    outZip,
                    "application/zip",
                    Path.GetFileName(outZip));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }


        public class RenamedFileInfo
        {
            public string OriginalName { get; set; }
            public string NewName { get; set; }
            public string FilePath { get; set; }
            public string ExtractedCode { get; set; }
            public string OriginalInvoiceNo { get; set; }
        }
    }
}