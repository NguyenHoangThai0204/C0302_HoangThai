
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
//    public class PdfSplitterController : Controller
//    {
//[HttpGet]
//public IActionResult Upload()
//{
//    return View();
//}

//        [HttpPost]
//        public IActionResult Upload(IFormFile pdfFile)
//        {
//            if (pdfFile == null || pdfFile.Length == 0)
//            {
//                ViewBag.Error = "Vui lòng chọn file PDF";
//                return View();
//            }

//            if (!pdfFile.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
//            {
//                ViewBag.Error = "Chỉ chấp nhận file PDF";
//                return View();
//            }

//            string tempFolder = null;
//            string tempInputFile = null;

//            try
//            {
//                tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
//                Directory.CreateDirectory(tempFolder);

//                tempInputFile = Path.Combine(tempFolder, "input.pdf");
//                using (var fileStream = new FileStream(tempInputFile, FileMode.Create))
//                {
//                    pdfFile.CopyTo(fileStream);
//                }

//                var fileInfos = new List<PdfFileInfo>();
//                var debugLogs = new List<string>();

//                debugLogs.Add($"Bắt đầu xử lý file: {pdfFile.FileName}");
//                debugLogs.Add($"Thư mục tạm: {tempFolder}");

//                using (var inputDocument = PdfSharp.Pdf.IO.PdfReader.Open(tempInputFile, PdfDocumentOpenMode.Import))
//                {
//                    int totalPages = inputDocument.PageCount;
//                    debugLogs.Add($"Tổng số trang: {totalPages}");

//                    for (int i = 0; i < totalPages; i++)
//                    {
//                        int pageNum = i + 1;
//                        debugLogs.Add($"");
//                        debugLogs.Add($"========== XỬ LÝ TRANG {pageNum}/{totalPages} ==========");

//                        string fileName = ExtractFileNameFromPage(tempInputFile, pageNum, i, debugLogs);
//                        string outputPath = Path.Combine(tempFolder, $"{fileName}.pdf");

//                        // Xử lý trùng tên
//                        int counter = 1;
//                        while (System.IO.File.Exists(outputPath))
//                        {
//                            string newFileName = $"{fileName}_{counter}";
//                            outputPath = Path.Combine(tempFolder, $"{newFileName}.pdf");
//                            debugLogs.Add($"File trùng tên, đổi thành: {newFileName}.pdf");
//                            counter++;
//                        }

//                        // Tạo PDF riêng cho trang này
//                        using (var outputDocument = new PdfSharp.Pdf.PdfDocument())
//                        {
//                            outputDocument.AddPage(inputDocument.Pages[i]);
//                            outputDocument.Save(outputPath);
//                        }

//                        debugLogs.Add($"✓ Đã lưu file: {Path.GetFileName(outputPath)}");

//                        fileInfos.Add(new PdfFileInfo
//                        {
//                            FileName = Path.GetFileName(outputPath),
//                            FilePath = outputPath,
//                            PageNumber = pageNum,
//                            InvoiceNumber = fileName
//                        });
//                    }
//                }

//                // Lưu debug log
//                var logPath = Path.Combine(tempFolder, "debug_log.txt");
//                System.IO.File.WriteAllLines(logPath, debugLogs);
//                debugLogs.Add($"✓ Đã lưu log: {logPath}");

//                // Xóa file input
//                try
//                {
//                    if (System.IO.File.Exists(tempInputFile))
//                    {
//                        System.IO.File.Delete(tempInputFile);
//                        debugLogs.Add("✓ Đã xóa file input tạm");
//                    }
//                }
//                catch (Exception ex)
//                {
//                    debugLogs.Add($"⚠ Không thể xóa file input: {ex.Message}");
//                }

//                // Tạo ZIP
//                var zipPath = Path.Combine(Path.GetTempPath(), $"PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");

