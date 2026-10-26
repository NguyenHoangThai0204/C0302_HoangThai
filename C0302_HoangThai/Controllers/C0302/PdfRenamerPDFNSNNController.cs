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
    public class PdfRenamerPDFNSNNController : Controller
    {
        private static readonly string TessDataPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        private static readonly bool HasTesseract =
            Directory.Exists(TessDataPath) &&
            System.IO.File.Exists(Path.Combine(TessDataPath, "eng.traineddata"));

        private static readonly object _pdfiumLock = new object();

        private const string NSNN_SUFFIX = "_NSNN";

        [HttpGet]
        public IActionResult Upload() => View();

        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult Step1_SplitAndExcel(
            IFormFile bigPdf,
            string? pageRanges,
            int? pagesToRead,
            string? readPosition)
        {
            if (bigPdf == null || bigPdf.Length == 0)
                return BadRequest("Vui lòng chọn file PDF.");
            if (!bigPdf.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest("File phải có định dạng .pdf");

            if (string.IsNullOrWhiteSpace(pageRanges))
                return BadRequest("Vui lòng nhập dãy số trang bắt đầu. VD: 1,5");

            int N = (pagesToRead.HasValue && pagesToRead.Value > 0) ? pagesToRead.Value : 3;
            if (N < 1 || N > 20)
                return BadRequest("Số trang cần đọc phải từ 1 đến 20.");

            string position = (readPosition ?? "top").Trim().ToLowerInvariant();
            if (position != "top" && position != "bottom" && position != "both")
                position = "top";

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

                List<int> startPages;
                try
                {
                    startPages = pageRanges!
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.Parse(s.Trim()))
                        .OrderBy(x => x)
                        .Distinct()
                        .ToList();
                }
                catch
                {
                    return BadRequest("Dãy số trang không hợp lệ. Ví dụ: 1,5");
                }

                if (startPages.Count == 0 || startPages[0] != 1)
                    return BadRequest("Dãy số trang phải bắt đầu bằng 1.");

                var ranges = new List<(int start, int end)>();
                for (int i = 0; i < startPages.Count; i++)
                {
                    int s = startPages[i];
                    int e = (i + 1 < startPages.Count) ? startPages[i + 1] - 1 : totalPages;

                    if (s > totalPages)
                        return BadRequest($"Trang {s} vượt quá tổng số trang ({totalPages}).");

                    ranges.Add((s, e));
                }

                var chunkFolder = Path.Combine(tempFolder, "chunks");
                Directory.CreateDirectory(chunkFolder);

                var rows = new List<(string ChunkName, List<string> Stks, string TenDatFile)>();
                var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < ranges.Count; i++)
                {
                    var (startPage, endPage) = ranges[i];
                    int chunkPageCount = endPage - startPage + 1;

                    var tempChunk = Path.Combine(chunkFolder, $"_tmp_{i}.pdf");
                    SplitPages(inputPath, tempChunk, startPage, endPage);

                    // Xác định trang đọc
                    List<int> pageIndices = new List<int>();
                    if (position == "bottom")
                    {
                        int start = Math.Max(0, chunkPageCount - N);
                        pageIndices = Enumerable.Range(start, chunkPageCount - start).ToList();
                    }
                    else if (position == "both")
                    {
                        // Đọc cả đầu và cuối, gộp lại (loại bỏ trùng)
                        int startBottom = Math.Max(0, chunkPageCount - N);
                        var bottomIndices = Enumerable.Range(startBottom, chunkPageCount - startBottom).ToList();
                        var topIndices = Enumerable.Range(0, Math.Min(N, chunkPageCount)).ToList();
                        pageIndices = topIndices.Union(bottomIndices).OrderBy(x => x).ToList();
                    }
                    else
                    {
                        // top
                        pageIndices = Enumerable.Range(0, Math.Min(N, chunkPageCount)).ToList();
                    }

                    var stks = new List<string>();
                    foreach (int p in pageIndices)
                    {
                        string ocr = OcrPageFromFile(tempChunk, p, position);
                        stks.Add(ExtractSoToKhai(ocr));
                    }
                    while (stks.Count < N) stks.Add("");

                    string votedStk = VoteSoToKhai(stks, i + 1);
                    string tenDatFile = votedStk + NSNN_SUFFIX;

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

                    rows.Add((chunkName, stks, tenDatFile));
                    GC.Collect(0, GCCollectionMode.Optimized);
                }

                string excelPath = Path.Combine(tempFolder, "Mapping.xlsx");
                BuildExcel(rows, N, position, excelPath);

                var outZipFolder = Path.Combine(tempFolder, "for_zip");
                Directory.CreateDirectory(outZipFolder);

                foreach (var f in Directory.GetFiles(chunkFolder))
                    System.IO.File.Copy(f, Path.Combine(outZipFolder, Path.GetFileName(f)));
                System.IO.File.Copy(excelPath, Path.Combine(outZipFolder, "Mapping.xlsx"));

                string zipPath = Path.Combine(tempFolder, "Step1_Output.zip");
                ZipFile.CreateFromDirectory(outZipFolder, zipPath);

                return PhysicalFile(zipPath, "application/zip",
                    $"Step1_NSNN_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            }
            catch (Exception ex)
            {
                try { Directory.Delete(tempFolder, true); } catch { }
                return BadRequest($"Lỗi xử lý: {ex.Message}");
            }
        }

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
                var pdfFolder = Path.Combine(tempFolder, "pdfs");
                Directory.CreateDirectory(pdfFolder);
                var zipPath = Path.Combine(tempFolder, "input.zip");
                using (var fs = new FileStream(zipPath, FileMode.Create)) pdfZip.CopyTo(fs);
                ZipFile.ExtractToDirectory(zipPath, pdfFolder, overwriteFiles: true);

                var excelPath = Path.Combine(tempFolder, "mapping.xlsx");
                using (var fs = new FileStream(excelPath, FileMode.Create)) mappingExcel.CopyTo(fs);

                var mapping = new Dictionary<string, (string TenMoi, string TenDatFile)>(
                    StringComparer.OrdinalIgnoreCase);

                using (var wb = new XLWorkbook(excelPath))
                {
                    var ws = wb.Worksheet("Mapping");
                    int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
                    int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

                    int colFileName = -1, colTenDatFile = -1, colTenMoi = -1;

                    for (int c = 1; c <= lastCol; c++)
                    {
                        string h = ws.Cell(1, c).GetString()?.Trim() ?? "";
                        if (h.StartsWith("Tên file", StringComparison.OrdinalIgnoreCase)) colFileName = c;
                        if (h.StartsWith("TenDatFile", StringComparison.OrdinalIgnoreCase)) colTenDatFile = c;
                        if (h.StartsWith("TenMoi", StringComparison.OrdinalIgnoreCase)) colTenMoi = c;
                    }

                    if (colFileName < 0 || colTenDatFile < 0 || colTenMoi < 0)
                        return BadRequest("File Excel không đúng định dạng.");

                    for (int r = 2; r <= lastRow; r++)
                    {
                        string orig = ws.Cell(r, colFileName).GetString()?.Trim();
                        string tenDatFile = ws.Cell(r, colTenDatFile).GetString()?.Trim();
                        string tenMoi = ws.Cell(r, colTenMoi).GetString()?.Trim();

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
                        continue;

                    string newName;
                    if (mapping.TryGetValue(originalName, out var info))
                    {
                        string tenDatFile = string.IsNullOrWhiteSpace(info.TenDatFile)
                            ? Path.GetFileNameWithoutExtension(originalName)
                            : info.TenDatFile;

                        if (!string.IsNullOrWhiteSpace(info.TenMoi))
                            newName = $"{info.TenMoi}_{tenDatFile}.pdf";
                        else
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
                    $"Renamed_NSNN_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            }
            catch (Exception ex)
            {
                try { Directory.Delete(tempFolder, true); } catch { }
                return BadRequest($"Lỗi: {ex.Message}");
            }
        }

        private void SplitPages(string sourcePath, string destPath, int startPage, int endPage)
        {
            using var reader = new PdfReader(sourcePath);
            using var srcDoc = new PdfDocument(reader);
            using var writer = new PdfWriter(destPath);
            using var dstDoc = new PdfDocument(writer);
            srcDoc.CopyPagesTo(startPage, endPage, dstDoc);
        }

        private string OcrPageFromFile(string pdfPath, int pageIndex, string position)
        {
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

            byte[] raw = null;
            int w = 0, h = 0;

            lock (_pdfiumLock)
            {
                try
                {
                    var library = DocLib.Instance;
                    using var docReader = library.GetDocReader(pdfPath, new PageDimensions(2067, 2924));

                    if (pageIndex >= docReader.GetPageCount())
                        return textLayer;

                    using var pgReader = docReader.GetPageReader(pageIndex);
                    w = pgReader.GetPageWidth();
                    h = pgReader.GetPageHeight();
                    raw = pgReader.GetImage();
                }
                catch
                {
                    return textLayer;
                }
            }

            if (raw == null || w <= 0 || h <= 0)
                return textLayer;

            try
            {
                // FIX: không crop theo position nữa.
                // "position" chỉ quyết định ĐỌC TRANG NÀO (đầu/cuối file),
                // còn số tờ khai có thể nằm ở bất kỳ vị trí nào trong trang
                // (tờ khai: ~8% từ trên, chứng từ ghi sổ: ~25%, giấy nộp tiền: ~50%)
                // => OCR toàn trang.
                int cropY = 0;
                int cropH = h;
                if (cropH < 1) cropH = 1;

                byte[] png;
                using (var img = Image.LoadPixelData<Bgra32>(raw, w, h))
                {
                    img.Mutate(ctx => ctx
                        .Crop(new Rectangle(0, cropY, w, cropH))
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

        private string ExtractSoToKhai(string ocrText)
        {
            if (string.IsNullOrEmpty(ocrText)) return string.Empty;

            string clean = NormalizeOCRText(ocrText);

            // FIX: bỏ "Số tờ khai đầu tiên <số>" (tờ khai chia nhỏ/tách luồng có 2 số trên
            // cùng 1 dòng: số tờ khai thật + số tờ khai đầu tiên). Nếu không bỏ, OCR đọc
            // "tờ" thành "td" nên nhãn thứ nhất không khớp, nhãn thứ hai khớp => lấy nhầm.
            clean = Regex.Replace(clean,
                @"(?:d[aâ]u|đầu)\s*ti[eê]n[ :.\-]*[0-9OIlSB]{11,12}", " ",
                RegexOptions.IgnoreCase);

            // 1. Neo vào chữ "khai" (chữ "Số", "tờ" hay bị OCR sai dấu nên không dùng).
            // Lấy số đứng NGAY SAU chữ "khai", theo thứ tự từ trên xuống: số đầu tiên hợp lệ
            // chính là số tờ khai. "khai đầu tiên <số>" có chữ "đầu tiên" chen giữa nên
            // không khớp. Cho phép OCR đọc "khai" thành "khal"/"kha1".
            foreach (Match lm in Regex.Matches(clean,
                @"kha[il1][ :.\-]{0,5}([0-9OIlSB]{11,12})\b", RegexOptions.IgnoreCase))
            {
                string stk = PadStk(NormalizeDigits(lm.Groups[1].Value));
                if (IsValidSoToKhai(stk)) return stk;
            }

            // 2. Tìm số đứng ngay trước ngày tháng (VD: 11087171980013/10/2026)
            // iText thường nối STT + STK + Ngày thành 1 chuỗi số dài
            var dateMatch = Regex.Match(clean, @"\b([0-9OIlSB]{11,12})\d{2}/\d{2}/\d{4}", RegexOptions.IgnoreCase);
            if (dateMatch.Success)
            {
                string stk = PadStk(NormalizeDigits(dateMatch.Groups[1].Value));
                if (IsValidSoToKhai(stk)) return stk;
            }

            // 3. Tìm CDF No
            var cdfMatch = Regex.Match(clean, @"cdf\s*no[\s\S]{0,20}?\b([0-9OIlSB]{11,12})\b", RegexOptions.IgnoreCase);
            if (cdfMatch.Success)
            {
                string stk = PadStk(NormalizeDigits(cdfMatch.Groups[1].Value));
                if (IsValidSoToKhai(stk)) return stk;
            }

            // 4. Fallback: Quét toàn bộ text tìm số 11-12 chữ số (bỏ qua các số quen thuộc như MST)
            // FIX: ưu tiên số bắt đầu bằng 10/11 (số tờ khai hải quan) để tránh
            // nhầm với mã vận đơn FedEx/DHL (VD: 384066982672) trên trang biên bản.
            var candidates = Regex.Matches(clean, @"\b([0-9OIlSB]{11,12})\b")
                .Cast<Match>()
                .Select(m => PadStk(NormalizeDigits(m.Value)))
                .Where(IsValidSoToKhai)
                .ToList();

            var preferred = candidates.FirstOrDefault(c => c.StartsWith("10") || c.StartsWith("11"));
            if (preferred != null) return preferred;

            return candidates.FirstOrDefault() ?? string.Empty;
        }

        private string VoteSoToKhai(List<string> stks, int unknownIndex)
        {
            var candidates = new List<(string Value, int Priority)>();
            for (int i = 0; i < stks.Count; i++)
                if (!string.IsNullOrEmpty(stks[i]))
                    candidates.Add((stks[i], i + 1));

            if (candidates.Count == 0) return $"unknown_{unknownIndex}";
            if (candidates.Count == 1) return candidates[0].Value;

            var groups = new List<(string Rep, List<int> Priorities)>();
            foreach (var (value, priority) in candidates)
            {
                bool merged = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    if (IsLooseSimilar(groups[g].Rep, value))
                    {
                        groups[g].Priorities.Add(priority);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    groups.Add((value, new List<int> { priority }));
            }

            var best = groups
                .OrderByDescending(g => g.Priorities.Count)
                .ThenBy(g => g.Priorities.Min())
                .First();

            return best.Rep;
        }

        private bool IsLooseSimilar(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) diff++;

            if (diff <= 2) return true;

            bool samePrefix5 = a.Substring(0, 5) == b.Substring(0, 5);
            bool sameSuffix5 = a.Substring(a.Length - 5) == b.Substring(b.Length - 5);
            return samePrefix5 && sameSuffix5 && diff <= 3;
        }

        private void BuildExcel(
            List<(string ChunkName, List<string> Stks, string TenDatFile)> rows,
            int N, string position, string outputPath)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            int colTenDatFile = N + 2;
            int colTenMoi = N + 3;
            int lastCol = colTenMoi;

            ws.Cell(1, 1).Value = "Tên file gốc (chunk)";
            for (int i = 0; i < N; i++)
                ws.Cell(1, 2 + i).Value = $"Số tờ khai #{i + 1}";
            ws.Cell(1, colTenDatFile).Value = "TenDatFile (voting)";
            ws.Cell(1, colTenMoi).Value = "TenMoi (để trống = dùng TenDatFile)";

            var hdr = ws.Range(1, 1, 1, lastCol);
            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            hdr.Style.Font.FontColor = XLColor.White;
            hdr.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            hdr.Style.Alignment.WrapText = true;

            for (int r = 0; r < rows.Count; r++)
            {
                int row = r + 2;
                var (name, stks, tenDatFile) = rows[r];

                ws.Cell(row, 1).Value = name;
                for (int i = 0; i < N; i++)
                    ws.Cell(row, 2 + i).Value = i < stks.Count ? stks[i] : "";
                ws.Cell(row, colTenDatFile).Value = tenDatFile;
                ws.Cell(row, colTenMoi).Value = "";

                ws.Cell(row, colTenMoi).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");

                bool allEmpty = stks.All(string.IsNullOrEmpty);
                if (allEmpty || tenDatFile.StartsWith("unknown_"))
                {
                    ws.Cell(row, colTenDatFile).Style.Font.FontColor = XLColor.Red;
                    ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE0E0");
                }
            }

            ws.Column(1).Width = 28;
            for (int i = 0; i < N; i++)
                ws.Column(2 + i).Width = 20;
            ws.Column(colTenDatFile).Width = 22;
            ws.Column(colTenMoi).Width = 36;
            ws.SheetView.Freeze(1, 1);

            if (rows.Count > 0)
            {
                var dataRange = ws.Range(1, 1, rows.Count + 1, lastCol);
                dataRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                dataRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            }

            var info = wb.AddWorksheet("Info");
            info.Cell(1, 1).Value = "Chế độ đọc:";
            info.Cell(1, 2).Value = position switch
            {
                "bottom" => "Đọc từ CUỐI file",
                "both" => "Đọc cả ĐẦU & CUỐI file",
                _ => "Đọc từ ĐẦU file"
            };
            info.Cell(2, 1).Value = "Số trang đọc mỗi file:";
            info.Cell(2, 2).Value = N;
            info.Cell(3, 1).Value = "Tổng số file:";
            info.Cell(3, 2).Value = rows.Count;
            info.Cell(4, 1).Value = "Thời gian:";
            info.Cell(4, 2).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            wb.SaveAs(outputPath);
        }

        // Chuẩn hóa độ dài STK thành 12 chữ số
        private string PadStk(string v)
        {
            // Nếu là 11 số và bắt đầu bằng 10 hoặc 11 -> tự động thêm số 0 vào cuối
            if (v.Length == 11 && (v.StartsWith("10") || v.StartsWith("11")))
                return v + "0";
            return v;
        }

        // Bắt buộc STK phải đúng 12 chữ số
        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12) return false;
            if (v.StartsWith("2024") || v.StartsWith("2025") || v.StartsWith("2026")) return false;
            if (v.StartsWith("0000")) return false;
            if (v.StartsWith("030") || v.StartsWith("010")) return false;
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