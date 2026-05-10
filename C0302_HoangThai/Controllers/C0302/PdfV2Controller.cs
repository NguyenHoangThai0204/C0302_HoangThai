using Microsoft.AspNetCore.Mvc;

using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

using ClosedXML.Excel;

using Docnet.Core;
using Docnet.Core.Models;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

using Tesseract;

using System.IO.Compression;
using System.Text.RegularExpressions;

namespace C0302_HoangThai.Controllers.C0302
{
    public class PdfV2Controller : Controller
    {
        private static readonly string TessDataPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        private static readonly bool HasTesseract =
            Directory.Exists(TessDataPath);

        // =========================================================
        // GET
        // =========================================================

        [HttpGet]
        public IActionResult Upload() => View();

        // =========================================================
        // POST: Tach PDF theo day so trang do user nhap
        // =========================================================

        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult SplitByRange(IFormFile file, string pageRanges)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File khong hop le.");

            if (string.IsNullOrWhiteSpace(pageRanges))
                return BadRequest("Vui long nhap day so trang.");

            // -- 1. Parse day so -----------------------------------------
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
                return BadRequest("Day so trang khong hop le. Vi du: 1,10,15,22");
            }

            if (startPages.Count == 0 || startPages[0] != 1)
                return BadRequest("Day so trang phai bat dau bang 1.");

            string tempFolder =
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            Directory.CreateDirectory(tempFolder);