//                // Đảm bảo không có file ZIP cũ
//                if (System.IO.File.Exists(zipPath))
//                {
//                    System.IO.File.Delete(zipPath);
//                }

//                ZipFile.CreateFromDirectory(tempFolder, zipPath);
//                debugLogs.Add($"✓ Đã tạo ZIP: {zipPath}");
//                debugLogs.Add($"✓ Kích thước ZIP: {new FileInfo(zipPath).Length / 1024} KB");

//                // Kiểm tra nội dung ZIP
//                using (var zip = ZipFile.OpenRead(zipPath))
//                {
//                    debugLogs.Add($"✓ ZIP chứa {zip.Entries.Count} file:");
//                    foreach (var entry in zip.Entries)
//                    {
//                        debugLogs.Add($"  - {entry.Name} ({entry.Length} bytes)");
//                    }
//                }

//                ViewBag.Files = fileInfos;
//                ViewBag.ZipPath = zipPath;
//                ViewBag.TotalFiles = fileInfos.Count;
//                ViewBag.DebugLogs = debugLogs;
//                ViewBag.Success = $"✓ Đã tách thành công {fileInfos.Count} file PDF!";

//                return View();
//            }
//            catch (Exception ex)
//            {
//                ViewBag.Error = $"Lỗi xử lý file: {ex.Message}\n{ex.StackTrace}";
//                return View();
//            }
//        }

//        [HttpGet]
//        public IActionResult DownloadZip(string filePath)
//        {
//            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
//            {
//                return NotFound("File không tồn tại");
//            }

//            try
//            {
//                var memory = new MemoryStream();
//                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
//                {
//                    stream.CopyTo(memory); // ← FIX: Copy đúng vào memory
//                }
//                memory.Position = 0;

//                return File(memory, "application/zip", Path.GetFileName(filePath));
//            }
//            catch (Exception ex)
//            {
//                return BadRequest($"Lỗi tải file: {ex.Message}");
//            }
//        }

//        private string ExtractFileNameFromPage(string pdfPath, int pageNumber, int pageIndex, List<string> debugLogs)
//        {
//            try
//            {
//                // Thử đọc text layer trước
//                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
//                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader))
//                {
//                    var page = pdfDoc.GetPage(pageNumber);
//                    var strategy = new SimpleTextExtractionStrategy();
//                    string pageText = PdfTextExtractor.GetTextFromPage(page, strategy);

//                    if (!string.IsNullOrWhiteSpace(pageText))
//                    {
//                        debugLogs.Add($"📄 PDF có text layer");
//                        debugLogs.Add($"Text preview: {pageText.Substring(0, Math.Min(200, pageText.Length))}...");

//                        string fileName = ExtractDocumentNumber(pageText, pageNumber, debugLogs);
//                        debugLogs.Add($"→ Tên file: {fileName}");
//                        return fileName;
//                    }
//                }

//                // Nếu không có text → OCR
//                debugLogs.Add($"📷 PDF không có text layer → Dùng OCR");
//                string ocrText = PerformOCR(pdfPath, pageIndex, debugLogs);

//                if (!string.IsNullOrWhiteSpace(ocrText))
//                {
//                    debugLogs.Add($"OCR preview: {ocrText.Substring(0, Math.Min(200, ocrText.Length))}...");
//                }
//                else
//                {
//                    debugLogs.Add("⚠ OCR không đọc được text");
//                }

//                string result = ExtractDocumentNumber(ocrText, pageNumber, debugLogs);
//                debugLogs.Add($"→ Tên file: {result}");
//                return result;
//            }
//            catch (Exception ex)
//            {
//                debugLogs.Add($"❌ Lỗi: {ex.Message}");
//                return $"Page_{pageNumber:D3}";
//            }
//        }

//        private string PerformOCR(string pdfPath, int pageIndex, List<string> debugLogs)
//        {
//            Bitmap bitmap = null;

