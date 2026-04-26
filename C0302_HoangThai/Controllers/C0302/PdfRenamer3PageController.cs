using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Tesseract;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamer3PageController : Controller
    {
        // Prefix hợp lệ theo bạn cung cấp
        private static readonly string[] AllowedPrefixes = new[]
        {
            "SIN","SLO","EUG","AUS","BRA","ITA","VN","VIE","PAN","CON","RMA","UNK","FRA"
        };

        // Regex group: SIN|SLO|...
        private static readonly string AllowedPrefixPattern =
            @"(?:SIN|SLO|EUG|AUS|BRA|ITA|VN|VIE|PAN|CON|RMA|UNK|FRA)";

        // digits tối thiểu để tránh bắt bị cụt (ITA-00277)
        private const int MinDigits = 6;

        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Upload(List<IFormFile> pdfFiles, IFormFile zipFile)
        {
            var debugLogs = new List<string>();
            var fileInfos = new List<PdfFileInfo>();

            try
            {
                var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempFolder);

                var extractedFolder = Path.Combine(tempFolder, "extracted");
                Directory.CreateDirectory(extractedFolder);

                // Xử lý ZIP file nếu có
                if (zipFile != null && zipFile.Length > 0)
                {
                    debugLogs.Add($"=== XỬ LÝ ZIP FILE: {zipFile.FileName} ===");

                    var zipPath = Path.Combine(tempFolder, zipFile.FileName);
                    using (var stream = new FileStream(zipPath, FileMode.Create))
                    {
                        zipFile.CopyTo(stream);
                    }

                    ZipFile.ExtractToDirectory(zipPath, extractedFolder);
                    debugLogs.Add($"✓ Giải nén thành công");

                    var pdfFilesInZip = Directory.GetFiles(extractedFolder, "*.pdf", SearchOption.AllDirectories);
                    debugLogs.Add($"✓ Tìm thấy {pdfFilesInZip.Length} file PDF trong ZIP");

                    foreach (var pdfPath in pdfFilesInZip)
                    {
                        ProcessSinglePdf(pdfPath, tempFolder, fileInfos, debugLogs);
                    }
                }
                // Xử lý multiple PDF files
                else if (pdfFiles != null && pdfFiles.Count > 0)
                {
                    debugLogs.Add($"=== XỬ LÝ {pdfFiles.Count} FILE PDF ===");

                    foreach (var pdfFile in pdfFiles)
                    {
                        if (pdfFile.Length == 0 || !pdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            debugLogs.Add($"⊘ Bỏ qua: {pdfFile.FileName} (không phải PDF)");
                            continue;
                        }

                        var tempPdfPath = Path.Combine(extractedFolder, pdfFile.FileName);
                        using (var stream = new FileStream(tempPdfPath, FileMode.Create))
                        {
                            pdfFile.CopyTo(stream);
                        }

                        ProcessSinglePdf(tempPdfPath, tempFolder, fileInfos, debugLogs);
                    }
                }
                else
                {
                    ViewBag.Error = "Vui lòng chọn file PDF hoặc file ZIP";
                    return View("Upload");
                }

                // Lưu debug log
                var logPath = Path.Combine(tempFolder, "rename_log.txt");
                System.IO.File.WriteAllLines(logPath, debugLogs);

                // Tạo ZIP output
                var outputZipPath = Path.Combine(Path.GetTempPath(), $"Renamed_PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");
                ZipFile.CreateFromDirectory(tempFolder, outputZipPath);

                try { Directory.Delete(extractedFolder, true); } catch { }

                ViewBag.Success = $"Đã đổi tên thành công {fileInfos.Count} file PDF!";
                ViewBag.Files = fileInfos;
                ViewBag.ZipPath = outputZipPath;
                ViewBag.TotalFiles = fileInfos.Count;
                ViewBag.DebugLogs = debugLogs;

                return View("Upload");
            }
            catch (Exception ex)
            {
                ViewBag.Error = $"Lỗi xử lý: {ex.Message}\n{ex.StackTrace}";
                ViewBag.DebugLogs = debugLogs;
                return View("Upload");
            }
        }

        [HttpGet]
        public IActionResult DownloadZip(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return NotFound();

            var memory = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open))
            {
                stream.CopyTo(memory);
            }
            memory.Position = 0;

            return File(memory, "application/zip", Path.GetFileName(filePath));
        }

        // ── Xử lý 1 file PDF: đọc tên → copy với tên mới (giữ nguyên toàn bộ trang) ──
        private void ProcessSinglePdf(string inputPath, string outputFolder, List<PdfFileInfo> fileInfos, List<string> debugLogs)
        {
            try
            {
                string originalName = Path.GetFileName(inputPath);
                debugLogs.Add($"\n--- Xử lý: {originalName} ---");

                string newFileName = ExtractFileNameFromPdf(inputPath, debugLogs);

                string outputPath = Path.Combine(outputFolder, $"{newFileName}.pdf");
                int counter = 1;
                while (System.IO.File.Exists(outputPath))
                {
                    outputPath = Path.Combine(outputFolder, $"{newFileName}_{counter}.pdf");
                    counter++;
                }

                // Copy nguyên file — chỉ đổi tên, không tách trang
                System.IO.File.Copy(inputPath, outputPath, true);

                fileInfos.Add(new PdfFileInfo
                {
                    OriginalName = originalName,
                    FileName = Path.GetFileName(outputPath),
                    FilePath = outputPath,
                    ExtractedCode = newFileName
                });

                debugLogs.Add($"✓ Đổi tên: {originalName} → {Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                debugLogs.Add($"❌ Lỗi xử lý {Path.GetFileName(inputPath)}: {ex.Message}");
            }
        }

        // ── Đọc trang 1 của PDF → ưu tiên text layer, fallback OCR ──
        private string ExtractFileNameFromPdf(string pdfPath, List<string> debugLogs)
        {
            try
            {
                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader))
                {
                    var page = pdfDoc.GetPage(1);
                    var strategy = new SimpleTextExtractionStrategy();
                    string pageText = PdfTextExtractor.GetTextFromPage(page, strategy);

                    string meaningfulText = Regex.Replace(pageText ?? "", @"[\s\x00-\x1f\x7f]", "");
                    debugLogs.Add($"  Text layer: {(pageText?.Length ?? 0)} raw, {meaningfulText.Length} có nghĩa");

                    if (meaningfulText.Length >= 10)
                    {
                        debugLogs.Add($"  ✓ Đọc được text layer");
                        debugLogs.Add($"  Text (500 ký tự đầu): {pageText.Substring(0, Math.Min(500, pageText.Length))}");

                        string fileName = ExtractDocumentNumber(pageText, debugLogs);
                        if (!fileName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                            return fileName;
                    }
                }

                debugLogs.Add($"  ⟳ Sử dụng OCR...");
                string ocrText = PerformOCR(pdfPath, debugLogs);

                if (!string.IsNullOrWhiteSpace(ocrText))
                    return ExtractDocumentNumber(ocrText, debugLogs);

                return "Unknown";
            }
            catch (Exception ex)
            {
                debugLogs.Add($"  ❌ Lỗi đọc PDF: {ex.Message}");
                return "Unknown";
            }
        }

        // OCR nhiều vùng (đỡ bị lệch mẫu scan) rồi ghép text lại
        private string PerformOCR(string pdfPath, List<string> debugLogs)
        {
            try
            {
                // Render ở ~400 DPI để Tesseract đọc chính xác hơn
                using (var library = DocLib.Instance)
                using (var docReader = library.GetDocReader(pdfPath, new PageDimensions(3307, 4677))) // ~400dpi A4
                using (var pageReader = docReader.GetPageReader(0))
                {
                    var width = pageReader.GetPageWidth();
                    var height = pageReader.GetPageHeight();
                    var rawBytes = pageReader.GetImage();

                    debugLogs.Add($"  ✓ Render {width}x{height}");

                    // OCR 3 vùng: (45-65), (60-80), (75-100)
                    var regions = new List<(float y0, float y1, string name)>
                    {
                        (0.45f, 0.65f, "mid1"),
                        (0.60f, 0.80f, "mid2"),
                        (0.75f, 1.00f, "bottom"),
                    };

                    var texts = new List<string>();

                    foreach (var r in regions)
                    {
                        int cropStartY = (int)(height * r.y0);
                        int cropEndY = (int)(height * r.y1);
                        if (cropEndY <= cropStartY) cropEndY = cropStartY + 1;
                        int cropH = cropEndY - cropStartY;

                        debugLogs.Add($"  ✓ Crop({r.name}) Y={cropStartY}→{cropEndY} (H={cropH})");

                        byte[] pngBytes;
                        using (var fullImg = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(rawBytes, width, height))
                        {
                            fullImg.Mutate(ctx => ctx
                                .Crop(new SixLabors.ImageSharp.Rectangle(0, cropStartY, width, cropH))
                                .Resize(width * 2, cropH * 2)
                                .Grayscale()
                                .BinaryThreshold(0.60f)); // ~153/255

                            using (var ms = new MemoryStream())
                            {
                                fullImg.SaveAsPng(ms);
                                pngBytes = ms.ToArray();
                            }
                        }

                        var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
                        if (!Directory.Exists(tessDataPath) ||
                            !System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
                        {
                            debugLogs.Add($"  ❌ Thiếu tessdata/eng.traineddata");
                            return "";
                        }

                        using (var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default))
                        using (var pix = Pix.LoadFromMemory(pngBytes))
                        using (var pg = engine.Process(pix, PageSegMode.Auto))
                        {
                            string text = pg.GetText() ?? "";
                            float conf = pg.GetMeanConfidence();
                            debugLogs.Add($"  OCR({r.name}) confidence: {conf:F2}");

                            if (!string.IsNullOrWhiteSpace(text))
                                debugLogs.Add($"  📄 OCR({r.name}) preview: {text.Replace("\n", " ")[..Math.Min(220, text.Length)]}");

                            texts.Add(text);
                        }
                    }

                    return string.Join("\n\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
                }
            }
            catch (Exception ex)
            {
                debugLogs.Add($"  ❌ OCR Error: {ex.Message}");
                if (ex.InnerException != null)
                    debugLogs.Add($"     Inner: {ex.InnerException.Message}");
                return "";
            }
        }

        private string NormalizeOcrText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string t = text.ToUpperInvariant();

            // OCR hay chèn dấu nháy: I'TA, I'I'A...
            t = Regex.Replace(t, @"[\'`\u2019\u2018]", "");

            // OCR hay chèn nhiều dấu gạch: ITA--0027..., ITA - - 0027...
            t = Regex.Replace(t, @"\s*-\s*", "-");
            t = Regex.Replace(t, @"-+", "-");

            // gom nhiều space
            t = Regex.Replace(t, @"\s+", " ");

            return t;
        }

        private bool IsValidAllowedCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;

            var m = Regex.Match(code.Trim().ToUpperInvariant(),
                $@"^({AllowedPrefixPattern})-(\d{{{MinDigits},10}}(?:-\d+)*)$",
                RegexOptions.IgnoreCase);

            return m.Success;
        }

        private string ExtractDocumentNumber(string text, List<string> debugLogs)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "Unknown";

            string norm = NormalizeOcrText(text);

            // ===== ƯU TIÊN 1: Số quản lý của nội bộ doanh nghiệp (có nhãn + prefix whitelist) =====
            var soQuanLyMatch = Regex.Match(norm,
                $@"S[o06ốô]\w*\s+qu\w*\s+l[yý]\w*\s+c\w+\s+n\w+\s+b\w+\s+doanh\s+nghi\w+\s*[:\-]?\s*(({AllowedPrefixPattern})[\s\-]\d{{{MinDigits},10}})",
                RegexOptions.IgnoreCase);

            if (soQuanLyMatch.Success)
            {
                string raw = soQuanLyMatch.Groups[1].Value;
                string cleaned = CleanOcrCode(raw);
                debugLogs.Add($"  Raw label code: '{raw}' → Cleaned: '{cleaned}'");
                if (IsValidAllowedCode(cleaned))
                {
                    debugLogs.Add($"  ✓ Số quản lý nội bộ: {cleaned}");
                    return SanitizeFileName(cleaned);
                }
            }

            // ===== ƯU TIÊN 2: Generic PREFIX-NUMBER (prefix whitelist) ở bất kỳ đâu =====
            var genericMatch = Regex.Match(norm,
                $@"\b(({AllowedPrefixPattern})-\d{{{MinDigits},10}}(?:-\d+)*)\b",
                RegexOptions.IgnoreCase);

            if (genericMatch.Success)
            {
                string raw = genericMatch.Groups[1].Value;
                string cleaned = CleanOcrCode(raw);
                debugLogs.Add($"  ✓ Generic allowed code: '{raw}' → '{cleaned}'");
                if (IsValidAllowedCode(cleaned))
                    return SanitizeFileName(cleaned.ToUpperInvariant());
            }

            // ===== ƯU TIÊN 3: Fallback theo nhãn nhưng mất prefix -> UNK-<digits> =====
            var soQuanLyNumberMatch = Regex.Match(norm,
                $@"S[o06ô]\w*\s+qu\w*\s+l\w+\s+c\w+\s+n\w+\s+b\w+\s+doanh\s+nghi\w+\s*[:\-]?\s*(\d{{{MinDigits},10}})",
                RegexOptions.IgnoreCase);

            if (soQuanLyNumberMatch.Success)
            {
                string digits = soQuanLyNumberMatch.Groups[1].Value.Trim();
                string fallback = $"UNK-{digits}";
                debugLogs.Add($"  ✓ Số quản lý nội bộ (fallback digits): {fallback}");
                return SanitizeFileName(fallback);
            }

            // ===== ƯU TIÊN 4: Invoice =====
            var invoiceMatch = Regex.Match(norm, @"Invoice\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (invoiceMatch.Success && invoiceMatch.Groups[1].Value.Length >= 5)
            {
                string num = invoiceMatch.Groups[1].Value.Trim();
                debugLogs.Add($"  ✓ Invoice: {num}");
                return SanitizeFileName(num);
            }

            // ===== ƯU TIÊN 5: Packing list =====
            var packingMatch = Regex.Match(norm, @"Packing\s*list\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (packingMatch.Success && packingMatch.Groups[1].Value.Length >= 5)
            {
                string num = packingMatch.Groups[1].Value.Trim();
                debugLogs.Add($"  ✓ Packing: {num}");
                return SanitizeFileName(num);
            }

            debugLogs.Add($"  ✗ Không tìm thấy mã hợp lệ");
            return "Unknown";
        }

        // Làm sạch mã bị OCR nhận sai ký tự.
        private string CleanOcrCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string s = raw.Trim().ToUpperInvariant();

            // Dấu nháy đơn/backtick giữa 2 chữ cái -> T  ("I''A" -> "ITA")
            s = Regex.Replace(s, @"(?<=[A-Z])[\'`\u2019\u2018]{1,2}(?=[A-Z])", "T");

            // l/I/| giữa 2 chữ số -> 1
            s = Regex.Replace(s, @"(?<=\d)[lI|](?=\d)", "1");

            // Chuẩn hóa dấu gạch nối
            s = Regex.Replace(s, @"\s*-\s*", "-");
            s = Regex.Replace(s, @"-+", "-");
            s = Regex.Replace(s, @"\s+", " ");

            // Nếu OCR ra "ITA 0027648" => "ITA-0027648" (áp dụng mọi prefix)
            s = Regex.Replace(s, $@"\b({AllowedPrefixPattern})\s+(\d{{{MinDigits},10}})\b", "$1-$2", RegexOptions.IgnoreCase);

            // Chỉ giữ A-Z, 0-9, '-', khoảng trắng (rồi bỏ space)
            s = Regex.Replace(s, @"[^A-Z0-9\-\s]", "");
            s = Regex.Replace(s, @"\s+", "");
            s = s.Trim('-');

            // Fix riêng: OCR hay nhầm IIA/IA -> ITA (vẫn nằm trong whitelist)
            s = Regex.Replace(s, @"^(IIA|I1A|IA)-", "ITA-", RegexOptions.IgnoreCase);

            // Nếu thiếu dấu - mà có dạng PREFIX+SỐ -> thêm dấu -
            if (!s.Contains('-') && Regex.IsMatch(s, $@"^({AllowedPrefixPattern})\d{{{MinDigits},10}}$", RegexOptions.IgnoreCase))
            {
                var pm = Regex.Match(s, $@"^({AllowedPrefixPattern})(\d{{{MinDigits},10}})$", RegexOptions.IgnoreCase);
                if (pm.Success)
                    s = pm.Groups[1].Value.ToUpperInvariant() + "-" + pm.Groups[2].Value;
            }

            // Rút gọn chữ số bị lặp do OCR: "777" -> "77"
            var dupMatch = Regex.Match(s, @"^([A-Z]+-)(0*)(\d+)$");
            if (dupMatch.Success)
            {
                string pfx = dupMatch.Groups[1].Value;
                string zeros = dupMatch.Groups[2].Value;
                string digits = dupMatch.Groups[3].Value;
                string fixedDigits = Regex.Replace(digits, @"(.)\1{2,}", m => new string(m.Groups[1].Value[0], 2));
                if (fixedDigits != digits)
                    s = pfx + zeros + fixedDigits;
            }

            return s;
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "Unknown";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string safe = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return safe.Substring(0, Math.Min(safe.Length, 150));
        }
    }

    public class PdfFileInfo
    {
        public string OriginalName { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public int PageNumber { get; set; }
        public string ExtractedCode { get; set; }
        public string InvoiceNumber => ExtractedCode; // giữ tương thích view cũ
    }
}



