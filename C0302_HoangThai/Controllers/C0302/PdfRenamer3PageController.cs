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

        // Mỗi tờ khai = 3 trang
        private const int PagesPerDoc = 3;

        [HttpGet]
        public IActionResult Upload() => View();

        // ════════════════════════════════════════════════════════════
        //  BƯỚC 1: Nhận 1 PDF lớn → tách mỗi 3 trang → xuất Excel
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult Step1_SplitAndExcel(IFormFile bigPdf)
        {
            if (bigPdf == null || bigPdf.Length == 0)
                return BadRequest("Vui lòng chọn file PDF.");
            if (!bigPdf.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest("File phải có định dạng .pdf");

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

                int docCount = (int)Math.Ceiling((double)totalPages / PagesPerDoc);

                var chunkFolder = Path.Combine(tempFolder, "chunks");
                Directory.CreateDirectory(chunkFolder);

                // FileName | SoToKhai1 | SoToKhai2 | SoToKhai3 | TenDatFile (voting)
                var rows = new List<(string ChunkName, string Stk1, string Stk2, string Stk3, string TenDatFile)>();
                var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < docCount; i++)
                {
                    int startPage = i * PagesPerDoc + 1;
                    int endPage = Math.Min(startPage + PagesPerDoc - 1, totalPages);

                    // Tách chunk tạm
                    var tempChunk = Path.Combine(chunkFolder, $"_tmp_{i}.pdf");
                    SplitPages(inputPath, tempChunk, startPage, endPage);

                    // OCR 3 trang → Voting
                    string ocr1 = OcrPageFromFile(tempChunk, 0);
                    string ocr2 = OcrPageFromFile(tempChunk, 1);
                    string ocr3 = OcrPageFromFile(tempChunk, 2);

                    string stk1 = ExtractSoToKhai(ocr1);
                    string stk2 = ExtractSoToKhai(ocr2);
                    string stk3 = ExtractSoToKhai(ocr3);

                    string tenDatFile = VoteSoToKhai(stk1, stk2, stk3, i + 1);

                    // Đặt tên chunk = kết quả voting, tránh trùng
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

                // Xuất Excel mapping
                string excelPath = Path.Combine(tempFolder, "Mapping.xlsx");
                BuildExcel(rows, excelPath);

                // Đóng gói ZIP
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
        //  Format tên: TenMoi_TenDatFile.pdf  (giống PdfV2)
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
                        string tenMoi = ws.Cell(r, 6).GetString()?.Trim(); // Col F: TenMoi (user điền)

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
                            // TenMoi - TenDatFile.pdf (giống PdfV2)
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
        //  OCR 1 trang: text layer → Docnet+ImageSharp+Tesseract
        //  pageIndex 0 (trang 1): crop 22% đầu trang
        //  pageIndex 1 (trang 2): crop 35% đầu trang (có "4. Số tờ khai :")
        //  pageIndex 2 (trang 3): crop 22%
        // ════════════════════════════════════════════════════════════
        private string OcrPageFromFile(string pdfPath, int pageIndex)
        {
            // Thử text layer trước
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

            try
            {
                using var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2067, 2924));

                if (pageIndex >= docReader.GetPageCount()) return textLayer;

                using var pgReader = docReader.GetPageReader(pageIndex);
                int w = pgReader.GetPageWidth();
                int h = pgReader.GetPageHeight();
                byte[] raw = pgReader.GetImage();

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
            catch { return textLayer; }
        }

        // ════════════════════════════════════════════════════════════
        //  Voting majority — port từ PdfV2
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
        //  Ưu tiên 1: dòng có nhãn "Số tờ khai" (trang 1 & 2)
        //             Trang 2: "4. Số tờ khai :  308487879400"
        //  Ưu tiên 2: quét toàn trang, bỏ dòng nhiễu
        // ════════════════════════════════════════════════════════════
        private string ExtractSoToKhai(string ocrText)
        {
            if (string.IsNullOrEmpty(ocrText)) return string.Empty;

            string clean = NormalizeOCRText(ocrText);
            var lines = clean.Split('\n');
            var rawNumbers = new List<string>();

            // ── Ưu tiên 1: dòng có nhãn "so to khai" ──────────────────
            // Bắt cả 2 dạng:
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
        //  Build Excel — 6 cột: Tên file | STK1 | STK2 | STK3 | TenDatFile | TenMoi
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
        //  Helpers — port từ PdfV2
        // ════════════════════════════════════════════════════════════

        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12) return false;
            // Loại số trông như năm ở đầu
            if (v.StartsWith("2024") || v.StartsWith("2025") || v.StartsWith("2026")) return false;
            // Loại prefix rõ ràng không phải số tờ khai
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

//using Microsoft.AspNetCore.Http;
//using Microsoft.AspNetCore.Mvc;
//using iText.Kernel.Pdf;
//using iText.Kernel.Pdf.Canvas.Parser;
//using iText.Kernel.Pdf.Canvas.Parser.Listener;
//using ClosedXML.Excel;
//using Docnet.Core;
//using Docnet.Core.Models;
//using SixLabors.ImageSharp;
//using SixLabors.ImageSharp.PixelFormats;
//using SixLabors.ImageSharp.Processing;
//using Tesseract;
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.IO.Compression;
//using System.Linq;
//using System.Text.RegularExpressions;

//namespace C0302_HoangThai.Controllers.C0302
//{
//    public class PdfRenamer3PageController : Controller
//    {
//        private static readonly string TessDataPath =
//            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

//        private static readonly bool HasTesseract =
//            Directory.Exists(TessDataPath) &&
//            System.IO.File.Exists(Path.Combine(TessDataPath, "eng.traineddata"));

//        [HttpGet]
//        public IActionResult Upload() => View();

//        // ════════════════════════════════════════════════════════════
//        //  BƯỚC 1: PDF/ZIP → Excel mapping
//        // ════════════════════════════════════════════════════════════
//        [HttpPost]
//        [RequestSizeLimit(500_000_000)]
//        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
//        public IActionResult Step1_ExportExcel(List<IFormFile> pdfFiles, IFormFile zipFile)
//        {
//            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
//            Directory.CreateDirectory(tempFolder);

//            try
//            {
//                var pdfFolder = Path.Combine(tempFolder, "pdfs");
//                Directory.CreateDirectory(pdfFolder);

//                var allPdfs = new List<string>();

//                if (zipFile != null && zipFile.Length > 0)
//                {
//                    var zipPath = Path.Combine(tempFolder, "input.zip");
//                    using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
//                        zipFile.CopyTo(fs);
//                    ZipFile.ExtractToDirectory(zipPath, pdfFolder, overwriteFiles: true);
//                    allPdfs.AddRange(Directory.GetFiles(pdfFolder, "*.pdf", SearchOption.AllDirectories));
//                }

//                if (pdfFiles != null)
//                {
//                    foreach (var f in pdfFiles)
//                    {
//                        if (f == null || f.Length == 0) continue;
//                        if (!f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
//                        string safeName = Path.GetFileName(f.FileName);
//                        string dest = GetUniquePath(pdfFolder, safeName);
//                        using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
//                            f.CopyTo(fs);
//                        allPdfs.Add(dest);
//                    }
//                }

//                if (allPdfs.Count == 0)
//                    return BadRequest("Không tìm thấy file PDF nào.");

//                // ✅ Chỉ 2 cột — bỏ Ghi chú
//                var rows = new List<(string Name, string SoToKhai)>();

//                foreach (var pdfPath in allPdfs)
//                {
//                    string name = Path.GetFileName(pdfPath);
//                    try
//                    {
//                        string soToKhai = ExtractSoToKhai(pdfPath);
//                        rows.Add((name, soToKhai));
//                    }
//                    catch
//                    {
//                        rows.Add((name, "Unknown"));
//                    }
//                    finally
//                    {
//                        GC.Collect(0, GCCollectionMode.Optimized);
//                    }
//                }

//                string excelPath = Path.Combine(tempFolder, "Mapping.xlsx");
//                BuildExcel(rows, excelPath);

//                // ✅ Không dùng cookie — JS fetch+blob tự xử lý
//                string dlName = $"Mapping_SoToKhai_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
//                return PhysicalFile(excelPath,
//                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
//                    dlName);
//            }
//            catch (Exception ex)
//            {
//                try { Directory.Delete(tempFolder, true); } catch { }
//                return BadRequest($"Lỗi xử lý: {ex.Message}");
//            }
//        }

//        // ════════════════════════════════════════════════════════════
//        //  BƯỚC 2: ZIP PDF + Excel → ZIP PDF đổi tên
//        // ════════════════════════════════════════════════════════════
//        [HttpPost]
//        [RequestSizeLimit(500_000_000)]
//        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
//        public IActionResult Step2_RenameFiles(IFormFile pdfZip, IFormFile mappingExcel)
//        {
//            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
//            Directory.CreateDirectory(tempFolder);

//            try
//            {
//                if (pdfZip == null || pdfZip.Length == 0)
//                    return BadRequest("Thiếu file ZIP chứa PDF.");
//                if (mappingExcel == null || mappingExcel.Length == 0)
//                    return BadRequest("Thiếu file Excel mapping.");

//                var pdfFolder = Path.Combine(tempFolder, "pdfs");
//                Directory.CreateDirectory(pdfFolder);
//                var zipPath = Path.Combine(tempFolder, "input.zip");
//                using (var fs = new FileStream(zipPath, FileMode.Create)) pdfZip.CopyTo(fs);
//                ZipFile.ExtractToDirectory(zipPath, pdfFolder, overwriteFiles: true);

//                var excelPath = Path.Combine(tempFolder, "mapping.xlsx");
//                using (var fs = new FileStream(excelPath, FileMode.Create)) mappingExcel.CopyTo(fs);
//                var mapping = ReadMappingExcel(excelPath);

//                var outFolder = Path.Combine(tempFolder, "renamed");
//                Directory.CreateDirectory(outFolder);

//                foreach (var kvp in mapping)
//                {
//                    string origName = kvp.Key;
//                    string tenMoi = kvp.Value?.Trim();

//                    var srcPath = Directory.GetFiles(pdfFolder, origName, SearchOption.AllDirectories)
//                                           .FirstOrDefault();
//                    if (srcPath == null) continue;

//                    string finalName = string.IsNullOrWhiteSpace(tenMoi)
//                        ? Path.GetFileNameWithoutExtension(origName)
//                        : tenMoi;

//                    string dest = GetUniquePath(outFolder, $"{SanitizeFileName(finalName)}.pdf");
//                    System.IO.File.Copy(srcPath, dest, true);
//                }

//                string resultZip = Path.Combine(tempFolder, "renamed.zip");
//                ZipFile.CreateFromDirectory(outFolder, resultZip);

//                // ✅ Không dùng cookie
//                string dlName = $"Renamed_PDFs_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
//                return PhysicalFile(resultZip, "application/zip", dlName);
//            }
//            catch (Exception ex)
//            {
//                try { Directory.Delete(tempFolder, true); } catch { }
//                return BadRequest($"Lỗi: {ex.Message}");
//            }
//        }

//        // ════════════════════════════════════════════════════════════
//        //  CORE: Đọc Số tờ khai từ PDF scan ảnh
//        // ════════════════════════════════════════════════════════════
//        private string ExtractSoToKhai(string pdfPath)
//        {
//            // ── 1. Thử text layer trước (PDF có text) ─────────────────
//            try
//            {
//                using var reader = new iText.Kernel.Pdf.PdfReader(pdfPath);
//                using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader);
//                string text = PdfTextExtractor.GetTextFromPage(
//                    pdfDoc.GetPage(1), new SimpleTextExtractionStrategy()) ?? "";

//                if (Regex.Replace(text, @"\s", "").Length >= 20)
//                {
//                    string found = FindSoToKhaiInText(text);
//                    if (found != null) return found;
//                }
//            }
//            catch { }

//            // ── 2. OCR cho PDF scan ảnh ───────────────────────────────
//            if (!HasTesseract) return "Unknown";

//            try
//            {
//                using var library = DocLib.Instance;
//                // Render 250 DPI để OCR rõ hơn
//                using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2067, 2924));
//                using var pgReader = docReader.GetPageReader(0);

//                int w = pgReader.GetPageWidth();
//                int h = pgReader.GetPageHeight();
//                byte[] raw = pgReader.GetImage();

//                // ✅ Crop top 25% — đủ bắt dòng "Số tờ khai" kể cả layout có tiêu đề lớn
//                int cropH = (int)(h * 0.25f);

//                byte[] png;
//                using (var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(raw, w, h))
//                {
//                    img.Mutate(ctx => ctx
//                        .Crop(new Rectangle(0, 0, w, Math.Max(1, cropH)))
//                        .Resize(w * 2, cropH * 2)   // phóng 2x cho OCR chính xác
//                        .Grayscale()
//                        .BinaryThreshold(0.50f));    // threshold 0.50 cho scan màu hồng

//                    using var ms = new MemoryStream();
//                    img.SaveAsPng(ms);
//                    png = ms.ToArray();
//                }

//                // ✅ Dùng "vie" (tiếng Việt) thay "eng" để OCR label tiếng Việt đúng hơn
//                // Nếu chưa có vie.traineddata thì fallback về eng
//                string lang = System.IO.File.Exists(Path.Combine(TessDataPath, "vie.traineddata"))
//                    ? "vie+eng"
//                    : "eng";

//                using var engine = new TesseractEngine(TessDataPath, lang, EngineMode.LstmOnly);

//                // Chỉ cho phép ký tự số + chữ cái + dấu phổ biến để OCR nhanh và sạch hơn
//                engine.SetVariable("tessedit_char_whitelist",
//                    "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz :-./\n");

//                using var pix = Pix.LoadFromMemory(png);
//                using var pg = engine.Process(pix, PageSegMode.Auto);

//                string ocrText = pg.GetText() ?? "";
//                string result = FindSoToKhaiInText(ocrText);
//                return result ?? "Unknown";
//            }
//            catch
//            {
//                return "Unknown";
//            }
//        }

//        // ════════════════════════════════════════════════════════════
//        //  Tìm Số tờ khai trong text (text layer hoặc OCR)
//        //
//        //  Tờ khai VN chuẩn:
//        //    "Số tờ khai   308278270660   Số tờ khai đầu tiên"
//        //  Số tờ khai = đúng 12 chữ số liên tiếp
//        //
//        //  Chiến lược:
//        //   A. Tìm theo từng dòng chứa label "Số tờ khai" → lấy số 12 chữ số
//        //      trên chính dòng đó (tránh bắt nhầm số ở dòng khác)
//        //   B. Regex multiline label + số kế tiếp
//        //   C. Standalone đúng 12 chữ số (KHÔNG dùng 10-13 để tránh nhầm)
//        // ════════════════════════════════════════════════════════════
//        private string FindSoToKhaiInText(string text)
//        {
//            if (string.IsNullOrWhiteSpace(text)) return null;

//            // ── Chiến lược A: Tìm theo từng dòng ────────────────────
//            // iText và OCR thường xuất mỗi ô thành 1 dòng riêng
//            foreach (var rawLine in text.Split('\n'))
//            {
//                string line = rawLine.Trim();
//                if (line.Length < 3) continue;

//                string lineUp = Regex.Replace(line.ToUpperInvariant(), @"[ \t]+", " ");

//                // Dòng phải chứa label dạng "S? T? KHAI" (tiếng Việt hoặc OCR nhòe)
//                bool hasLabel = Regex.IsMatch(lineUp,
//                    @"S[O06\u1ED0\u00D4\u1ED2\u1ED4\u1ED6\u1ED8].{0,5}T[O0\u1EDD\u1EDF\u1EE1\u1EE3\u1EDB\u01A0].{0,5}KHAI",
//                    RegexOptions.IgnoreCase);

//                // Fallback: dòng chứa "SO TO KHAI" hoặc biến thể OCR
//                if (!hasLabel)
//                    hasLabel = Regex.IsMatch(lineUp, @"S.{0,3}T.{0,3}KHAI", RegexOptions.IgnoreCase);

//                if (!hasLabel) continue;

//                // Lấy tất cả chuỗi số trên dòng đó
//                var nums = Regex.Matches(lineUp, @"\d+");

//                // Ưu tiên: số đúng 12 chữ số
//                foreach (Match nm in nums)
//                    if (nm.Value.Length == 12)
//                        return nm.Value;

//                // Ghép tất cả số lại (trường hợp OCR tách số bằng dấu chấm/khoảng trắng)
//                string joined = string.Concat(nums.Select(m => m.Value));
//                if (joined.Length >= 10 && joined.Length <= 14)
//                    return joined;
//            }

//            // ── Chiến lược B: Regex multiline label + số ────────────
//            string t = Regex.Replace(text.ToUpperInvariant(), @"[ \t]+", " ").Trim();

//            var labelPatterns = new[]
//            {
//                // Label rõ + đúng 12 chữ số theo sau (ngay hoặc cách tối đa 15 ký tự)
//                @"S[O06].{0,5}T[O0].{0,5}KHAI[\s\S]{0,15}?(\d{12})\b",
//                // Label + số có thể có dấu chấm/khoảng trắng giữa
//                @"S[O06].{0,5}T[O0].{0,5}KHAI\s*[:\-]?\s*([\d][\d\. ]{8,14}[\d])",
//                @"S.{0,4}T.{0,4}KHAI\s*[:\-]?\s*([\d][\d\. ]{8,14}[\d])",
//            };

//            foreach (var pat in labelPatterns)
//            {
//                var m = Regex.Match(t, pat, RegexOptions.IgnoreCase);
//                if (!m.Success) continue;

//                string digits = Regex.Replace(m.Groups[1].Value, @"[^\d]", "");
//                if (digits.Length >= 10 && digits.Length <= 14)
//                    return digits;
//            }

//            // ── Chiến lược C: Standalone đúng 12 chữ số ────────────
//            // Không dùng 10-13 để tránh bắt nhầm mã loại hình, mã HS, v.v.
//            {
//                var m = Regex.Match(t, @"\b(\d{12})\b");
//                if (m.Success) return m.Groups[1].Value;
//            }

//            return null;
//        }

//        // ════════════════════════════════════════════════════════════
//        //  Build Excel — 3 cột, không có Ghi chú
//        // ════════════════════════════════════════════════════════════
//        private void BuildExcel(List<(string Name, string SoToKhai)> rows, string outputPath)
//        {
//            using var wb = new XLWorkbook();
//            var ws = wb.AddWorksheet("Mapping");

//            ws.Cell(1, 1).Value = "Tên file gốc";
//            ws.Cell(1, 2).Value = "Số tờ khai";
//            ws.Cell(1, 3).Value = "Tên mới (điền vào đây)";

//            var hdr = ws.Range(1, 1, 1, 3);
//            hdr.Style.Font.Bold = true;
//            hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
//            hdr.Style.Font.FontColor = XLColor.White;
//            hdr.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

//            for (int i = 0; i < rows.Count; i++)
//            {
//                int r = i + 2;
//                var (name, soToKhai) = rows[i];

//                ws.Cell(r, 1).Value = name;
//                ws.Cell(r, 2).Value = soToKhai;
//                ws.Cell(r, 3).Value = "";

//                ws.Cell(r, 3).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

//                if (soToKhai == "Unknown")
//                {
//                    ws.Cell(r, 2).Style.Font.FontColor = XLColor.Red;
//                    ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE0E0");
//                }
//            }

//            ws.Column(1).Width = 40;
//            ws.Column(2).Width = 20;
//            ws.Column(3).Width = 32;
//            ws.SheetView.Freeze(1, 0);

//            if (rows.Count > 0)
//            {
//                var dataRange = ws.Range(1, 1, rows.Count + 1, 3);
//                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
//                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
//            }

//            wb.SaveAs(outputPath);
//        }

//        // ── Đọc mapping Excel ─────────────────────────────────────────
//        private Dictionary<string, string> ReadMappingExcel(string excelPath)
//        {
//            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
//            using var wb = new XLWorkbook(excelPath);
//            var ws = wb.Worksheet(1);
//            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
//            for (int r = 2; r <= lastRow; r++)
//            {
//                string orig = ws.Cell(r, 1).GetString()?.Trim();
//                string tenMoi = ws.Cell(r, 3).GetString()?.Trim();
//                if (!string.IsNullOrEmpty(orig))
//                    result[orig] = tenMoi;
//            }
//            return result;
//        }

//        // ── Helpers ───────────────────────────────────────────────────
//        private string GetUniquePath(string folder, string fileName)
//        {
//            string path = Path.Combine(folder, fileName);
//            if (!System.IO.File.Exists(path)) return path;

//            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
//            string ext = Path.GetExtension(fileName);
//            int counter = 1;
//            while (System.IO.File.Exists(path))
//                path = Path.Combine(folder, $"{nameOnly}_{counter++}{ext}");
//            return path;
//        }

//        private string SanitizeFileName(string name)
//        {
//            if (string.IsNullOrWhiteSpace(name)) return "Unknown";
//            char[] invalid = Path.GetInvalidFileNameChars();
//            string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
//            return safe[..Math.Min(safe.Length, 150)];
//        }
//    }
//}

