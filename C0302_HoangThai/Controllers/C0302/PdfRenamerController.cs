using Microsoft.AspNetCore.Mvc;


using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Tesseract;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;


namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamerController : Controller
    {
        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

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

                    // Lấy tất cả PDF từ ZIP
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

                // Tạo ZIP file output
                var outputZipPath = Path.Combine(Path.GetTempPath(), $"Renamed_PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");
                ZipFile.CreateFromDirectory(tempFolder, outputZipPath);

                // Xóa folder tạm (giữ lại ZIP)
                try
                {
                    Directory.Delete(extractedFolder, true);
                }
                catch { }

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

        private void ProcessSinglePdf(string inputPath, string outputFolder, List<RenamedFileInfo> fileInfos, List<string> debugLogs)
        {
            try
            {
                string originalName = Path.GetFileName(inputPath);
                debugLogs.Add($"\n--- Xử lý: {originalName} ---");

                // Đọc nội dung PDF để lấy tên mới
                string newFileName = ExtractFileNameFromPdf(inputPath, debugLogs);

                // Tạo tên file output (tránh trùng)
                string outputPath = Path.Combine(outputFolder, $"{newFileName}.pdf");
                int counter = 1;
                while (System.IO.File.Exists(outputPath))
                {
                    outputPath = Path.Combine(outputFolder, $"{newFileName}_{counter}.pdf");
                    counter++;
                }

                // Copy file với tên mới
                System.IO.File.Copy(inputPath, outputPath, true);

                fileInfos.Add(new RenamedFileInfo
                {
                    OriginalName = originalName,
                    NewName = Path.GetFileName(outputPath),
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

        private string ExtractFileNameFromPdf(string pdfPath, List<string> debugLogs)
        {
            try
            {
                // Thử đọc text layer trước
                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader))
                {
                    var page = pdfDoc.GetPage(1); // Đọc trang đầu tiên
                    var strategy = new SimpleTextExtractionStrategy();
                    string pageText = PdfTextExtractor.GetTextFromPage(page, strategy);

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        debugLogs.Add($"  ✓ Đọc được text layer");
                        string fileName = ExtractDocumentNumber(pageText, debugLogs);
                        if (!fileName.StartsWith("Unknown"))
                        {
                            return fileName;
                        }
                    }
                }

                // Nếu không có text layer → dùng OCR
                debugLogs.Add($"  ⟳ Sử dụng OCR...");
                string ocrText = PerformOCR(pdfPath, debugLogs);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    return ExtractDocumentNumber(ocrText, debugLogs);
                }

                return "Unknown";
            }
            catch (Exception ex)
            {
                debugLogs.Add($"  ❌ Lỗi đọc PDF: {ex.Message}");
                return "Unknown";
            }
        }
        private string PerformOCR(string pdfPath, List<string> debugLogs)
        {
            try
            {
                using var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2160, 3840));
                using var pageReader = docReader.GetPageReader(0);

                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                var rawBytes = pageReader.GetImage();

                var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

                if (!Directory.Exists(tessDataPath) || !System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
                {
                    debugLogs.Add($"  ❌ Thiếu tessdata/eng.traineddata");
                    return "";
                }

                using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-/#:.,() ");

                string bestText = "";
                float bestConfidence = 0;
                var rotations = new[] { 0, 90, 180, 270 };

                foreach (var angle in rotations)
                {
                    using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rawBytes, width, height);

                    image.Mutate(x =>
                    {
                        if (angle > 0)
                        {
                            x.Rotate(angle);
                        }
                        x.Grayscale();
                        x.Contrast(1.2f);
                    });

                    using var ms = new MemoryStream();
                    image.SaveAsPng(ms);
                    ms.Position = 0;

                    using var pix = Pix.LoadFromMemory(ms.ToArray());
                    using var page = engine.Process(pix);

                    string text = page.GetText();
                    float confidence = page.GetMeanConfidence();

                    if (confidence > bestConfidence)
                    {
                        bestConfidence = confidence;
                        bestText = text;
                    }
                }

                debugLogs.Add($"  ✓ Best OCR confidence: {bestConfidence:F2}");

                // ✅ FIX: Kiểm tra bestText trước khi Substring
                if (!string.IsNullOrEmpty(bestText))
                {
                    int previewLength = Math.Min(300, bestText.Length);
                    debugLogs.Add($"  📄 OCR Text ({previewLength} ký tự đầu): {bestText.Substring(0, previewLength)}");
                }
                else
                {
                    debugLogs.Add($"  ⚠️ OCR không trích xuất được text nào");
                }

                return bestText ?? "";
            }
            catch (Exception ex)
            {
                debugLogs.Add($"  ❌ OCR Error: {ex.Message}");

                // ✅ Log thêm InnerException
                if (ex.InnerException != null)
                {
                    debugLogs.Add($"     Inner: {ex.InnerException.Message}");
                }

                return "";
            }
        }
        // Cập nhật hàm Extract với regex linh hoạt hơn
        private string ExtractDocumentNumber(string text, List<string> debugLogs)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "Unknown";
            }

            // Pattern 1: Invoice # (ưu tiên cao nhất)
            var invoiceMatch = Regex.Match(text, @"Invoice\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (invoiceMatch.Success && invoiceMatch.Groups[1].Value.Length >= 5)
            {
                string num = CleanupExtractedText(invoiceMatch.Groups[1].Value);
                if (num.Length >= 5)
                {
                    debugLogs.Add($"  ✓ Tìm thấy Invoice: {num}");
                    return SanitizeFileName(num);
                }
            }

            // Pattern 2: Packing list #
            var packingMatch = Regex.Match(text, @"Packing\s*[Ll]ist\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (packingMatch.Success && packingMatch.Groups[1].Value.Length >= 5)
            {
                string num = CleanupExtractedText(packingMatch.Groups[1].Value);
                if (num.Length >= 5)
                {
                    debugLogs.Add($"  ✓ Tìm thấy Packing: {num}");
                    return SanitizeFileName(num);
                }
            }

            // Pattern 3: Format XXX-XXXXXXX/YYY-YYYYYYY (SIÊU LINH HOẠT)
            var complexMatch = Regex.Match(text, @"([A-Z]{2,4}[-\s]?\d+(?:[-\s]?\d+)*(?:/[A-Z]{2,4}[-\s]?\d+(?:[-\s]?\d+)*)*)", RegexOptions.IgnoreCase);
            if (complexMatch.Success && complexMatch.Groups[1].Value.Length >= 7)
            {
                string num = CleanupExtractedText(complexMatch.Groups[1].Value);
                if (num.Length >= 7)
                {
                    debugLogs.Add($"  ✓ Tìm thấy Complex code: {num}");
                    return SanitizeFileName(num);
                }
            }

            // Pattern 4: Single code XXX-XXXXXX
            var simpleMatch = Regex.Match(text, @"([A-Z]{2,4}[-\s]?\d{5,}(?:[-\s]?\d+)*)", RegexOptions.IgnoreCase);
            if (simpleMatch.Success)
            {
                string num = CleanupExtractedText(simpleMatch.Groups[1].Value);
                if (num.Length >= 7)
                {
                    debugLogs.Add($"  ✓ Tìm thấy Simple code: {num}");
                    return SanitizeFileName(num);
                }
            }

            debugLogs.Add($"  ✗ Không tìm thấy mã hợp lệ");
            return "Unknown";
        }

        // HÀM MỚI: Làm sạch text OCR
        private string CleanupExtractedText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return "";

            // Loại bỏ khoảng trắng thừa
            string cleaned = Regex.Replace(rawText, @"\s+", "");

            // Loại bỏ ký tự đặc biệt (giữ chữ, số, dấu gạch ngang, slash)
            cleaned = Regex.Replace(cleaned, @"[^A-Z0-9\-/]", "", RegexOptions.IgnoreCase);

            // Loại bỏ dấu gạch ngang/slash ở đầu cuối
            cleaned = cleaned.Trim('-', '/');

            return cleaned.ToUpper();
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "Unknown";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string safe = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());

            return safe.Substring(0, Math.Min(safe.Length, 150));
        }

        public class RenamedFileInfo
        {
            public string OriginalName { get; set; }
            public string NewName { get; set; }
            public string FilePath { get; set; }
            public string ExtractedCode { get; set; }
        }
    }
}