//using Microsoft.AspNetCore.Http;
//using Microsoft.AspNetCore.Mvc;
//using iText.Kernel.Pdf;
//using iText.Kernel.Pdf.Canvas.Parser;
//using iText.Kernel.Pdf.Canvas.Parser.Listener;
//using Tesseract;
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.IO.Compression;
//using System.Linq;
//using System.Text.RegularExpressions;
//using Docnet.Core;
//using Docnet.Core.Models;
//using SixLabors.ImageSharp;
//using SixLabors.ImageSharp.PixelFormats;
//using SixLabors.ImageSharp.Processing;

//namespace C0302_HoangThai.Controllers.C0302
//{
//    public class PdfRenamer3PageController : Controller
//    {
//        [HttpGet]
//        public IActionResult Upload()
//        {
//            return View();
//        }

//        [HttpPost]
//        public IActionResult Upload(List<IFormFile> pdfFiles, IFormFile zipFile)
//        {
//            var debugLogs = new List<string>();
//            var fileInfos = new List<PdfFileInfo>();

//            try
//            {
//                var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
//                Directory.CreateDirectory(tempFolder);

//                var extractedFolder = Path.Combine(tempFolder, "extracted");
//                Directory.CreateDirectory(extractedFolder);

//                // Xử lý ZIP file nếu có
//                if (zipFile != null && zipFile.Length > 0)
//                {
//                    debugLogs.Add($"=== XỬ LÝ ZIP FILE: {zipFile.FileName} ===");

