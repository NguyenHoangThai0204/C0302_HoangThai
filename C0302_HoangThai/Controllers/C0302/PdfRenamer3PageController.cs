using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Tesseract;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamer3PageController : Controller
    {
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

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        debugLogs.Add($"  ✓ Đọc được text layer");
                        debugLogs.Add($"  Text (500 ký tự đầu): {pageText.Substring(0, Math.Min(500, pageText.Length))}");

                        string fileName = ExtractDocumentNumber(pageText, debugLogs);
                        if (!fileName.StartsWith("Unknown"))
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

        private string PerformOCR(string pdfPath, List<string> debugLogs)
        {
            Bitmap fullBitmap = null;
            Bitmap bottomBitmap = null;
            try
            {
                using (var library = DocLib.Instance)
                // Tăng độ phân giải để OCR chính xác hơn
                using (var docReader = library.GetDocReader(pdfPath, new PageDimensions(3508, 4961)))
                using (var pageReader = docReader.GetPageReader(0))
                {
                    var width = pageReader.GetPageWidth();
                    var height = pageReader.GetPageHeight();
                    var rawBytes = pageReader.GetImage();

                    fullBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    var bitmapData = fullBitmap.LockBits(
                        new Rectangle(0, 0, width, height),
                        ImageLockMode.WriteOnly,
                        fullBitmap.PixelFormat);
                    System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bitmapData.Scan0, rawBytes.Length);
                    fullBitmap.UnlockBits(bitmapData);

                    // Crop vùng 1/4 dưới của trang — nơi có "Số quản lý của nội bộ doanh nghiệp"
                    int cropStartY = (int)(height * 0.72);
                    int cropHeight = height - cropStartY;
                    var cropRect = new Rectangle(0, cropStartY, width, cropHeight);
                    bottomBitmap = fullBitmap.Clone(cropRect, fullBitmap.PixelFormat);
                    debugLogs.Add($"  ✓ Crop vùng cuối trang: Y={cropStartY} → {height} (cao {cropHeight}px)");

                    // Tiền xử lý vùng crop
                    bottomBitmap = PreprocessImage(bottomBitmap, debugLogs);

                    var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");
                    if (!Directory.Exists(tessDataPath) || !System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
                    {
                        debugLogs.Add($"  ❌ Thiếu tessdata/eng.traineddata");
                        return "";
                    }

                    string bottomText = "";
                    string fullText = "";

                    using (var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default))
                    {
                        // Bỏ whitelist để đọc được tiếng Việt có dấu
                        engine.SetVariable("tessedit_pageseg_mode", "6"); // Assume uniform block of text

                        // Bước 1: OCR vùng cuối trang (ưu tiên tìm mã số)
                        using (var pix = PixConverter.ToPix(bottomBitmap))
                        using (var pg = engine.Process(pix))
                        {
                            bottomText = pg.GetText();
                            float conf = pg.GetMeanConfidence();
                            debugLogs.Add($"  OCR vùng cuối: confidence={conf:F2}");
                            debugLogs.Add($"  📄 Bottom OCR: {bottomText.Replace("\n", " ").Substring(0, Math.Min(400, bottomText.Length))}");
                        }

                        // Nếu tìm thấy mã trong vùng cuối → trả về luôn
                        string quickCheck = ExtractDocumentNumber(bottomText, debugLogs);
                        if (!quickCheck.StartsWith("Unknown"))
                        {
                            debugLogs.Add($"  ✓ Tìm thấy mã trong vùng cuối trang: {quickCheck}");
                            return quickCheck;
                        }

                        // Bước 2: Fallback OCR toàn trang nếu vùng cuối không có kết quả
                        debugLogs.Add($"  ⟳ Không tìm thấy ở vùng cuối, OCR toàn trang...");
                        Bitmap fullProcessed = PreprocessImage(fullBitmap, new List<string>());
                        using (var pix = PixConverter.ToPix(fullProcessed))
                        using (var pg = engine.Process(pix))
                        {
                            fullText = pg.GetText();
                            float conf = pg.GetMeanConfidence();
                            debugLogs.Add($"  OCR toàn trang: confidence={conf:F2}");
                            debugLogs.Add($"  📄 Full OCR preview: {fullText.Replace("\n", " ").Substring(0, Math.Min(400, fullText.Length))}");
                        }
                        fullProcessed?.Dispose();
                    }

                    return string.IsNullOrWhiteSpace(fullText) ? bottomText : fullText;
                }
            }
            catch (Exception ex)
            {
                debugLogs.Add($"  ❌ OCR Error: {ex.Message}");
                if (ex.InnerException != null)
                    debugLogs.Add($"     Inner: {ex.InnerException.Message}");
                return "";
            }
            finally
            {
                fullBitmap?.Dispose();
                bottomBitmap?.Dispose();
            }
        }

        private Bitmap PreprocessImage(Bitmap original, List<string> debugLogs)
        {
            try
            {
                // Scale 2x để OCR chính xác hơn
                int newW = original.Width * 2;
                int newH = original.Height * 2;
                Bitmap scaled = new Bitmap(newW, newH);
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(original, 0, 0, newW, newH);
                }

                // Grayscale + threshold 160 (lọc tốt hơn nền hồng của tờ khai hải quan)
                Bitmap processed = new Bitmap(newW, newH);
                for (int y = 0; y < newH; y++)
                    for (int x = 0; x < newW; x++)
                    {
                        Color pixel = scaled.GetPixel(x, y);
                        int gray = (int)(pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114);
                        gray = gray > 160 ? 255 : 0;
                        processed.SetPixel(x, y, Color.FromArgb(gray, gray, gray));
                    }

                scaled.Dispose();
                if (debugLogs != null && debugLogs.Count >= 0)
                    debugLogs.Add("  ✓ Tiền xử lý: scale 2x + grayscale + threshold(160)");
                return processed;
            }
            catch (Exception ex)
            {
                debugLogs?.Add($"  Lỗi tiền xử lý: {ex.Message}");
                return original;
            }
        }

        private string ExtractDocumentNumber(string text, List<string> debugLogs)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "Unknown";

            // ===== ƯU TIÊN 1: Số quản lý của nội bộ doanh nghiệp =====
            // OCR hay nhận sai: "S6" thay "Số", "ndi" thay "nội", mã bị nhiễu như "I''A-00277652"
            // → Tìm nhãn linh hoạt, sau đó làm sạch phần mã trích ra

            var soQuanLyMatch = Regex.Match(text,
                @"S[o06\u00f4]\w*\s+qu\w*\s+l\w+\s+c\w+\s+n\w+\s+b\w+\s+doanh\s+nghi\w+\s*[:\-]?\s*([A-Z][A-Z0-9''`\-]{3,12})",
                RegexOptions.IgnoreCase);
            if (soQuanLyMatch.Success)
            {
                string raw = soQuanLyMatch.Groups[1].Value;
                string cleaned = CleanOcrCode(raw);
                debugLogs.Add($"  Raw OCR code: '{raw}' → Cleaned: '{cleaned}'");
                if (cleaned.Length >= 5)
                {
                    debugLogs.Add($"  ✓ Số quản lý nội bộ: {cleaned}");
                    return SanitizeFileName(cleaned);
                }
            }

            // ===== ƯU TIÊN 2: Invoice =====
            var invoiceMatch = Regex.Match(text, @"Invoice\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (invoiceMatch.Success && invoiceMatch.Groups[1].Value.Length >= 5)
            {
                string num = invoiceMatch.Groups[1].Value.Trim();
                debugLogs.Add($"  ✓ Invoice: {num}");
                return SanitizeFileName(num);
            }

            // ===== ƯU TIÊN 3: Packing list =====
            var packingMatch = Regex.Match(text, @"Packing\s*list\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (packingMatch.Success && packingMatch.Groups[1].Value.Length >= 5)
            {
                string num = packingMatch.Groups[1].Value.Trim();
                debugLogs.Add($"  ✓ Packing: {num}");
                return SanitizeFileName(num);
            }

            // ===== ƯU TIÊN 4: VIE-XXXXXXX =====
            var vieMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7}(?:-\d+)*(?:/[A-Z]{3}-\d+(?:-\d+)*)*)\b", RegexOptions.IgnoreCase);
            if (vieMatch.Success)
            {
                debugLogs.Add($"  ✓ VIE: {vieMatch.Groups[1].Value}");
                return SanitizeFileName(vieMatch.Groups[1].Value.ToUpper());
            }

            // ===== ƯU TIÊN 5: ITA/SLO =====
            var itaMatch = Regex.Match(text, @"\b((?:ITA|SLO)-[0-9\-]+(?:/(?:ITA|SLO)-[0-9\-]+)*)\b", RegexOptions.IgnoreCase);
            if (itaMatch.Success)
            {
                debugLogs.Add($"  ✓ ITA/SLO: {itaMatch.Value}");
                return SanitizeFileName(itaMatch.Value.Trim().ToUpper());
            }

            // ===== ƯU TIÊN 6: Generic XXX-XXXXXX =====
            var genericMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7}(?:-\d+)*(?:/[A-Z]{3}-\d+(?:-\d+)*)*)\b", RegexOptions.IgnoreCase);
            if (genericMatch.Success)
            {
                debugLogs.Add($"  ✓ Generic: {genericMatch.Groups[1].Value}");
                return SanitizeFileName(genericMatch.Groups[1].Value.ToUpper());
            }

            debugLogs.Add($"  ✗ Không tìm thấy mã hợp lệ");
            return "Unknown";
        }

        // Làm sạch mã bị OCR nhận sai ký tự. Ví dụ: "I''A-00277652" -> "ITA-0027652"
        private string CleanOcrCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string s = raw.Trim().ToUpper();

            // Dấu nháy đơn/backtick giữa 2 chữ cái -> T  ("I''A" -> "ITA")
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=[A-Z])['`’‘]{1,2}(?=[A-Z])", "T");

            // l/I/| giữa 2 chữ số -> 1
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<=\d)[lI|](?=\d)", "1");

            // Chỉ giữ A-Z, 0-9, dấu gạch ngang
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[^A-Z0-9\-]", "");
            s = s.Trim('-');

            // Nếu không có dấu - mà có dạng PREFIX+SỐ -> thêm dấu -
            if (!s.Contains('-') && s.Length >= 8 && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Z]{2,5}\d{5,}$"))
            {
                int prefixLen = System.Text.RegularExpressions.Regex.Match(s, @"^([A-Z]+)").Length;
                s = s.Substring(0, prefixLen) + "-" + s.Substring(prefixLen);
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