            try
            {
                // -- 2. Luu PDF goc ra disk (Docnet can path) ------------
                string pdfPath = Path.Combine(tempFolder, file.FileName);

                using (var fs = new FileStream(pdfPath, FileMode.Create))
                    file.CopyTo(fs);

                // -- 3. Lay tong so trang + build ranges -----------------
                int totalPages;

                using (var reader = new PdfReader(pdfPath))
                using (var pdfDoc = new PdfDocument(reader))
                    totalPages = pdfDoc.GetNumberOfPages();

                var ranges = new List<(int start, int end)>();

                for (int i = 0; i < startPages.Count; i++)
                {
                    int s = startPages[i];
                    int e = (i + 1 < startPages.Count)
                        ? startPages[i + 1] - 1
                        : totalPages;

                    if (s > totalPages)
                        return BadRequest(
                            $"Trang {s} vuot qua tong so trang ({totalPages}).");

                    ranges.Add((s, e));
                }

                // -- 4. Tach PDF + OCR 3 trang dau moi file con ---------
                string outFolder = Path.Combine(tempFolder, "output");
                Directory.CreateDirectory(outFolder);

                // Tuple: FileName, SoToKhai1, SoToKhai2, SoToKhai3,
                //        TenDatFile (ket qua voting), StartPage, EndPage
                var excelRows = new List<(
                    string FileName,
                    string SoToKhai1,
                    string SoToKhai2,
                    string SoToKhai3,
                    string TenDatFile,
                    int StartPage,
                    int EndPage)>();

                using (var reader = new PdfReader(pdfPath))
                using (var srcDoc = new PdfDocument(reader))
                {
                    for (int idx = 0; idx < ranges.Count; idx++)
                    {
                        var (start, end) = ranges[idx];

                        // -- 4a. Tach trang -> file PDF con --------------
                        string subPath =
                            Path.Combine(tempFolder, $"sub_{idx}.pdf");

                        using (var writer = new PdfWriter(subPath))
                        using (var subDoc = new PdfDocument(writer))
                        {
                            var merger = new PdfMerger(subDoc);
                            merger.Merge(srcDoc, start, end);
                        }

                        // -- 4b. OCR 3 trang dau -------------------------
                        string ocr1 = OcrPageFromFile(subPath, pageIndex: 0);
                        string ocr2 = OcrPageFromFile(subPath, pageIndex: 1);
                        string ocr3 = OcrPageFromFile(subPath, pageIndex: 2);

                        string soToKhai1 = ExtractSoToKhai(ocr1);
                        string soToKhai2 = ExtractSoToKhai(ocr2);
                        string soToKhai3 = ExtractSoToKhai(ocr3);

                        // -- 4c. Voting majority de chon ten file --------
                        //
                        // Quy tac:
                        //   - Gom cac ket qua khac rong
                        //   - Khong co gi -> unknown_N
                        //   - Chi 1 ket qua (bat ke tu trang nao) -> dat luon
                        //   - Nhieu ket qua -> dem tan suat (loose-similar gop chung)
                        //     -> chon cai xuat hien nhieu nhat
                        //     -> hoa nhau -> uu tien trang 1, roi trang 2, roi trang 3

                        string tenDatFile = VoteSoToKhai(
                            soToKhai1, soToKhai2, soToKhai3, idx + 1);

                        // -- 4d. Copy sang output ------------------------
                        string destName = $"{SanitizeFileName(tenDatFile)}.pdf";
                        string destPath = Path.Combine(outFolder, destName);

                        int suffix = 2;
                        while (System.IO.File.Exists(destPath))
                        {
                            destName =
                                $"{SanitizeFileName(tenDatFile)}_{suffix++}.pdf";
                            destPath = Path.Combine(outFolder, destName);
                        }

                        System.IO.File.Copy(subPath, destPath);

                        excelRows.Add((
                            destName,
                            soToKhai1, soToKhai2, soToKhai3,
                            tenDatFile,
                            start, end));

                        Console.WriteLine(
                            $"[V2] File {idx + 1}: trang {start}-{end}" +
                            $" | OCR: [{soToKhai1}] [{soToKhai2}] [{soToKhai3}]" +
                            $" => {destName}");
                    }
                }

                // -- 5. Mapping.xlsx -------------------------------------
                string excelPath = Path.Combine(outFolder, "Mapping.xlsx");
                BuildExcel(excelRows, excelPath);

                // -- 6. ZIP ----------------------------------------------
                string zipPath = Path.Combine(
                    tempFolder,
                    $"KetQua_{DateTime.Now:yyyyMMddHHmmss}.zip");

                ZipFile.CreateFromDirectory(outFolder, zipPath);

                return PhysicalFile(
                    zipPath,
                    "application/zip",
                    Path.GetFileName(zipPath));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        // =========================================================
        // POST: Doi ten PDF
        // Ghep: TenMoi - TenDatFile.pdf
        // =========================================================

        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult RenameFiles(IFormFile pdfZip, IFormFile mappingExcel)
        {
            if (pdfZip == null || mappingExcel == null)
                return BadRequest("Vui long chon du 2 file.");

            string tempFolder =
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            Directory.CreateDirectory(tempFolder);

            try
            {
                // Doc Excel mapping
                string xlPath = Path.Combine(tempFolder, "mapping.xlsx");

                using (var fs = new FileStream(xlPath, FileMode.Create))
                    mappingExcel.CopyTo(fs);

                // Dict: TenFileCu -> (TenMoi, TenDatFile)
                var mapping = new Dictionary<string, (string TenMoi, string TenDatFile)>(
                    StringComparer.OrdinalIgnoreCase);

                using (var wb = new XLWorkbook(xlPath))
                {
                    var ws = wb.Worksheet(1);
                    int row = 2;

                    while (true)
                    {
                        // Col 1: Ten file  Col 6: TenMoi  Col 7: TenDatFile
                        var cellFile = ws.Cell(row, 1).GetString().Trim();
                        var cellTenMoi = ws.Cell(row, 6).GetString().Trim();
                        var cellTenDatFile = ws.Cell(row, 7).GetString().Trim();

                        if (string.IsNullOrEmpty(cellFile)) break;

                        if (!string.IsNullOrEmpty(cellTenMoi))
                            mapping[cellFile] = (cellTenMoi, cellTenDatFile);

                        row++;
                    }
                }

                // Giai nen ZIP + doi ten + nen lai
                string extractDir = Path.Combine(tempFolder, "extracted");
                Directory.CreateDirectory(extractDir);

                string zipPath = Path.Combine(tempFolder, "upload.zip");

                using (var fs = new FileStream(zipPath, FileMode.Create))
                    pdfZip.CopyTo(fs);

                ZipFile.ExtractToDirectory(zipPath, extractDir);

                string outDir = Path.Combine(tempFolder, "renamed");
                Directory.CreateDirectory(outDir);

                foreach (var srcFile in Directory.GetFiles(extractDir))
                {
                    string originalName = Path.GetFileName(srcFile);
                    string newName;

                    if (originalName.EndsWith(".xlsx",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        newName = originalName;
                    }
                    else if (mapping.TryGetValue(originalName, out var info))
                    {
                        // TenMoi - TenDatFile.pdf
                        // Neu TenDatFile trong Excel bi bo trong -> dung ten file goc
                        string tenDatFile = string.IsNullOrEmpty(info.TenDatFile)
                            ? Path.GetFileNameWithoutExtension(originalName)
                            : info.TenDatFile;

                        newName = $"{info.TenMoi} - {tenDatFile}.pdf";
                    }
                    else
                    {
                        newName = originalName;
                    }

                    System.IO.File.Copy(
                        srcFile,
                        Path.Combine(outDir, SanitizeFileName(newName)));
                }

                string outZip = Path.Combine(
                    tempFolder,
                    $"Renamed_{DateTime.Now:yyyyMMddHHmmss}.zip");

                ZipFile.CreateFromDirectory(outDir, outZip);

                return PhysicalFile(
                    outZip,
                    "application/zip",
                    Path.GetFileName(outZip));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.ToString());
            }
        }

        // =========================================================
        // VOTING MAJORITY
        // =========================================================

        private string VoteSoToKhai(string s1, string s2, string s3, int unknownIndex)
        {
            var candidates = new List<(string Value, int Priority)>();
            if (!string.IsNullOrEmpty(s1)) candidates.Add((s1, 1));
            if (!string.IsNullOrEmpty(s2)) candidates.Add((s2, 2));
            if (!string.IsNullOrEmpty(s3)) candidates.Add((s3, 3));

            if (candidates.Count == 0)
                return $"unknown_{unknownIndex}";

            if (candidates.Count == 1)
                return candidates[0].Value;

            // Tìm cặp EXACT match (hoặc chỉ sai 1 ký tự do OCR)
            (string Rep, int BestPriority) winner = (string.Empty, int.MaxValue);

            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    if (candidates[i].Value == candidates[j].Value)
                    {
                        int bestPriority = Math.Min(candidates[i].Priority, candidates[j].Priority);
                        string rep = candidates[i].Priority <= candidates[j].Priority
                            ? candidates[i].Value
                            : candidates[j].Value;

                        if (bestPriority < winner.BestPriority)
                            winner = (rep, bestPriority);
                    }
                }
            }