//                    var zipPath = Path.Combine(tempFolder, zipFile.FileName);
//                    using (var stream = new FileStream(zipPath, FileMode.Create))
//                    {
//                        zipFile.CopyTo(stream);
//                    }

//                    ZipFile.ExtractToDirectory(zipPath, extractedFolder);
//                    debugLogs.Add($"✓ Giải nén thành công");

//                    var pdfFilesInZip = Directory.GetFiles(extractedFolder, "*.pdf", SearchOption.AllDirectories);
//                    debugLogs.Add($"✓ Tìm thấy {pdfFilesInZip.Length} file PDF trong ZIP");

//                    foreach (var pdfPath in pdfFilesInZip)
//                    {
//                        ProcessSinglePdf(pdfPath, tempFolder, fileInfos, debugLogs);
//                    }
//                }
//                // Xử lý multiple PDF files
//                else if (pdfFiles != null && pdfFiles.Count > 0)
//                {
//                    debugLogs.Add($"=== XỬ LÝ {pdfFiles.Count} FILE PDF ===");

//                    foreach (var pdfFile in pdfFiles)
//                    {
//                        if (pdfFile.Length == 0 || !pdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
//                        {
//                            debugLogs.Add($"⊘ Bỏ qua: {pdfFile.FileName} (không phải PDF)");
//                            continue;
//                        }

