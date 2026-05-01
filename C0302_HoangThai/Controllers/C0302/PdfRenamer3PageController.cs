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

        [HttpGet]
        public IActionResult Upload() => View();

        // ════════════════════════════════════════════════════════════
        //  BƯỚC 1: PDF/ZIP → Excel mapping
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult Step1_ExportExcel(List<IFormFile> pdfFiles, IFormFile zipFile)
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            try
            {
                var pdfFolder = Path.Combine(tempFolder, "pdfs");
                Directory.CreateDirectory(pdfFolder);

                var allPdfs = new List<string>();

                if (zipFile != null && zipFile.Length > 0)
                {
                    var zipPath = Path.Combine(tempFolder, "input.zip");
                    using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                        zipFile.CopyTo(fs);
                    ZipFile.ExtractToDirectory(zipPath, pdfFolder, overwriteFiles: true);
                    allPdfs.AddRange(Directory.GetFiles(pdfFolder, "*.pdf", SearchOption.AllDirectories));
                }

                if (pdfFiles != null)
                {
                    foreach (var f in pdfFiles)
                    {
                        if (f == null || f.Length == 0) continue;
                        if (!f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
                        string safeName = Path.GetFileName(f.FileName);
                        string dest = GetUniquePath(pdfFolder, safeName);
                        using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                            f.CopyTo(fs);
                        allPdfs.Add(dest);
                    }
                }

                if (allPdfs.Count == 0)
                    return BadRequest("Không tìm thấy file PDF nào.");

                // ✅ Chỉ 2 cột — bỏ Ghi chú
                var rows = new List<(string Name, string SoToKhai)>();

                foreach (var pdfPath in allPdfs)
                {
                    string name = Path.GetFileName(pdfPath);
                    try
                    {
                        string soToKhai = ExtractSoToKhai(pdfPath);
                        rows.Add((name, soToKhai));
                    }
                    catch
                    {
                        rows.Add((name, "Unknown"));
                    }
                    finally
                    {
                        GC.Collect(0, GCCollectionMode.Optimized);
                    }
                }

                string excelPath = Path.Combine(tempFolder, "Mapping.xlsx");
                BuildExcel(rows, excelPath);

                // ✅ Không dùng cookie — JS fetch+blob tự xử lý
                string dlName = $"Mapping_SoToKhai_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                return PhysicalFile(excelPath,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    dlName);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(tempFolder, true); } catch { }
                return BadRequest($"Lỗi xử lý: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════
        //  BƯỚC 2: ZIP PDF + Excel → ZIP PDF đổi tên
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult Step2_RenameFiles(IFormFile pdfZip, IFormFile mappingExcel)
        {
            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempFolder);

            try
            {
                if (pdfZip == null || pdfZip.Length == 0)
                    return BadRequest("Thiếu file ZIP chứa PDF.");
                if (mappingExcel == null || mappingExcel.Length == 0)
                    return BadRequest("Thiếu file Excel mapping.");

                var pdfFolder = Path.Combine(tempFolder, "pdfs");
                Directory.CreateDirectory(pdfFolder);
                var zipPath = Path.Combine(tempFolder, "input.zip");
                using (var fs = new FileStream(zipPath, FileMode.Create)) pdfZip.CopyTo(fs);
                ZipFile.ExtractToDirectory(zipPath, pdfFolder, overwriteFiles: true);

                var excelPath = Path.Combine(tempFolder, "mapping.xlsx");
                using (var fs = new FileStream(excelPath, FileMode.Create)) mappingExcel.CopyTo(fs);
                var mapping = ReadMappingExcel(excelPath);

                var outFolder = Path.Combine(tempFolder, "renamed");
                Directory.CreateDirectory(outFolder);

                foreach (var kvp in mapping)
                {
                    string origName = kvp.Key;
                    string tenMoi = kvp.Value?.Trim();

                    var srcPath = Directory.GetFiles(pdfFolder, origName, SearchOption.AllDirectories)
                                           .FirstOrDefault();
                    if (srcPath == null) continue;

                    string finalName = string.IsNullOrWhiteSpace(tenMoi)
                        ? Path.GetFileNameWithoutExtension(origName)
                        : tenMoi;

                    string dest = GetUniquePath(outFolder, $"{SanitizeFileName(finalName)}.pdf");
                    System.IO.File.Copy(srcPath, dest, true);
                }

                string resultZip = Path.Combine(tempFolder, "renamed.zip");
                ZipFile.CreateFromDirectory(outFolder, resultZip);

                // ✅ Không dùng cookie
                string dlName = $"Renamed_PDFs_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                return PhysicalFile(resultZip, "application/zip", dlName);
            }
            catch (Exception ex)
            {
                try { Directory.Delete(tempFolder, true); } catch { }
                return BadRequest($"Lỗi: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════
        //  CORE: Đọc Số tờ khai từ PDF scan ảnh
        // ════════════════════════════════════════════════════════════
        private string ExtractSoToKhai(string pdfPath)
        {
            // ── 1. Thử text layer trước (PDF có text) ─────────────────
            try
            {
                using var reader = new iText.Kernel.Pdf.PdfReader(pdfPath);
                using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader);
                string text = PdfTextExtractor.GetTextFromPage(
                    pdfDoc.GetPage(1), new SimpleTextExtractionStrategy()) ?? "";

                if (Regex.Replace(text, @"\s", "").Length >= 20)
                {
                    string found = FindSoToKhaiInText(text);
                    if (found != null) return found;
                }
            }
            catch { }

            // ── 2. OCR cho PDF scan ảnh ───────────────────────────────
            if (!HasTesseract) return "Unknown";

            try
            {
                using var library = DocLib.Instance;
                // Render 250 DPI để OCR rõ hơn
                using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2067, 2924));
                using var pgReader = docReader.GetPageReader(0);

                int w = pgReader.GetPageWidth();
                int h = pgReader.GetPageHeight();
                byte[] raw = pgReader.GetImage();

                // ✅ Crop top 25% — đủ bắt dòng "Số tờ khai" kể cả layout có tiêu đề lớn
                int cropH = (int)(h * 0.25f);

                byte[] png;
                using (var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(raw, w, h))
                {
                    img.Mutate(ctx => ctx
                        .Crop(new Rectangle(0, 0, w, Math.Max(1, cropH)))
                        .Resize(w * 2, cropH * 2)   // phóng 2x cho OCR chính xác
                        .Grayscale()
                        .BinaryThreshold(0.50f));    // threshold 0.50 cho scan màu hồng

                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    png = ms.ToArray();
                }

                // ✅ Dùng "vie" (tiếng Việt) thay "eng" để OCR label tiếng Việt đúng hơn
                // Nếu chưa có vie.traineddata thì fallback về eng
                string lang = System.IO.File.Exists(Path.Combine(TessDataPath, "vie.traineddata"))
                    ? "vie+eng"
                    : "eng";

                using var engine = new TesseractEngine(TessDataPath, lang, EngineMode.LstmOnly);

                // Chỉ cho phép ký tự số + chữ cái + dấu phổ biến để OCR nhanh và sạch hơn
                engine.SetVariable("tessedit_char_whitelist",
                    "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz :-./\n");

                using var pix = Pix.LoadFromMemory(png);
                using var pg = engine.Process(pix, PageSegMode.Auto);

                string ocrText = pg.GetText() ?? "";
                string result = FindSoToKhaiInText(ocrText);
                return result ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        // ════════════════════════════════════════════════════════════
        //  Tìm Số tờ khai trong text (text layer hoặc OCR)
        //
        //  Tờ khai VN chuẩn:
        //    "Số tờ khai   308278270660   Số tờ khai đầu tiên"
        //  Số tờ khai = đúng 12 chữ số liên tiếp
        //
        //  Chiến lược:
        //   A. Tìm theo từng dòng chứa label "Số tờ khai" → lấy số 12 chữ số
        //      trên chính dòng đó (tránh bắt nhầm số ở dòng khác)
        //   B. Regex multiline label + số kế tiếp
        //   C. Standalone đúng 12 chữ số (KHÔNG dùng 10-13 để tránh nhầm)
        // ════════════════════════════════════════════════════════════
        private string FindSoToKhaiInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // ── Chiến lược A: Tìm theo từng dòng ────────────────────
            // iText và OCR thường xuất mỗi ô thành 1 dòng riêng
            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length < 3) continue;

                string lineUp = Regex.Replace(line.ToUpperInvariant(), @"[ \t]+", " ");

                // Dòng phải chứa label dạng "S? T? KHAI" (tiếng Việt hoặc OCR nhòe)
                bool hasLabel = Regex.IsMatch(lineUp,
                    @"S[O06\u1ED0\u00D4\u1ED2\u1ED4\u1ED6\u1ED8].{0,5}T[O0\u1EDD\u1EDF\u1EE1\u1EE3\u1EDB\u01A0].{0,5}KHAI",
                    RegexOptions.IgnoreCase);

                // Fallback: dòng chứa "SO TO KHAI" hoặc biến thể OCR
                if (!hasLabel)
                    hasLabel = Regex.IsMatch(lineUp, @"S.{0,3}T.{0,3}KHAI", RegexOptions.IgnoreCase);

                if (!hasLabel) continue;

                // Lấy tất cả chuỗi số trên dòng đó
                var nums = Regex.Matches(lineUp, @"\d+");

                // Ưu tiên: số đúng 12 chữ số
                foreach (Match nm in nums)
                    if (nm.Value.Length == 12)
                        return nm.Value;

                // Ghép tất cả số lại (trường hợp OCR tách số bằng dấu chấm/khoảng trắng)
                string joined = string.Concat(nums.Select(m => m.Value));
                if (joined.Length >= 10 && joined.Length <= 14)
                    return joined;
            }

            // ── Chiến lược B: Regex multiline label + số ────────────
            string t = Regex.Replace(text.ToUpperInvariant(), @"[ \t]+", " ").Trim();

            var labelPatterns = new[]
            {
                // Label rõ + đúng 12 chữ số theo sau (ngay hoặc cách tối đa 15 ký tự)
                @"S[O06].{0,5}T[O0].{0,5}KHAI[\s\S]{0,15}?(\d{12})\b",
                // Label + số có thể có dấu chấm/khoảng trắng giữa
                @"S[O06].{0,5}T[O0].{0,5}KHAI\s*[:\-]?\s*([\d][\d\. ]{8,14}[\d])",
                @"S.{0,4}T.{0,4}KHAI\s*[:\-]?\s*([\d][\d\. ]{8,14}[\d])",
            };

            foreach (var pat in labelPatterns)
            {
                var m = Regex.Match(t, pat, RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                string digits = Regex.Replace(m.Groups[1].Value, @"[^\d]", "");
                if (digits.Length >= 10 && digits.Length <= 14)
                    return digits;
            }

            // ── Chiến lược C: Standalone đúng 12 chữ số ────────────
            // Không dùng 10-13 để tránh bắt nhầm mã loại hình, mã HS, v.v.
            {
                var m = Regex.Match(t, @"\b(\d{12})\b");
                if (m.Success) return m.Groups[1].Value;
            }

            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  Build Excel — 3 cột, không có Ghi chú
        // ════════════════════════════════════════════════════════════
        private void BuildExcel(List<(string Name, string SoToKhai)> rows, string outputPath)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            ws.Cell(1, 1).Value = "Tên file gốc";
            ws.Cell(1, 2).Value = "Số tờ khai";
            ws.Cell(1, 3).Value = "Tên mới (điền vào đây)";

            var hdr = ws.Range(1, 1, 1, 3);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            hdr.Style.Font.FontColor = XLColor.White;
            hdr.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            for (int i = 0; i < rows.Count; i++)
            {
                int r = i + 2;
                var (name, soToKhai) = rows[i];

                ws.Cell(r, 1).Value = name;
                ws.Cell(r, 2).Value = soToKhai;
                ws.Cell(r, 3).Value = "";

                ws.Cell(r, 3).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

                if (soToKhai == "Unknown")
                {
                    ws.Cell(r, 2).Style.Font.FontColor = XLColor.Red;
                    ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE0E0");
                }
            }

            ws.Column(1).Width = 40;
            ws.Column(2).Width = 20;
            ws.Column(3).Width = 32;
            ws.SheetView.Freeze(1, 0);

            if (rows.Count > 0)
            {
                var dataRange = ws.Range(1, 1, rows.Count + 1, 3);
                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            wb.SaveAs(outputPath);
        }

        // ── Đọc mapping Excel ─────────────────────────────────────────
        private Dictionary<string, string> ReadMappingExcel(string excelPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var wb = new XLWorkbook(excelPath);
            var ws = wb.Worksheet(1);
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= lastRow; r++)
            {
                string orig = ws.Cell(r, 1).GetString()?.Trim();
                string tenMoi = ws.Cell(r, 3).GetString()?.Trim();
                if (!string.IsNullOrEmpty(orig))
                    result[orig] = tenMoi;
            }
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────
        private string GetUniquePath(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            if (!System.IO.File.Exists(path)) return path;

            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;
            while (System.IO.File.Exists(path))
                path = Path.Combine(folder, $"{nameOnly}_{counter++}{ext}");
            return path;
        }

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return safe[..Math.Min(safe.Length, 150)];
        }
    }
}