            return string.IsNullOrEmpty(winner.Rep)
                ? $"unknown_{unknownIndex}"
                : winner.Rep;
        }

        // =========================================================
        // OCR 1 trang tu file PDF
        // Dung Docnet + ImageSharp + Tesseract (giong file goc)
        // =========================================================

        private string OcrPageFromFile(string pdfPath, int pageIndex)
        {
            // Thu text layer truoc (nhanh, khong can OCR)
            string textLayer = "";

            try
            {
                using var reader = new PdfReader(pdfPath);
                using var pdfDoc = new PdfDocument(reader);

                int iTextPage = pageIndex + 1; // iText7 1-based

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

            // Fallback: OCR qua Docnet + ImageSharp + Tesseract
            if (!HasTesseract)
                return textLayer;

            try
            {
                using var library = DocLib.Instance;
                using var docReader = library.GetDocReader(
                    pdfPath,
                    new PageDimensions(2067, 2924));

                if (pageIndex >= docReader.GetPageCount())
                    return textLayer;

                using var pgReader = docReader.GetPageReader(pageIndex);

                int w = pgReader.GetPageWidth();
                int h = pgReader.GetPageHeight();
                byte[] raw = pgReader.GetImage();

                // Chi OCR 22% phan dau trang (noi co so to khai)
                int cropH = (int)(h * 0.22f);

                byte[] png;

                using (var img = Image.LoadPixelData<Bgra32>(raw, w, h))
                {
                    img.Mutate(ctx => ctx
                        .Crop(new Rectangle(0, 0, w, cropH))
                        .Resize(w * 2, cropH * 2)
                        .Grayscale()
                        .Contrast(1.2f)
                        .BinaryThreshold(0.38f));

                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    png = ms.ToArray();
                }

                string lang = System.IO.File.Exists(
                    Path.Combine(TessDataPath, "vie.traineddata"))
                    ? "vie+eng"
                    : "eng";

                using var engine = new TesseractEngine(
                    TessDataPath, lang, EngineMode.LstmOnly);

                engine.SetVariable(
                    "tessedit_char_whitelist",
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

        // =========================================================
        // Tim so to khai tu OCR text (giong file goc)
        // =========================================================

        private string ExtractSoToKhai(string ocrText)
        {
            if (string.IsNullOrEmpty(ocrText))
                return string.Empty;

            string clean = NormalizeOCRText(ocrText);
            var lines = clean.Split('\n');

            var rawNumbers = new List<string>();

            // Uu tien dong co "so to khai"
            foreach (var line in lines)
            {
                bool isToKhaiLine = Regex.IsMatch(
                    line,
                    @"so\s*to\s*khai|phu\s*luc.*to\s*khai|to\s*khai\s*:?\s*[0-9]",
                    RegexOptions.IgnoreCase);

                if (!isToKhaiLine) continue;

                var nums = Regex.Matches(line, @"[0-9OIlSB]{10,15}");

                foreach (Match m in nums)
                    rawNumbers.Add(NormalizeDigits(m.Value));
            }

            // Fallback: quet toan trang (bo qua dong co nhieu)
            if (!rawNumbers.Any())
            {
                foreach (var line in lines)
                {
                    string lower = line.ToLower();

                    bool badLine =
                        lower.Contains("invoice") ||
                        lower.Contains("packing") ||
                        lower.Contains("hawb") ||
                        lower.Contains("tracking") ||
                        lower.Contains("bill to") ||
                        lower.Contains("ship to") ||
                        lower.Contains("vat") ||
                        lower.Contains("dhl") ||
                        lower.Contains("fedex") ||
                        lower.Contains("ups");

                    if (badLine) continue;

                    var nums = Regex.Matches(line, @"[0-9OIlSB]{10,15}");

                    foreach (Match m in nums)
                        rawNumbers.Add(NormalizeDigits(m.Value));
                }
            }

            foreach (var n in rawNumbers.Distinct())
            {
                if (IsValidSoToKhai(n))
                    return n;
            }

            return string.Empty;
        }

        // =========================================================
        // BuildExcel
        // Col 1: Ten file
        // Col 2: So to khai trang 1
        // Col 3: So to khai trang 2
        // Col 4: So to khai trang 3
        // Col 5: Trang bat dau
        // Col 6: TenMoi      <- nguoi dung dien
        // Col 7: TenDatFile  <- auto dien = ket qua voting (co the sua)
        // =========================================================

        private void BuildExcel(
                List<(string FileName,
                      string SoToKhai1,
                      string SoToKhai2,
                      string SoToKhai3,
                      string TenDatFile,
                      int StartPage,
                      int EndPage)> rows,
                string outputPath)
            {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Mapping");

            // Headers
            ws.Cell(1, 1).Value = "Ten file";
            ws.Cell(1, 2).Value = "So to khai (trang 1)";
            ws.Cell(1, 3).Value = "So to khai (trang 2)";
            ws.Cell(1, 4).Value = "So to khai (trang 3)";
            ws.Cell(1, 5).Value = "Trang bat dau";
            ws.Cell(1, 6).Value = "TenDatFile";          // nguoi dung dien
            ws.Cell(1, 7).Value = "TenMoi";      // = ket qua voting, co the sua

            var headerRow = ws.Row(1);
            headerRow.Style.Font.Bold = true;
            headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            headerRow.Style.Font.FontColor = XLColor.White;

            // Highlight cot nguoi dung can dien
            //ws.Column(6).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");
            //ws.Column(7).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");

            for (int i = 0; i < rows.Count; i++)
            {
                int r = i + 2;
                var row = rows[i];

                ws.Cell(r, 1).Value = row.FileName;
                ws.Cell(r, 2).Value = row.SoToKhai1;
                ws.Cell(r, 3).Value = row.SoToKhai2;
                ws.Cell(r, 4).Value = row.SoToKhai3;
                ws.Cell(r, 5).Value = row.StartPage;
                ws.Cell(r, 6).Value = row.TenDatFile;             // TenMoi: nguoi dung dien
                ws.Cell(r, 7).Value = ""; // TenDatFile: auto
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(outputPath);
        }

        // =========================================================
        // Helpers (giong file goc)
        // =========================================================

        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12) return false;

            if (!v.StartsWith("10") && !v.StartsWith("11"))
                return false;

            if (v.StartsWith("2024") || v.StartsWith("2025") || v.StartsWith("2026"))
                return false;

            if (v.StartsWith("030") || v.StartsWith("010"))
                return false;

            return true;
        }

        private bool IsLooseSimilar(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) ||
                string.IsNullOrWhiteSpace(b)) return false;

            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) diff++;

            if (diff <= 2) return true;

            bool samePrefix5 = a.Substring(0, 5) == b.Substring(0, 5);
            bool sameSuffix5 = a.Substring(a.Length - 5) == b.Substring(b.Length - 5);

            return samePrefix5 && sameSuffix5 && diff <= 3;
        }

        private string NormalizeOCRText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return Regex.Replace(text, @"[ \t]+", " ");
        }

        private string NormalizeDigits(string value)
        {
            return value
                .Replace("O", "0").Replace("o", "0")
                .Replace("I", "1").Replace("l", "1")
                .Replace("|", "1").Replace("B", "8")
                .Replace("S", "5").Replace(" ", "")
                .Replace(".", "").Replace(",", "");
        }

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown";

            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string(
                name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

            return safe[..Math.Min(safe.Length, 150)];
        }
    }
}