//                        var tempPdfPath = Path.Combine(extractedFolder, pdfFile.FileName);
//                        using (var stream = new FileStream(tempPdfPath, FileMode.Create))
//                        {
//                            pdfFile.CopyTo(stream);
//                        }

//                        ProcessSinglePdf(tempPdfPath, tempFolder, fileInfos, debugLogs);
//                    }
//                }
//                else
//                {
//                    ViewBag.Error = "Vui lòng chọn file PDF hoặc file ZIP";
//                    return View("Upload");
//                }

//                // Lưu debug log
//                var logPath = Path.Combine(tempFolder, "rename_log.txt");
//                System.IO.File.WriteAllLines(logPath, debugLogs);

//                // Tạo ZIP output
//                var outputZipPath = Path.Combine(Path.GetTempPath(), $"Renamed_PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");
//                ZipFile.CreateFromDirectory(tempFolder, outputZipPath);

//                try { Directory.Delete(extractedFolder, true); } catch { }

//                ViewBag.Success = $"Đã đổi tên thành công {fileInfos.Count} file PDF!";
//                ViewBag.Files = fileInfos;
//                ViewBag.ZipPath = outputZipPath;
//                ViewBag.TotalFiles = fileInfos.Count;
//                ViewBag.DebugLogs = debugLogs;

