using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using ClosedXML.Excel;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Tesseract;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfRenamer3PageController : Controller
    {
        private static readonly string TessDataPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        private static readonly bool HasTesseract =
            Directory.Exists(TessDataPath) &&
            System.IO.File.Exists(Path.Combine(TessDataPath, "eng.traineddata"));

        // ✅ Khóa tĩnh để serialize truy cập PDFium (thread-safe)
        //    PDFium không an toàn khi nhiều thread cùng dùng → phải lock
        private static readonly object _pdfiumLock = new object();

        // Mỗi tờ khai = 3 trang
        private const int PagesPerDoc = 3;

        [HttpGet]
        public IActionResult Upload() => View();

        // ════════════════════════════════════════════════════════════
        //  BƯỚC 1: Nhận 1 PDF lớn → tách theo dãy/số trang → xuất Excel
        //  Ưu tiên: pageRanges > pagesPerFile > mặc định 3 trang
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult Step1_SplitAndExcel(
            IFormFile bigPdf,
            int? pagesPerFile,
            string? pageRanges)
        {
            if (bigPdf == null || bigPdf.Length == 0)
                return BadRequest("Vui lòng chọn file PDF.");
            if (!bigPdf.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest("File phải có định dạng .pdf");

            if (pagesPerFile.HasValue && (pagesPerFile.Value < 1 || pagesPerFile.Value > 1000))
                return BadRequest("Số trang mỗi file phải từ 1 đến 1000.");

            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            try
            {
                var inputPath = Path.Combine(tempFolder, "input.pdf");
                using (var fs = new FileStream(inputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    bigPdf.CopyTo(fs);

                int totalPages;
                using (var reader = new PdfReader(inputPath))
                using (var doc = new PdfDocument(reader))
                    totalPages = doc.GetNumberOfPages();

                if (totalPages == 0)
                    return BadRequest("File PDF không có trang nào.");

                // ════════════════════════════════════════════════════════════
                //  XÁC ĐỊNH CÁCH TÁCH
                // ════════════════════════════════════════════════════════════
                var ranges = new List<(int start, int end)>();

                if (!string.IsNullOrWhiteSpace(pageRanges))
                {
                    // ── Mode A: Dãy số trang bắt đầu (giống PdfV2) ────────
                    List<int> startPages;
                    try
                    {
                        startPages = pageRanges
                            .Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => int.Parse(s.Trim()))
                            .OrderBy(x => x)
                            .Distinct()
                            .ToList();
                    }
                    catch
                    {
                        return BadRequest("Dãy số trang không hợp lệ. Ví dụ: 1,4,7");
                    }

                    if (startPages.Count == 0 || startPages[0] != 1)
                        return BadRequest("Dãy số trang phải bắt đầu bằng 1.");

                    for (int i = 0; i < startPages.Count; i++)
                    {
                        int s = startPages[i];
                        int e = (i + 1 < startPages.Count) ? startPages[i + 1] - 1 : totalPages;

                        if (s > totalPages)
                            return BadRequest($"Trang {s} vượt quá tổng số trang ({totalPages}).");

                        ranges.Add((s, e));
                    }
                }
                else
                {
                    // ── Mode B: Số trang mỗi file (mặc định 3) ────────────
                    int n = (pagesPerFile.HasValue && pagesPerFile.Value > 0)
                        ? pagesPerFile.Value
                        : PagesPerDoc;   // = 3

                    for (int s = 1; s <= totalPages; s += n)
                        ranges.Add((s, Math.Min(s + n - 1, totalPages)));
                }

                // ════════════════════════════════════════════════════════════
                //  TÁCH + OCR
                // ════════════════════════════════════════════════════════════
                var chunkFolder = Path.Combine(tempFolder, "chunks");
                Directory.CreateDirectory(chunkFolder);

                var rows = new List<(string ChunkName, string Stk1, string Stk2, string Stk3, string TenDatFile)>();
                var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < ranges.Count; i++)
                {
                    var (startPage, endPage) = ranges[i];

                    var tempChunk = Path.Combine(chunkFolder, $"_tmp_{i}.pdf");
                    SplitPages(inputPath, tempChunk, startPage, endPage);

                    string ocr1 = OcrPageFromFile(tempChunk, 0);
                    string ocr2 = OcrPageFromFile(tempChunk, 1);
                    string ocr3 = OcrPageFromFile(tempChunk, 2);

                    string stk1 = ExtractSoToKhai(ocr1);
                    string stk2 = ExtractSoToKhai(ocr2);
                    string stk3 = ExtractSoToKhai(ocr3);

                    string tenDatFile = VoteSoToKhai(stk1, stk2, stk3, i + 1);

                    string baseName = SanitizeFileName(tenDatFile);
                    if (nameCount.ContainsKey(baseName))
                    {
                        nameCount[baseName]++;
                        baseName = $"{baseName}_{nameCount[baseName]}";
                    }
                    else
                    {
                        nameCount[baseName] = 0;
                    }

                    string chunkName = $"{baseName}.pdf";
                    string chunkPath = Path.Combine(chunkFolder, chunkName);
                    System.IO.File.Move(tempChunk, chunkPath);

                    rows.Add((chunkName, stk1, stk2, stk3, tenDatFile));
                    GC.Collect(0, GCCollectionMode.Optimized);
                }

                // Xuất Excel + đóng gói ZIP
                string excelPath = Path.Combine(tempFolder, "Mapping.xlsx");
                BuildExcel(rows, excelPath);

                var outZipFolder = Path.Combine(tempFolder, "for_zip");
                Directory.CreateDirectory(outZipFolder);

                foreach (var f in Directory.GetFiles(chunkFolder))
                    System.IO.File.Copy(f, Path.Combine(outZipFolder, Path.GetFileName(f)));
                System.IO.File.Copy(excelPath, Path.Combine(outZipFolder, "Mapping.xlsx"));

                string zipPath = Path.Combine(tempFolder, "Step1_Output.zip");
                ZipFile.CreateFromDirectory(outZipFolder, zipPath);

                return PhysicalFile(zipPath, "application/zip",
                    $"Step1_ToKhai_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            }
            catch (Exception ex)
            {
                try { Directory.Delete(tempFolder, true); } catch { }
                return BadRequest($"Lỗi xử lý: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════
        //  BƯỚC 2: ZIP chunks + Excel đã điền Tên mới → ZIP PDF đổi tên
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult Step2_RenameFiles(IFormFile pdfZip, IFormFile mappingExcel)
        {
            if (pdfZip == null || pdfZip.Length == 0)
                return BadRequest("Thiếu file ZIP chứa PDF chunks.");
            if (mappingExcel == null || mappingExcel.Length == 0)
                return BadRequest("Thiếu file Excel mapping.");

            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            try
            {
                // Giải nén ZIP
                var pdfFolder = Path.Combine(tempFolder, "pdfs");
                Directory.CreateDirectory(pdfFolder);
                var zipPath = Path.Combine(tempFolder, "input.zip");
                using (var fs = new FileStream(zipPath, FileMode.Create)) pdfZip.CopyTo(fs);
                ZipFile.ExtractToDirectory(zipPath, pdfFolder, overwriteFiles: true);

                // Đọc Excel
                var excelPath = Path.Combine(tempFolder, "mapping.xlsx");
                using (var fs = new FileStream(excelPath, FileMode.Create)) mappingExcel.CopyTo(fs);

                // Dict: FileName -> (TenMoi, TenDatFile)
                var mapping = new Dictionary<string, (string TenMoi, string TenDatFile)>(
                    StringComparer.OrdinalIgnoreCase);

                using (var wb = new XLWorkbook(excelPath))
                {
                    var ws = wb.Worksheet(1);
                    int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
                    for (int r = 2; r <= lastRow; r++)
                    {
                        string orig = ws.Cell(r, 1).GetString()?.Trim();
                        string tenDatFile = ws.Cell(r, 5).GetString()?.Trim(); // Col E: TenDatFile (voting)
                        string tenMoi = ws.Cell(r, 6).GetString()?.Trim();     // Col F: TenMoi (user điền)

                        if (string.IsNullOrEmpty(orig)) continue;
                        mapping[orig] = (tenMoi, tenDatFile);
                    }
                }

                var outFolder = Path.Combine(tempFolder, "renamed");
                Directory.CreateDirectory(outFolder);

                var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var srcFile in Directory.GetFiles(pdfFolder))
                {
                    string originalName = Path.GetFileName(srcFile);
                    if (originalName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                        continue; // bỏ qua Excel nếu nằm trong ZIP

                    string newName;
                    if (mapping.TryGetValue(originalName, out var info))
                    {
                        string tenDatFile = string.IsNullOrWhiteSpace(info.TenDatFile)
                            ? Path.GetFileNameWithoutExtension(originalName)
                            : info.TenDatFile;

                        if (!string.IsNullOrWhiteSpace(info.TenMoi))
                            // TenMoi - TenDatFile.pdf
                            newName = $"{info.TenMoi}_{tenDatFile}.pdf";
                        else
                            // Không điền TenMoi → dùng TenDatFile (kết quả voting)
                            newName = $"{tenDatFile}.pdf";
                    }
                    else
                    {
                        newName = originalName;
                    }

                    string finalName = SanitizeFileName(Path.GetFileNameWithoutExtension(newName));
                    if (nameCount.ContainsKey(finalName))
                    {
                        nameCount[finalName]++;
                        finalName = $"{finalName}_{nameCount[finalName]}";
                    }
                    else
                    {
                        nameCount[finalName] = 0;
                    }

                    System.IO.File.Copy(srcFile, Path.Combine(outFolder, $"{finalName}.pdf"), true);
                }

                string resultZip = Path.Combine(tempFolder, "renamed.zip");
                ZipFile.CreateFromDirectory(outFolder, resultZip);

                return PhysicalFile(resultZip, "application/zip",
                    $"Renamed_PDFs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            }
            catch (Exception ex)
            {
                try { Directory.Delete(tempFolder, true); } catch { }
                return BadRequest($"Lỗi: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Tách trang
        // ════════════════════════════════════════════════════════════
        private void SplitPages(string sourcePath, string destPath, int startPage, int endPage)
        {
            using var reader = new PdfReader(sourcePath);
            using var srcDoc = new PdfDocument(reader);
            using var writer = new PdfWriter(destPath);
            using var dstDoc = new PdfDocument(writer);
            srcDoc.CopyPagesTo(startPage, endPage, dstDoc);
        }

        // ════════════════════════════════════════════════════════════
        //  OCR 1 trang
        //  - pageIndex 0 (trang 1): crop 22% đầu trang
        //  - pageIndex 1 (trang 2): crop 35% đầu trang (có "4. Số tờ khai :")
        //  - pageIndex 2 (trang 3): crop 22%
        //
        //  ✅ FIX: KHÔNG dùng 'using' cho DocLib.Instance — đây là singleton
        //     toàn cục, Dispose nó sẽ làm hỏng PDFium cho các request sau.
        //     Đồng thời bọc trong lock vì PDFium không thread-safe.
        // ════════════════════════════════════════════════════════════
        private string OcrPageFromFile(string pdfPath, int pageIndex)
        {
            // ── 1. Thử text layer trước ─────────────────────────────
            string textLayer = "";
            try
            {
                using var reader = new PdfReader(pdfPath);
                using var pdfDoc = new PdfDocument(reader);
                int iTextPage = pageIndex + 1;
                if (iTextPage <= pdfDoc.GetNumberOfPages())
                {
                    textLayer = PdfTextExtractor.GetTextFromPage(
                        pdfDoc.GetPage(iTextPage),
                        new SimpleTextExtractionStrategy()) ?? "";
                }
            }
            catch { }

            if (textLayer.Length >= 50)
                return textLayer;

            if (!HasTesseract) return textLayer;

            // ── 2. Render trang qua Docnet (trong lock — thread-safe) ─
            byte[] raw = null;
            int w = 0, h = 0;

            lock (_pdfiumLock)
            {
                try
                {
                    // ✅ KHÔNG 'using' — không bao giờ Dispose singleton
                    var library = DocLib.Instance;

                    using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2067, 2924));

                    if (pageIndex >= docReader.GetPageCount())
                        return textLayer;

                    using var pgReader = docReader.GetPageReader(pageIndex);
                    w = pgReader.GetPageWidth();
                    h = pgReader.GetPageHeight();
                    raw = pgReader.GetImage();   // byte[] đã copy ra → dùng ngoài lock an toàn
                }
                catch
                {
                    return textLayer;
                }
            }

            if (raw == null || w <= 0 || h <= 0)
                return textLayer;

            // ── 3. Xử lý ảnh + OCR (ngoài lock để giảm thời gian giữ) ─
            try
            {
                // Trang 2 (index=1): "4. Số tờ khai :" nằm sâu hơn → crop 35%
                float cropRatio = (pageIndex == 1) ? 0.35f : 0.22f;
                int cropH = (int)(h * cropRatio);

                byte[] png;
                using (var img = Image.LoadPixelData<Bgra32>(raw, w, h))
                {
                    img.Mutate(ctx => ctx
                        .Crop(new Rectangle(0, 0, w, Math.Max(1, cropH)))
                        .Resize(w * 2, cropH * 2)
                        .Grayscale()
                        .Contrast(1.2f)
                        .BinaryThreshold(0.38f));
                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    png = ms.ToArray();
                }

                string lang = System.IO.File.Exists(Path.Combine(TessDataPath, "vie.traineddata"))
                    ? "vie+eng" : "eng";

                using var engine = new TesseractEngine(TessDataPath, lang, EngineMode.LstmOnly);
                engine.SetVariable("tessedit_char_whitelist",
                    "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz :-./\n");

                using var pix = Pix.LoadFromMemory(png);
                using var page = engine.Process(pix, PageSegMode.Auto);

                return page.GetText() ?? "";
            }
            catch
            {
                return textLayer;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Voting majority
        // ════════════════════════════════════════════════════════════
        private string VoteSoToKhai(string s1, string s2, string s3, int unknownIndex)
        {
            var candidates = new List<(string Value, int Priority)>();
            if (!string.IsNullOrEmpty(s1)) candidates.Add((s1, 1));
            if (!string.IsNullOrEmpty(s2)) candidates.Add((s2, 2));
            if (!string.IsNullOrEmpty(s3)) candidates.Add((s3, 3));

            if (candidates.Count == 0) return $"unknown_{unknownIndex}";
            if (candidates.Count == 1) return candidates[0].Value;

            (string Rep, int BestPriority) winner = (string.Empty, int.MaxValue);

            for (int i = 0; i < candidates.Count; i++)
                for (int j = i + 1; j < candidates.Count; j++)
                    if (candidates[i].Value == candidates[j].Value)
                    {
                        int best = Math.Min(candidates[i].Priority, candidates[j].Priority);
                        string rep = candidates[i].Priority <= candidates[j].Priority
                            ? candidates[i].Value : candidates[j].Value;
                        if (best < winner.BestPriority)
                            winner = (rep, best);
                    }

            // Không có cặp exact → ưu tiên trang 1
            return string.IsNullOrEmpty(winner.Rep)
                ? candidates.OrderBy(c => c.Priority).First().Value
                : winner.Rep;
        }

        // ════════════════════════════════════════════════════════════
        //  Tìm Số tờ khai trong OCR text
        // ════════════════════════════════════════════════════════════
        private string ExtractSoToKhai(string ocrText)
        {
            if (string.IsNullOrEmpty(ocrText)) return string.Empty;

            string clean = NormalizeOCRText(ocrText);
            var lines = clean.Split('\n');
            var rawNumbers = new List<string>();

            // ── Ưu tiên 1: dòng có nhãn "so to khai" ──────────────────
            //   Trang 1: "Số tờ khai  308487879400  Số tờ khai đầu tiên"
            //   Trang 2: "4. Số tờ khai :  308487879400"
            foreach (var line in lines)
            {
                bool isToKhaiLine =
                    Regex.IsMatch(line, @"s[o0].{0,4}t[o0].{0,4}khai", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(line, @"\d\.\s*s.{0,5}t.{0,5}khai", RegexOptions.IgnoreCase);

                if (!isToKhaiLine) continue;

                foreach (Match m in Regex.Matches(line, @"[0-9OIlSB]{10,15}"))
                    rawNumbers.Add(NormalizeDigits(m.Value));
            }

            // ── Ưu tiên 2: quét toàn trang, bỏ dòng nhiễu ─────────────
            if (!rawNumbers.Any())
            {
                foreach (var line in lines)
                {
                    string lower = line.ToLower();
                    bool badLine =
                        lower.Contains("invoice") || lower.Contains("packing") ||
                        lower.Contains("hawb") || lower.Contains("tracking") ||
                        lower.Contains("bill to") || lower.Contains("ship to") ||
                        lower.Contains("vat") || lower.Contains("dhl") ||
                        lower.Contains("fedex") || lower.Contains("ups");
                    if (badLine) continue;

                    foreach (Match m in Regex.Matches(line, @"[0-9OIlSB]{10,15}"))
                        rawNumbers.Add(NormalizeDigits(m.Value));
                }
            }

            foreach (var n in rawNumbers.Distinct())
                if (IsValidSoToKhai(n)) return n;

            return string.Empty;
        }

        // ════════════════════════════════════════════════════════════
        //  Build Excel — 6 cột
        // ════════════════════════════════════════════════════════════
        private void BuildExcel(
            List<(string ChunkName, string Stk1, string Stk2, string Stk3, string TenDatFile)> rows,
            string outputPath)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            ws.Cell(1, 1).Value = "Tên file gốc (chunk)";
            ws.Cell(1, 2).Value = "Số tờ khai (trang 1)";
            ws.Cell(1, 3).Value = "Số tờ khai (trang 2)";
            ws.Cell(1, 4).Value = "Số tờ khai (trang 3)";
            ws.Cell(1, 5).Value = "TenDatFile (voting)";
            ws.Cell(1, 6).Value = "TenMoi (để trống = dùng TenDatFile)";

            var hdr = ws.Range(1, 1, 1, 6);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            hdr.Style.Font.FontColor = XLColor.White;
            hdr.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            for (int i = 0; i < rows.Count; i++)
            {
                int r = i + 2;
                var (name, stk1, stk2, stk3, tenDatFile) = rows[i];

                ws.Cell(r, 1).Value = name;
                ws.Cell(r, 2).Value = stk1;
                ws.Cell(r, 3).Value = stk2;
                ws.Cell(r, 4).Value = stk3;
                ws.Cell(r, 5).Value = tenDatFile;
                ws.Cell(r, 6).Value = ""; // Người dùng điền

                ws.Cell(r, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

                bool allUnknown = string.IsNullOrEmpty(stk1)
                               && string.IsNullOrEmpty(stk2)
                               && string.IsNullOrEmpty(stk3);
                if (allUnknown || tenDatFile.StartsWith("unknown_"))
                {
                    ws.Cell(r, 5).Style.Font.FontColor = XLColor.Red;
                    ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE0E0");
                }
            }

            ws.Column(1).Width = 28;
            ws.Column(2).Width = 22;
            ws.Column(3).Width = 22;
            ws.Column(4).Width = 22;
            ws.Column(5).Width = 22;
            ws.Column(6).Width = 36;
            ws.SheetView.Freeze(1, 0);

            if (rows.Count > 0)
            {
                var dataRange = ws.Range(1, 1, rows.Count + 1, 6);
                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            wb.SaveAs(outputPath);
        }

        // ════════════════════════════════════════════════════════════
        //  Helpers
        // ════════════════════════════════════════════════════════════

        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12) return false;
            if (v.StartsWith("2024") || v.StartsWith("2025") || v.StartsWith("2026")) return false;
            if (v.StartsWith("0000")) return false;
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
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return safe[..Math.Min(safe.Length, 150)];
        }
    }
}