//            try
//            {
//                using (var library = DocLib.Instance)
//                using (var docReader = library.GetDocReader(pdfPath, new PageDimensions(1080, 1920)))
//                {
//                    if (pageIndex >= docReader.GetPageCount())
//                    {
//                        debugLogs.Add($"❌ Page index {pageIndex} vượt quá số trang");
//                        return "";
//                    }

//                    using (var pageReader = docReader.GetPageReader(pageIndex))
//                    {
//                        var width = pageReader.GetPageWidth();
//                        var height = pageReader.GetPageHeight();
//                        var rawBytes = pageReader.GetImage();

//                        debugLogs.Add($"  Render: {width}x{height}px");

//                        bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
//                        var bitmapData = bitmap.LockBits(
//                            new Rectangle(0, 0, width, height),
//                            ImageLockMode.WriteOnly,
//                            bitmap.PixelFormat);

//                        System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bitmapData.Scan0, rawBytes.Length);
//                        bitmap.UnlockBits(bitmapData);

//                        bitmap = PreprocessImage(bitmap, debugLogs);

//                        string bestText = "";
//                        float bestConfidence = 0;
//                        int bestRotation = 0;

//                        var rotations = new[] { 0, 90, 180, 270 };
//                        var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

//                        if (!Directory.Exists(tessDataPath) || !System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
//                        {
//                            debugLogs.Add($"❌ Thiếu tessdata/eng.traineddata");
//                            return "";
//                        }

//                        using (var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default))
//                        {
//                            engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-/#:.,() ");

//                            foreach (var rotation in rotations)
//                            {
//                                Bitmap rotatedBitmap = RotateImage(bitmap, rotation);

//                                try
//                                {
//                                    using (var pix = PixConverter.ToPix(rotatedBitmap))
//                                    using (var page = engine.Process(pix))
//                                    {
//                                        string text = page.GetText();
//                                        float confidence = page.GetMeanConfidence();

//                                        debugLogs.Add($"    {rotation}°: {text.Length} ký tự, conf={confidence:F2}");

//                                        if (confidence > bestConfidence)
//                                        {
//                                            bestConfidence = confidence;
//                                            bestText = text;
//                                            bestRotation = rotation;
//                                        }
//                                    }
//                                }
//                                finally
//                                {
//                                    if (rotation != 0) rotatedBitmap?.Dispose();
//                                }
//                            }
//                        }

//                        debugLogs.Add($"  → Best: {bestRotation}° (conf={bestConfidence:F2})");
//                        return bestText;
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                debugLogs.Add($"❌ OCR Error: {ex.Message}");
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

//        private Bitmap PreprocessImage(Bitmap original, List<string> debugLogs)
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

//        private string ExtractDocumentNumber(string text, int pageNumber, List<string> debugLogs)
//        {
//            if (string.IsNullOrWhiteSpace(text))
//            {
//                return $"Page_{pageNumber:D3}";
//            }

//            // Pattern 1: Invoice (cho phép nhiều số, gạch ngang, slash)
//            var invoiceMatch = Regex.Match(text, @"Invoice\s*#?\s*:?\s*([A-Z0-9\-/]+(?:\s*[A-Z0-9\-/]+)*)", RegexOptions.IgnoreCase);
//            if (invoiceMatch.Success && invoiceMatch.Groups[1].Value.Length >= 5)
//            {
//                string num = invoiceMatch.Groups[1].Value.Trim();
//                debugLogs.Add($"  ✓ Invoice: {num}");
//                return SanitizeFileName(num);
//            }

//            // Pattern 2: Packing list (cho phép nhiều số, gạch ngang, slash)
//            var packingMatch = Regex.Match(text, @"Packing\s*list\s*#?\s*:?\s*([A-Z0-9\-/]+(?:\s*[A-Z0-9\-/]+)*)", RegexOptions.IgnoreCase);
//            if (packingMatch.Success && packingMatch.Groups[1].Value.Length >= 5)
//            {
//                string num = packingMatch.Groups[1].Value.Trim();
//                debugLogs.Add($"  ✓ Packing: {num}");
//                return SanitizeFileName(num);
//            }

