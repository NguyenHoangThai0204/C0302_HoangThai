using Microsoft.AspNetCore.Mvc;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using System.IO.Compression;
using System.Text.RegularExpressions;

using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Tesseract;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfController : Controller
    {
        private static readonly string TessDataPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        private static readonly bool HasTesseract =
            Directory.Exists(TessDataPath) &&
            System.IO.File.Exists(Path.Combine(TessDataPath, "eng.traineddata"));

        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

        [HttpPost]
        public IActionResult SplitPdf(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return Content("File không hợp lệ");

            string uploadPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot/uploads");
            Directory.CreateDirectory(uploadPath);

            string filePath = Path.Combine(uploadPath, file.FileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                file.CopyTo(stream);
            }

            Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>();

            using (PdfReader reader = new PdfReader(filePath))
            using (PdfDocument pdfDoc = new PdfDocument(reader))
            {
                int totalPages = pdfDoc.GetNumberOfPages();
                string currentKey = null;

                for (int i = 1; i <= totalPages; i++)
                {
                    string key = ExtractKeyFromPage(filePath, i);

                    if (!string.IsNullOrEmpty(key))
                    {
                        key = key.Replace("O", "0");

                        // ✅ CHỈ chấp nhận key dạng số quản lý DN
                        bool isSoQuanLy = Regex.IsMatch(key, @"^\d{3}/\d{4}/[A-Z0-9]+$");

                        if (isSoQuanLy)
                        {
                            currentKey = key;
                        }
                    }

                    if (string.IsNullOrEmpty(currentKey))
                        currentKey = "Unknown";

                    if (!groups.ContainsKey(currentKey))
                        groups[currentKey] = new List<int>();

                    groups[currentKey].Add(i);

                    Console.WriteLine($"Page {i} => {currentKey}");
                }

                string zipPath = Path.Combine(uploadPath, $"KetQua_{DateTime.Now:yyyyMMddHHmmss}.zip");

                using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var group in groups)
                    {
                        string safeKey = group.Key.Replace("/", "-").Replace("\\", "-");
                        string outFile = Path.Combine(uploadPath, $"{safeKey}.pdf");

                        using (PdfWriter writer = new PdfWriter(outFile))
                        using (PdfDocument newPdf = new PdfDocument(writer))
                        {
                            PdfMerger merger = new PdfMerger(newPdf);

                            foreach (var pageNum in group.Value)
                            {
                                merger.Merge(pdfDoc, pageNum, pageNum);
                            }
                        }

                        zip.CreateEntryFromFile(outFile, $"{safeKey}.pdf");
                    }
                }

                return PhysicalFile(zipPath, "application/zip", Path.GetFileName(zipPath));
            }
        }

        // =========================================================
        // OCR từng trang
        // =========================================================
        private string ExtractKeyFromPage(string pdfPath, int pageNumber)
        {
            if (!HasTesseract) return null;

            try
            {
                using var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2067, 2924));
                using var pgReader = docReader.GetPageReader(pageNumber - 1);

                int w = pgReader.GetPageWidth();
                int h = pgReader.GetPageHeight();
                byte[] raw = pgReader.GetImage();

                int cropH = (int)(h * 0.3f);

                byte[] png;
                using (var img = Image.LoadPixelData<Rgba32>(raw, w, h))
                {
                    img.Mutate(ctx => ctx
                        .Crop(new Rectangle(0, 0, w, cropH))
                        .Resize(w * 2, cropH * 2)
                        .Grayscale()
                        .BinaryThreshold(0.5f));

                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    png = ms.ToArray();
                }

                string lang = System.IO.File.Exists(Path.Combine(TessDataPath, "vie.traineddata"))
                    ? "vie+eng"
                    : "eng";

                using var engine = new TesseractEngine(TessDataPath, lang, EngineMode.LstmOnly);

                engine.SetVariable("tessedit_char_whitelist",
                    "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz :-./\n");

                using var pix = Pix.LoadFromMemory(png);
                using var page = engine.Process(pix);

                string text = page.GetText() ?? "";

                return FindKey(text);
            }
            catch
            {
                return null;
            }
        }

        // =========================================================
        // TÌM KEY
        // =========================================================
        private string FindKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string t = text.ToUpper();

            // Ưu tiên số quản lý DN
            var m1 = Regex.Match(t, @"\b\d{3}\/\d{4}\/[A-Z0-9]+\b");
            if (m1.Success)
                return m1.Value;

            return null; // ❌ bỏ luôn số 12 digit để tránh tách sai
        }
    }
}