//                return View("Upload");
//            }
//            catch (Exception ex)
//            {
//                ViewBag.Error = $"Lỗi xử lý: {ex.Message}\n{ex.StackTrace}";
//                ViewBag.DebugLogs = debugLogs;
//                return View("Upload");
//            }
//        }

//        [HttpGet]
//        public IActionResult DownloadZip(string filePath)
//        {
//            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
//                return NotFound();

//            var memory = new MemoryStream();
//            using (var stream = new FileStream(filePath, FileMode.Open))
//            {
//                stream.CopyTo(memory);
//            }
//            memory.Position = 0;

//            return File(memory, "application/zip", Path.GetFileName(filePath));
//        }

//        // ── Xử lý 1 file PDF: đọc tên → copy với tên mới (giữ nguyên toàn bộ trang) ──
//        private void ProcessSinglePdf(string inputPath, string outputFolder, List<PdfFileInfo> fileInfos, List<string> debugLogs)
//        {
//            try
//            {
//                string originalName = Path.GetFileName(inputPath);
//                debugLogs.Add($"\n--- Xử lý: {originalName} ---");

//                string newFileName = ExtractFileNameFromPdf(inputPath, debugLogs);

//                string outputPath = Path.Combine(outputFolder, $"{newFileName}.pdf");
//                int counter = 1;
//                while (System.IO.File.Exists(outputPath))
//                {
//                    outputPath = Path.Combine(outputFolder, $"{newFileName}_{counter}.pdf");
//                    counter++;
//                }

//                // Copy nguyên file — chỉ đổi tên, không tách trang
//                System.IO.File.Copy(inputPath, outputPath, true);