//            // Pattern 3: Phức tạp như ITA-0027367-68-69/SLO-0009 (ưu tiên cao nhất cho format này)
//            var complexMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7}(?:-\d+)*(?:/[A-Z]{3}-\d+(?:-\d+)*)*)\b", RegexOptions.IgnoreCase);
//            if (complexMatch.Success)
//            {
//                debugLogs.Add($"  ✓ Complex Format: {complexMatch.Groups[1].Value}");
//                return SanitizeFileName(complexMatch.Groups[1].Value.ToUpper());
//            }

//            // Pattern 4: ITA/SLO với đầy đủ số theo sau (lấy toàn bộ chuỗi)
//            var itaMatch = Regex.Match(text, @"\b((?:ITA|SLO)-[0-9\-]+(?:/(?:ITA|SLO)-[0-9\-]+)*)\b", RegexOptions.IgnoreCase);
//            if (itaMatch.Success)
//            {
//                debugLogs.Add($"  ✓ ITA/SLO: {itaMatch.Value}");
//                return SanitizeFileName(itaMatch.Value.Trim().ToUpper());
//            }

//            // Pattern 5: VIE-XXXXXXX (giữ nguyên)
//            var vieMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7})\b", RegexOptions.IgnoreCase);
//            if (vieMatch.Success)
//            {
//                debugLogs.Add($"  ✓ VIE: {vieMatch.Groups[1].Value}");
//                return SanitizeFileName(vieMatch.Groups[1].Value.ToUpper());
//            }

//            // Pattern 6: Generic XXX-XXXXXX với extension
//            var genericMatch = Regex.Match(text, @"\b([A-Z]{2,4}-\d{6,}(?:-\d+)*)\b", RegexOptions.IgnoreCase);
//            if (genericMatch.Success)
//            {
//                debugLogs.Add($"  ✓ Generic: {genericMatch.Groups[1].Value}");
//                return SanitizeFileName(genericMatch.Groups[1].Value.ToUpper());
//            }

//            debugLogs.Add($"  ✗ Không tìm thấy mã");
//            return $"Page_{pageNumber:D3}";
//        }

//        private string SanitizeFileName(string fileName)
//        {
//            if (string.IsNullOrWhiteSpace(fileName))
//                return "Unknown";

//            char[] invalidChars = Path.GetInvalidFileNameChars();
//            string safe = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
//            return safe.Substring(0, Math.Min(safe.Length, 100));
//        }
//    }

//    public class PdfFileInfo
//    {
//        public string FileName { get; set; }
//        public string FilePath { get; set; }
//        public int PageNumber { get; set; }
//        public string InvoiceNumber { get; set; }
//    }
//}



