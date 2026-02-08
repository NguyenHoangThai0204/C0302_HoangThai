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
                using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2160, 3840)); // Tăng resolution
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

                // WHITELIST - chỉ nhận ký tự hợp lệ
                engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-/#:.,() ");

                string bestText = "";
                float bestConfidence = 0;
                var rotations = new[] { 0, 90, 180, 270 };

                foreach (var angle in rotations)
                {
                    using Image<Rgba32> image = Image.LoadPixelData<Rgba32>(rawBytes, width, height);

                    // Preprocessing
                    image.Mutate(x =>
                    {
                        if (angle > 0)
                        {
                            x.Rotate(angle);
                        }
                        x.Grayscale();
                        x.Contrast(1.2f); // Tăng độ tương phản
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
                debugLogs.Add($"  📄 OCR Text (300 ký tự đầu): {bestText.Substring(0, Math.Min(300, bestText.Length))}");

                return bestText;
            }
            catch (Exception ex)
            {
                debugLogs.Add($"  ❌ OCR Error: {ex.Message}");
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




//using Microsoft.AspNetCore.Mvc;


//using Microsoft.AspNetCore.Http;
//using Microsoft.AspNetCore.Mvc;
//using PdfSharp.Pdf;
//using PdfSharp.Pdf.IO;
//using iText.Kernel.Pdf;
//using iText.Kernel.Pdf.Canvas.Parser;
//using iText.Kernel.Pdf.Canvas.Parser.Listener;
//using Tesseract;
//using System;
//using System.Collections.Generic;
//using System.Drawing;
//using System.Drawing.Imaging;
//using System.IO;
//using System.IO.Compression;
//using System.Linq;
//using System.Text.RegularExpressions;
//using Docnet.Core;
//using Docnet.Core.Models;


//namespace C0302_HoangThai.Controllers.C0302
//{
//    public class PdfRenamerController : Controller
//    {
//        [HttpGet]
//        public IActionResult Upload()
//        {
//            return View();
//        }

//        [HttpPost]
//        public IActionResult ProcessFiles(List<IFormFile> pdfFiles, IFormFile zipFile)
//        {
//            var debugLogs = new List<string>();
//            var fileInfos = new List<RenamedFileInfo>();

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

//                    // Lấy tất cả PDF từ ZIP
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

//                // Tạo ZIP file output
//                var outputZipPath = Path.Combine(Path.GetTempPath(), $"Renamed_PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");
//                ZipFile.CreateFromDirectory(tempFolder, outputZipPath);

//                // Xóa folder tạm (giữ lại ZIP)
//                try
//                {
//                    Directory.Delete(extractedFolder, true);
//                }
//                catch { }

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

//        private void ProcessSinglePdf(string inputPath, string outputFolder, List<RenamedFileInfo> fileInfos, List<string> debugLogs)
//        {
//            try
//            {
//                string originalName = Path.GetFileName(inputPath);
//                debugLogs.Add($"\n--- Xử lý: {originalName} ---");

//                // Đọc nội dung PDF để lấy tên mới
//                string newFileName = ExtractFileNameFromPdf(inputPath, debugLogs);

//                // Tạo tên file output (tránh trùng)
//                string outputPath = Path.Combine(outputFolder, $"{newFileName}.pdf");
//                int counter = 1;
//                while (System.IO.File.Exists(outputPath))
//                {
//                    outputPath = Path.Combine(outputFolder, $"{newFileName}_{counter}.pdf");
//                    counter++;
//                }

//                // Copy file với tên mới
//                System.IO.File.Copy(inputPath, outputPath, true);

//                fileInfos.Add(new RenamedFileInfo
//                {
//                    OriginalName = originalName,
//                    NewName = Path.GetFileName(outputPath),
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

//        private string ExtractFileNameFromPdf(string pdfPath, List<string> debugLogs)
//        {
//            try
//            {
//                // Thử đọc text layer trước
//                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
//                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader))
//                {
//                    var page = pdfDoc.GetPage(1); // Đọc trang đầu tiên
//                    var strategy = new SimpleTextExtractionStrategy();
//                    string pageText = PdfTextExtractor.GetTextFromPage(page, strategy);

//                    if (!string.IsNullOrWhiteSpace(pageText))
//                    {
//                        debugLogs.Add($"  ✓ Đọc được text layer");
//                        string fileName = ExtractDocumentNumber(pageText, debugLogs);
//                        if (!fileName.StartsWith("Unknown"))
//                        {
//                            return fileName;
//                        }
//                    }
//                }

//                // Nếu không có text layer → dùng OCR
//                debugLogs.Add($"  ⟳ Sử dụng OCR...");
//                string ocrText = PerformOCR(pdfPath, debugLogs);

//                if (!string.IsNullOrWhiteSpace(ocrText))
//                {
//                    return ExtractDocumentNumber(ocrText, debugLogs);
//                }

//                return "Unknown";
//            }
//            catch (Exception ex)
//            {
//                debugLogs.Add($"  ❌ Lỗi đọc PDF: {ex.Message}");
//                return "Unknown";
//            }
//        }

//        private string PerformOCR(string pdfPath, List<string> debugLogs)
//        {
//            Bitmap bitmap = null;

//            try
//            {
//                using (var library = DocLib.Instance)
//                {
//                    using (var docReader = library.GetDocReader(pdfPath, new PageDimensions(1080, 1920)))
//                    {
//                        using (var pageReader = docReader.GetPageReader(0)) // Trang đầu tiên
//                        {
//                            var width = pageReader.GetPageWidth();
//                            var height = pageReader.GetPageHeight();
//                            var rawBytes = pageReader.GetImage();

//                            bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
//                            var bitmapData = bitmap.LockBits(
//                                new Rectangle(0, 0, width, height),
//                                ImageLockMode.WriteOnly,
//                                bitmap.PixelFormat);

//                            System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bitmapData.Scan0, rawBytes.Length);
//                            bitmap.UnlockBits(bitmapData);

//                            // Tiền xử lý ảnh
//                            bitmap = PreprocessImage(bitmap);

//                            // OCR với nhiều góc xoay
//                            string bestText = "";
//                            float bestConfidence = 0;
//                            var rotations = new[] { 0, 90, 180, 270 };

//                            var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

//                            if (!Directory.Exists(tessDataPath) || !System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
//                            {
//                                debugLogs.Add($"  ❌ Thiếu tessdata/eng.traineddata");
//                                return "";
//                            }

//                            using (var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default))
//                            {
//                                engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-/#:.,() ");

//                                foreach (var rotation in rotations)
//                                {
//                                    Bitmap rotatedBitmap = RotateImage(bitmap, rotation);

//                                    try
//                                    {
//                                        using (var pix = PixConverter.ToPix(rotatedBitmap))
//                                        using (var page = engine.Process(pix))
//                                        {
//                                            string text = page.GetText();
//                                            float confidence = page.GetMeanConfidence();

//                                            if (confidence > bestConfidence)
//                                            {
//                                                bestConfidence = confidence;
//                                                bestText = text;
//                                            }
//                                        }
//                                    }
//                                    finally
//                                    {
//                                        if (rotation != 0) rotatedBitmap?.Dispose();
//                                    }
//                                }
//                            }

//                            debugLogs.Add($"  ✓ OCR confidence: {bestConfidence:F2}");
//                            return bestText;
//                        }
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                debugLogs.Add($"  ❌ OCR Error: {ex.Message}");
//                return "";
//            }
//            finally
//            {
//                bitmap?.Dispose();
//            }
//        }

//        private Bitmap RotateImage(Bitmap image, float angle)
//        {
//            if (angle == 0) return image;

//            Bitmap rotatedBitmap = new Bitmap(image.Width, image.Height);
//            rotatedBitmap.SetResolution(image.HorizontalResolution, image.VerticalResolution);

//            using (Graphics g = Graphics.FromImage(rotatedBitmap))
//            {
//                g.Clear(Color.White);
//                g.TranslateTransform(image.Width / 2f, image.Height / 2f);
//                g.RotateTransform(angle);
//                g.TranslateTransform(-image.Width / 2f, -image.Height / 2f);
//                g.DrawImage(image, new Point(0, 0));
//            }

//            return rotatedBitmap;
//        }

//        private Bitmap PreprocessImage(Bitmap original)
//        {
//            try
//            {
//                Bitmap processed = new Bitmap(original.Width, original.Height);

//                for (int y = 0; y < processed.Height; y++)
//                {
//                    for (int x = 0; x < processed.Width; x++)
//                    {
//                        Color pixel = original.GetPixel(x, y);
//                        int gray = (int)(pixel.R * 0.3 + pixel.G * 0.59 + pixel.B * 0.11);
//                        gray = gray > 127 ? 255 : 0;
//                        processed.SetPixel(x, y, Color.FromArgb(gray, gray, gray));
//                    }
//                }

//                return processed;
//            }
//            catch
//            {
//                return original;
//            }
//        }

//        private string ExtractDocumentNumber(string text, List<string> debugLogs)
//        {
//            if (string.IsNullOrWhiteSpace(text))
//            {
//                return "Unknown";
//            }

//            // Pattern 1: Invoice
//            var invoiceMatch = Regex.Match(text, @"Invoice\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
//            if (invoiceMatch.Success && invoiceMatch.Groups[1].Value.Length >= 5)
//            {
//                string num = invoiceMatch.Groups[1].Value.Trim();
//                debugLogs.Add($"  ✓ Tìm thấy Invoice: {num}");
//                return SanitizeFileName(num);
//            }

//            // Pattern 2: Packing list
//            var packingMatch = Regex.Match(text, @"Packing\s*list\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
//            if (packingMatch.Success && packingMatch.Groups[1].Value.Length >= 5)
//            {
//                string num = packingMatch.Groups[1].Value.Trim();
//                debugLogs.Add($"  ✓ Tìm thấy Packing: {num}");
//                return SanitizeFileName(num);
//            }

//            // Pattern 3: VIE-XXXXXXX hoặc có thêm suffix
//            var vieMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7}(?:-\d+)*(?:/[A-Z]{3}-\d+(?:-\d+)*)*)\b", RegexOptions.IgnoreCase);
//            if (vieMatch.Success)
//            {
//                debugLogs.Add($"  ✓ Tìm thấy VIE code: {vieMatch.Groups[1].Value}");
//                return SanitizeFileName(vieMatch.Groups[1].Value.ToUpper());
//            }

//            // Pattern 4: ITA/SLO
//            var itaMatch = Regex.Match(text, @"\b((?:ITA|SLO)-[0-9\-]+(?:/(?:ITA|SLO)-[0-9\-]+)*)\b", RegexOptions.IgnoreCase);
//            if (itaMatch.Success)
//            {
//                debugLogs.Add($"  ✓ Tìm thấy ITA/SLO: {itaMatch.Value}");
//                return SanitizeFileName(itaMatch.Value.Trim().ToUpper());
//            }

//            // Pattern 5: Generic XXX-XXXXXX
//            var genericMatch = Regex.Match(text, @"\b([A-Z]{2,4}-\d{6,})\b", RegexOptions.IgnoreCase);
//            if (genericMatch.Success)
//            {
//                debugLogs.Add($"  ✓ Tìm thấy Generic code: {genericMatch.Groups[1].Value}");
//                return SanitizeFileName(genericMatch.Groups[1].Value.ToUpper());
//            }

//            debugLogs.Add($"  ✗ Không tìm thấy mã hợp lệ");
//            return "Unknown";
//        }

//        private string SanitizeFileName(string fileName)
//        {
//            if (string.IsNullOrWhiteSpace(fileName))
//                return "Unknown";

//            char[] invalidChars = Path.GetInvalidFileNameChars();
//            string safe = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
//            return safe.Substring(0, Math.Min(safe.Length, 100));
//        }

//        [HttpGet]
//        public IActionResult DownloadZip(string filePath)
//        {
//            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
//            {
//                return NotFound();
//            }

//            var memory = new MemoryStream();
//            using (var stream = new FileStream(filePath, FileMode.Open))
//            {
//                stream.CopyTo(memory);
//            }
//            memory.Position = 0;

//            return File(memory, "application/zip", Path.GetFileName(filePath));
//        }
//    }

//    public class RenamedFileInfo
//    {
//        public string OriginalName { get; set; }
//        public string NewName { get; set; }
//        public string FilePath { get; set; }
//        public string ExtractedCode { get; set; }
//    }
//}