//                fileInfos.Add(new PdfFileInfo
//                {
//                    OriginalName = originalName,
//                    FileName = Path.GetFileName(outputPath),
//                    FilePath = outputPath,
//                    ExtractedCode = newFileName
//                });

//                debugLogs.Add($"✓ Đổi tên: {originalName} → {Path.GetFileName(outputPath)}");
//            }
//            catch (Exception ex)
//            {
//                debugLogs.Add($"❌ Lỗi xử lý {Path.GetFileName(inputPath)}: {ex.Message}");
//            }
//        }

//        // ── Đọc trang 1 của PDF → ưu tiên text layer, fallback OCR ──
//        private string ExtractFileNameFromPdf(string pdfPath, List<string> debugLogs)
//        {
//            try
//            {
//                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
//                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader))
//                {
//                    var page = pdfDoc.GetPage(1);
//                    var strategy = new SimpleTextExtractionStrategy();
//                    string pageText = PdfTextExtractor.GetTextFromPage(page, strategy);

//                    string meaningfulText = Regex.Replace(pageText ?? "", @"[\s\x00-\x1f\x7f]", "");
//                    debugLogs.Add($"  Text layer: {(pageText?.Length ?? 0)} raw, {meaningfulText.Length} có nghĩa");

//                    if (meaningfulText.Length >= 10)
//                    {
//                        debugLogs.Add($"  ✓ Đọc được text layer");
//                        debugLogs.Add($"  Text (500 ký tự đầu): {pageText.Substring(0, Math.Min(500, pageText.Length))}");

//                        string fileName = ExtractDocumentNumber(pageText, debugLogs);
//                        if (!fileName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
//                            return fileName;
//                    }
//                }

//                debugLogs.Add($"  ⟳ Sử dụng OCR...");
//                string ocrText = PerformOCR(pdfPath, debugLogs);

//                if (!string.IsNullOrWhiteSpace(ocrText))
//                    return ExtractDocumentNumber(ocrText, debugLogs);

//                return "Unknown";
//            }
//            catch (Exception ex)
//            {
//                debugLogs.Add($"  ❌ Lỗi đọc PDF: {ex.Message}");
//                return "Unknown";
//            }
//        }

//        // OCR nhiều vùng (đỡ bị lệch mẫu scan) rồi ghép text lại
//        private string PerformOCR(string pdfPath, List<string> debugLogs)
//        {
//            try
//            {
//                // Render ở ~400 DPI để Tesseract đọc chính xác hơn
//                using (var library = DocLib.Instance)
//                using (var docReader = library.GetDocReader(pdfPath, new PageDimensions(3307, 4677))) // ~400dpi A4
//                using (var pageReader = docReader.GetPageReader(0))
//                {
//                    var width = pageReader.GetPageWidth();
//                    var height = pageReader.GetPageHeight();
//                    var rawBytes = pageReader.GetImage();

//                    debugLogs.Add($"  ✓ Render {width}x{height}");

//                    // OCR 3 vùng: (45-65), (60-80), (75-100)
//                    var regions = new List<(float y0, float y1, string name)>
//                    {
//                        (0.45f, 0.65f, "mid1"),
//                        (0.60f, 0.80f, "mid2"),
//                        (0.75f, 1.00f, "bottom"),
//                    };

//                    var texts = new List<string>();

//                    foreach (var r in regions)
//                    {
//                        int cropStartY = (int)(height * r.y0);
//                        int cropEndY = (int)(height * r.y1);
//                        if (cropEndY <= cropStartY) cropEndY = cropStartY + 1;
//                        int cropH = cropEndY - cropStartY;

//                        debugLogs.Add($"  ✓ Crop({r.name}) Y={cropStartY}→{cropEndY} (H={cropH})");

//                        byte[] pngBytes;
//                        using (var fullImg = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(rawBytes, width, height))
//                        {
//                            fullImg.Mutate(ctx => ctx
//                                .Crop(new SixLabors.ImageSharp.Rectangle(0, cropStartY, width, cropH))
//                                .Resize(width * 2, cropH * 2)
//                                .Grayscale()
//                                .BinaryThreshold(0.60f)); // ~153/255

//                            using (var ms = new MemoryStream())
//                            {
//                                fullImg.SaveAsPng(ms);
//                                pngBytes = ms.ToArray();
//                            }
//                        }

//                        var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
//                        if (!Directory.Exists(tessDataPath) ||
//                            !System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
//                        {
//                            debugLogs.Add($"  ❌ Thiếu tessdata/eng.traineddata");
//                            return "";
//                        }

//                        using (var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default))
//                        using (var pix = Pix.LoadFromMemory(pngBytes))
//                        using (var pg = engine.Process(pix, PageSegMode.Auto))
//                        {
//                            string text = pg.GetText() ?? "";
//                            float conf = pg.GetMeanConfidence();
//                            debugLogs.Add($"  OCR({r.name}) confidence: {conf:F2}");