using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
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
    public class PdfSplitterController : Controller
    {
        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

        [HttpPost]
        public IActionResult Upload(IFormFile pdfFile)
        {
            if (pdfFile == null || pdfFile.Length == 0)
            {
                ViewBag.Error = "Vui lòng chọn file PDF";
                return View();
            }

            if (!pdfFile.FileName.EndsWith(".pdf"))
            {
                ViewBag.Error = "Chỉ chấp nhận file PDF";
                return View();
            }

            try
            {
                var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempFolder);

                var tempInputFile = Path.Combine(tempFolder, "input.pdf");
                using (var fileStream = new FileStream(tempInputFile, FileMode.Create))
                {
                    pdfFile.CopyTo(fileStream);
                }

                var fileInfos = new List<PdfFileInfo>();
                var debugLogs = new List<string>();

                using (var inputDocument = PdfSharp.Pdf.IO.PdfReader.Open(tempInputFile, PdfDocumentOpenMode.Import))
                {
                    int totalPages = inputDocument.PageCount;

                    for (int i = 0; i < totalPages; i++)
                    {
                        int pageNum = i + 1;
                        string fileName = ExtractFileNameFromPage(tempInputFile, pageNum, i, debugLogs);
                        string outputPath = Path.Combine(tempFolder, $"{fileName}.pdf");

                        int counter = 1;
                        while (System.IO.File.Exists(outputPath))
                        {
                            outputPath = Path.Combine(tempFolder, $"{fileName}_{counter}.pdf");
                            counter++;
                        }

                        using (var outputDocument = new PdfSharp.Pdf.PdfDocument())
                        {
                            outputDocument.AddPage(inputDocument.Pages[i]);
                            outputDocument.Save(outputPath);
                        }

                        fileInfos.Add(new PdfFileInfo
                        {
                            FileName = Path.GetFileName(outputPath),
                            FilePath = outputPath,
                            PageNumber = pageNum,
                            InvoiceNumber = fileName
                        });
                    }
                }

                var logPath = Path.Combine(tempFolder, "debug_log.txt");
                System.IO.File.WriteAllLines(logPath, debugLogs);

                try
                {
                    if (System.IO.File.Exists(tempInputFile))
                    {
                        System.IO.File.Delete(tempInputFile);
                    }
                }
                catch (Exception ex)
                {
                    debugLogs.Add($"Không thể xóa file input: {ex.Message}");
                }

                var zipPath = Path.Combine(Path.GetTempPath(), $"PDFs_{DateTime.Now:yyyyMMddHHmmss}.zip");
                ZipFile.CreateFromDirectory(tempFolder, zipPath);

                ViewBag.Files = fileInfos;
                ViewBag.ZipPath = zipPath;
                ViewBag.TotalFiles = fileInfos.Count;
                ViewBag.DebugLogs = debugLogs;
                ViewBag.Success = $"Đã tách thành công {fileInfos.Count} file PDF!";

                return View();
            }
            catch (Exception ex)
            {
                ViewBag.Error = $"Lỗi xử lý file: {ex.Message}\n{ex.StackTrace}";
                return View();
            }
        }

        [HttpGet]
        public IActionResult DownloadZip(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var memory = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open))
            {
                stream.CopyTo(memory); // ← ĐÚNG! Copy vào memory
            }
            memory.Position = 0;

            return File(memory, "application/zip", Path.GetFileName(filePath));
        }

        private string ExtractFileNameFromPage(string pdfPath, int pageNumber, int pageIndex, List<string> debugLogs)
        {
            try
            {
                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader))
                {
                    var page = pdfDoc.GetPage(pageNumber);
                    var strategy = new SimpleTextExtractionStrategy();
                    string pageText = PdfTextExtractor.GetTextFromPage(page, strategy);

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        debugLogs.Add($"=== TRANG {pageNumber} (Text Layer) ===");
                        debugLogs.Add($"Text: {pageText.Substring(0, Math.Min(500, pageText.Length))}");

                        string fileName = ExtractDocumentNumber(pageText, pageNumber, debugLogs);
                        debugLogs.Add($"Tên file: {fileName}");
                        debugLogs.Add("---");
                        return fileName;
                    }
                }

                debugLogs.Add($"=== TRANG {pageNumber} (Dùng OCR) ===");
                string ocrText = PerformOCR(pdfPath, pageIndex, debugLogs);

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    debugLogs.Add($"OCR Text: {ocrText.Substring(0, Math.Min(500, ocrText.Length))}");
                }
                else
                {
                    debugLogs.Add("OCR không đọc được text");
                }

                string result = ExtractDocumentNumber(ocrText, pageNumber, debugLogs);
                debugLogs.Add($"Tên file: {result}");
                debugLogs.Add("---");

                return result;
            }
            catch (Exception ex)
            {
                debugLogs.Add($"Lỗi đọc trang {pageNumber}: {ex.Message}");
                return $"Page_{pageNumber:D3}";
            }
        }

        private string PerformOCR(string pdfPath, int pageIndex, List<string> debugLogs)
        {
            Bitmap bitmap = null;

            try
            {
                using (var library = DocLib.Instance)
                {
                    using (var docReader = library.GetDocReader(pdfPath, new PageDimensions(1080, 1920)))
                    {
                        if (pageIndex >= docReader.GetPageCount())
                        {
                            debugLogs.Add($"Page index {pageIndex} vượt quá số trang");
                            return "";
                        }

                        using (var pageReader = docReader.GetPageReader(pageIndex))
                        {
                            var width = pageReader.GetPageWidth();
                            var height = pageReader.GetPageHeight();
                            var rawBytes = pageReader.GetImage();

                            debugLogs.Add($"✓ Render trang {pageIndex + 1}: {width}x{height}");

                            bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                            var bitmapData = bitmap.LockBits(
                                new Rectangle(0, 0, width, height),
                                ImageLockMode.WriteOnly,
                                bitmap.PixelFormat);

                            System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bitmapData.Scan0, rawBytes.Length);
                            bitmap.UnlockBits(bitmapData);

                            // Tiền xử lý
                            bitmap = PreprocessImage(bitmap, debugLogs);

                            // Thử OCR với nhiều góc xoay
                            string bestText = "";
                            float bestConfidence = 0;

                            var rotations = new[] { 0, 90, 180, 270 };

                            var tessDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

                            if (!Directory.Exists(tessDataPath) || !System.IO.File.Exists(Path.Combine(tessDataPath, "eng.traineddata")))
                            {
                                debugLogs.Add($"❌ Thiếu tessdata/eng.traineddata");
                                return "";
                            }

                            using (var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default))
                            {
                                engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-/#:.,() ");

                                foreach (var rotation in rotations)
                                {
                                    Bitmap rotatedBitmap = RotateImage(bitmap, rotation);

                                    try
                                    {
                                        using (var pix = PixConverter.ToPix(rotatedBitmap))
                                        using (var page = engine.Process(pix))
                                        {
                                            string text = page.GetText();
                                            float confidence = page.GetMeanConfidence();

                                            debugLogs.Add($"  Góc {rotation}°: {text.Length} ký tự, confidence: {confidence:F2}");

                                            if (confidence > bestConfidence)
                                            {
                                                bestConfidence = confidence;
                                                bestText = text;
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        if (rotation != 0) rotatedBitmap?.Dispose();
                                    }
                                }
                            }

                            debugLogs.Add($"✓ Chọn text tốt nhất (confidence: {bestConfidence:F2})");

                            if (bestText.Length > 0)
                            {
                                var preview = bestText.Replace("\n", " ").Substring(0, Math.Min(200, bestText.Length));
                                debugLogs.Add($"Preview: {preview}");
                            }

                            return bestText;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                debugLogs.Add($"❌ OCR Error: {ex.Message}");
                return "";
            }
            finally
            {
                bitmap?.Dispose();
            }
        }

        // Hàm xoay ảnh
        private Bitmap RotateImage(Bitmap image, float angle)
        {
            if (angle == 0) return image;

            Bitmap rotatedBitmap = new Bitmap(image.Width, image.Height);
            rotatedBitmap.SetResolution(image.HorizontalResolution, image.VerticalResolution);

            using (Graphics g = Graphics.FromImage(rotatedBitmap))
            {
                g.Clear(Color.White);
                g.TranslateTransform(image.Width / 2f, image.Height / 2f);
                g.RotateTransform(angle);
                g.TranslateTransform(-image.Width / 2f, -image.Height / 2f);
                g.DrawImage(image, new Point(0, 0));
            }

            return rotatedBitmap;
        }
        private Bitmap PreprocessImage(Bitmap original, List<string> debugLogs)
        {
            try
            {
                Bitmap processed = new Bitmap(original.Width, original.Height);

                for (int y = 0; y < processed.Height; y++)
                {
                    for (int x = 0; x < processed.Width; x++)
                    {
                        Color pixel = original.GetPixel(x, y);
                        int gray = (int)(pixel.R * 0.3 + pixel.G * 0.59 + pixel.B * 0.11);
                        gray = gray > 127 ? 255 : 0;
                        processed.SetPixel(x, y, Color.FromArgb(gray, gray, gray));
                    }
                }

                debugLogs.Add("✓ Tiền xử lý ảnh (grayscale + threshold)");
                return processed;
            }
            catch (Exception ex)
            {
                debugLogs.Add($"Lỗi tiền xử lý: {ex.Message}");
                return original;
            }
        }

        private string ExtractDocumentNumber(string text, int pageNumber, List<string> debugLogs)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return $"Page_{pageNumber:D3}";
            }

            // Pattern 1: Invoice
            var invoiceMatch = Regex.Match(text, @"Invoice\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (invoiceMatch.Success && invoiceMatch.Groups[1].Value.Length >= 5)
            {
                string num = invoiceMatch.Groups[1].Value.Trim();
                debugLogs.Add($"  ✓ Invoice: {num}");
                return SanitizeFileName(num);
            }

            // Pattern 2: Packing list
            var packingMatch = Regex.Match(text, @"Packing\s*list\s*#?\s*:?\s*([A-Z0-9\-/]+)", RegexOptions.IgnoreCase);
            if (packingMatch.Success && packingMatch.Groups[1].Value.Length >= 5)
            {
                string num = packingMatch.Groups[1].Value.Trim();
                debugLogs.Add($"  ✓ Packing: {num}");
                return SanitizeFileName(num);
            }

            // Pattern 3: VIE-XXXXXXX
            //var vieMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7})\b", RegexOptions.IgnoreCase);
            var vieMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7}(?:-\d+)*(?:/[A-Z]{3}-\d+(?:-\d+)*)*)\b", RegexOptions.IgnoreCase);
            if (vieMatch.Success)
            {
                debugLogs.Add($"  ✓ VIE: {vieMatch.Groups[1].Value}");
                return SanitizeFileName(vieMatch.Groups[1].Value.ToUpper());
            }

            // Pattern 4: ITA/SLO
            //var itaMatch = Regex.Match(text, @"\b(ITA|SLO)-[0-9\-/]+", RegexOptions.IgnoreCase);
            var itaMatch = Regex.Match(text, @"\b((?:ITA|SLO)-[0-9\-]+(?:/(?:ITA|SLO)-[0-9\-]+)*)\b", RegexOptions.IgnoreCase);
            if (itaMatch.Success)
            {
                debugLogs.Add($"  ✓ ITA/SLO: {itaMatch.Value}");
                return SanitizeFileName(itaMatch.Value.Trim().ToUpper());
            }

            // Pattern 5: Generic XXX-XXXXXX
            //var genericMatch = Regex.Match(text, @"\b([A-Z]{2,4}-\d{6,})\b", RegexOptions.IgnoreCase);
            var genericMatch = Regex.Match(text, @"\b([A-Z]{3}-\d{7}(?:-\d+)*(?:/[A-Z]{3}-\d+(?:-\d+)*)*)\b", RegexOptions.IgnoreCase);
            if (genericMatch.Success)
            {
                debugLogs.Add($"  ✓ Generic: {genericMatch.Groups[1].Value}");
                return SanitizeFileName(genericMatch.Groups[1].Value.ToUpper());
            }

            debugLogs.Add($"  ✗ Không tìm thấy mã");
            return $"Page_{pageNumber:D3}";
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "Unknown";

            char[] invalidChars = Path.GetInvalidFileNameChars();
            string safe = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return safe.Substring(0, Math.Min(safe.Length, 100));
        }
    }

    public class PdfFileInfo
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public int PageNumber { get; set; }
        public string InvoiceNumber { get; set; }
    }
}