//                            if (!string.IsNullOrWhiteSpace(text))
//                                debugLogs.Add($"  📄 OCR({r.name}) preview: {text.Replace("\n", " ")[..Math.Min(220, text.Length)]}");

//                            texts.Add(text);
//                        }
//                    }

//                    return string.Join("\n\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
//                }
//            }
//            catch (Exception ex)
//            {
//                debugLogs.Add($"  ❌ OCR Error: {ex.Message}");
//                if (ex.InnerException != null)
//                    debugLogs.Add($"     Inner: {ex.InnerException.Message}");
//                return "";
//            }
//        }
//        private string NormalizeOcrText(string text)
//        {
//            if (string.IsNullOrWhiteSpace(text)) return "";

//            string t = text.ToUpperInvariant();

//            // OCR hay chèn dấu nháy: I'TA, I'I'A...
//            t = Regex.Replace(t, @"[\'`\u2019\u2018]", "");

//            // OCR hay chèn nhiều dấu gạch: ITA--0027..., ITA - - 0027...
//            t = Regex.Replace(t, @"\s*-\s*", "-");
//            t = Regex.Replace(t, @"-+", "-");

//            // gom nhiều space
//            t = Regex.Replace(t, @"\s+", " ");

//            return t;
//        }
//        private string ExtractDocumentNumber(string text, List<string> debugLogs)
//        {
//            if (string.IsNullOrWhiteSpace(text))
//                return "Unknown";

//            // Normalize để bắt được kiểu I'TA--0027652 / I'I'A-00277648
//            string norm = NormalizeOcrText(text);

//            // ===== ƯU TIÊN 0: bắt trực tiếp ITA-xxxxxxx ở bất kỳ đâu (sau normalize) =====
//            var directIta = Regex.Match(norm, @"\bITA-\d{6,10}\b", RegexOptions.IgnoreCase);
//            if (directIta.Success)
//            {
//                string cleaned = CleanOcrCode(directIta.Value);
//                debugLogs.Add($"  ✓ Direct ITA code: '{directIta.Value}' → '{cleaned}'");
//                if (cleaned.Length >= 8)
//                    return SanitizeFileName(cleaned);
//            }

//            // ===== ƯU TIÊN 1: Số quản lý của nội bộ doanh nghiệp (có nhãn) =====
//            var soQuanLyMatch = Regex.Match(norm,
//                @"S[o06ốô]\w*\s+qu\w*\s+l[yý]\w*\s+c\w+\s+n\w+\s+b\w+\s+doanh\s+nghi\w+\s*[:\-]?\s*([A-Z]{2,8}[\s\-]\d{5,10}(?:[\s\-]\d+)?)",
//                RegexOptions.IgnoreCase);
//            if (soQuanLyMatch.Success)
//            {
//                string raw = soQuanLyMatch.Groups[1].Value;
//                string cleaned = CleanOcrCode(raw);
//                debugLogs.Add($"  Raw label code: '{raw}' → Cleaned: '{cleaned}'");
//                if (cleaned.Length >= 8)
//                {
//                    debugLogs.Add($"  ✓ Số quản lý nội bộ: {cleaned}");
//                    return SanitizeFileName(cleaned);
//                }
//            }

//            // Fallback: chỉ bắt dãy số 6-10 chữ số gần nhãn (nếu OCR rớt prefix)
//            var soQuanLyNumberMatch = Regex.Match(norm,
//                @"S[o06ô]\w*\s+qu\w*\s+l\w+\s+c\w+\s+n\w+\s+b\w+\s+doanh\s+nghi\w+\s*[:\-]?\s*(?:[A-Z]{0,8}\s*)?-?\s*(\d{6,10})",
//                RegexOptions.IgnoreCase);
//            if (soQuanLyNumberMatch.Success)
//            {
//                string digits = soQuanLyNumberMatch.Groups[1].Value.Trim();
//                string fallback = "ITA-" + digits;
//                debugLogs.Add($"  ✓ Số quản lý nội bộ (fallback digits): {fallback}");
//                return SanitizeFileName(fallback);
//            }

//            // ===== ƯU TIÊN 2: Invoice =====
//            var invoiceMatch = Regex.Match(norm, @"Invoice\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
//            if (invoiceMatch.Success && invoiceMatch.Groups[1].Value.Length >= 5)
//            {
//                string num = invoiceMatch.Groups[1].Value.Trim();
//                debugLogs.Add($"  ✓ Invoice: {num}");
//                return SanitizeFileName(num);
//            }

//            // ===== ƯU TIÊN 3: Packing list =====
//            var packingMatch = Regex.Match(norm, @"Packing\s*list\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
//            if (packingMatch.Success && packingMatch.Groups[1].Value.Length >= 5)
//            {
//                string num = packingMatch.Groups[1].Value.Trim();
//                debugLogs.Add($"  ✓ Packing: {num}");
//                return SanitizeFileName(num);
//            }

//            // ===== ƯU TIÊN 4: Generic PREFIX-XXXXXXX =====
//            var genericMatch = Regex.Match(norm,
//                @"\b([A-Z]{2,8}-\d{5,10}(?:-\d+)*(?:/[A-Z]{2,8}-\d{5,10}(?:-\d+)*)*)\b",
//                RegexOptions.IgnoreCase);
//            if (genericMatch.Success)
//            {
//                string cleaned = CleanOcrCode(genericMatch.Groups[1].Value);
//                debugLogs.Add($"  ✓ Generic code: '{genericMatch.Groups[1].Value}' → '{cleaned}'");
//                return SanitizeFileName(cleaned.ToUpperInvariant());
//            }

//            debugLogs.Add($"  ✗ Không tìm thấy mã hợp lệ");
//            return "Unknown";
//        }
//        // Làm sạch mã bị OCR nhận sai ký tự.
//        // Và normalize các prefix bị OCR sai (LTTA, ITTA, ITRA, PRA...) => ITA nếu digits có dạng 6-10 số
//        private string CleanOcrCode(string raw)
//        {
//            if (string.IsNullOrWhiteSpace(raw)) return "";

//            string s = raw.Trim().ToUpperInvariant();

//            // Dấu nháy đơn/backtick giữa 2 chữ cái -> T  ("I''A" -> "ITA")
//            s = Regex.Replace(s, @"(?<=[A-Z])[\'`\u2019\u2018]{1,2}(?=[A-Z])", "T");

//            // l/I/| giữa 2 chữ số -> 1
//            s = Regex.Replace(s, @"(?<=\d)[lI|](?=\d)", "1");

//            // Chuẩn hóa dấu gạch nối
//            s = Regex.Replace(s, @"\s*-\s*", "-");
//            s = Regex.Replace(s, @"\s+", " ");

//            // Nếu OCR ra "ITA 0027648" => "ITA-0027648"
//            s = Regex.Replace(s, @"\b([A-Z]{2,8})\s+(\d{6,10})\b", "$1-$2");

//            // Chỉ giữ A-Z, 0-9, '-', khoảng trắng (rồi bỏ space)
//            s = Regex.Replace(s, @"[^A-Z0-9\-\s]", "");
//            s = Regex.Replace(s, @"\s+", "");
//            s = s.Trim('-');

//            // Nếu không có dấu - mà có dạng PREFIX+SỐ -> thêm dấu -
//            if (!s.Contains('-') && s.Length >= 8 && Regex.IsMatch(s, @"^[A-Z]{2,8}\d{6,10}$"))
//            {
//                var pm = Regex.Match(s, @"^([A-Z]{2,8})(\d{6,10})$");
//                if (pm.Success)
//                    s = pm.Groups[1].Value + "-" + pm.Groups[2].Value;
//            }

//            // Rút gọn chữ số bị lặp do OCR: "777" -> "77"
//            var dupMatch = Regex.Match(s, @"^([A-Z]+-)(0*)(\d+)$");
//            if (dupMatch.Success)
//            {
//                string pfx = dupMatch.Groups[1].Value;
//                string zeros = dupMatch.Groups[2].Value;
//                string digits = dupMatch.Groups[3].Value;
//                string fixedDigits = Regex.Replace(digits, @"(.)\1{2,}", m => new string(m.Groups[1].Value[0], 2));
//                if (fixedDigits != digits)
//                    s = pfx + zeros + fixedDigits;
//            }

//            // Normalize prefix về ITA nếu digits hợp lệ
//            var m2 = Regex.Match(s, @"^([A-Z]{2,8})-(\d{6,10})$");
//            if (m2.Success)
//            {
//                var digits = m2.Groups[2].Value;
//                // nếu dãy số 6-10 chữ số => ép ITA để tránh LTTA/PRA/ITRA/ITTA...
//                s = "ITA-" + digits;
//            }

//            return s;
//        }

//        private string SanitizeFileName(string fileName)
//        {
//            if (string.IsNullOrWhiteSpace(fileName))
//                return "Unknown";

//            char[] invalidChars = Path.GetInvalidFileNameChars();
//            string safe = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
//            return safe.Substring(0, Math.Min(safe.Length, 150));
//        }
//    }

//    public class PdfFileInfo
//    {
//        public string OriginalName { get; set; }
//        public string FileName { get; set; }
//        public string FilePath { get; set; }
//        public int PageNumber { get; set; }
//        public string ExtractedCode { get; set; }
//        public string InvoiceNumber => ExtractedCode; // giữ tương thích view cũ
//    }
//}