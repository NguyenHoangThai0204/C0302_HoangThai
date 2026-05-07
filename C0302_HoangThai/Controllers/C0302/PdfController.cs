using Microsoft.AspNetCore.Http;
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
    public class PdfController : Controller
    {
        private static readonly string TessDataPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        private static readonly bool HasTesseract =
            Directory.Exists(TessDataPath) &&
            (
                System.IO.File.Exists(Path.Combine(TessDataPath, "eng.traineddata")) ||
                System.IO.File.Exists(Path.Combine(TessDataPath, "vie.traineddata"))
            );

        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

        // ════════════════════════════════════════════════
        // STEP 1
        // Upload PDF lớn
        // → Tách PDF
        // → Xuất ZIP + Excel mapping
        // ════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult SplitPdf(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("File không hợp lệ");

            string tempFolder =
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            Directory.CreateDirectory(tempFolder);

            try
            {
                string pdfPath =
                    Path.Combine(tempFolder, file.FileName);

                using (var fs = new FileStream(pdfPath, FileMode.Create))
                {
                    file.CopyTo(fs);
                }

                Dictionary<string, List<int>> groups = new();

                string currentSoToKhai = null;

                using (PdfReader reader = new PdfReader(pdfPath))
                using (PdfDocument pdfDoc = new PdfDocument(reader))
                {
                    int totalPages = pdfDoc.GetNumberOfPages();

                    for (int i = 1; i <= totalPages; i++)
                    {
                        string soToKhai =
                            ExtractSoToKhaiFromPage(pdfPath, i);

                        // có số tờ khai => file mới
                        if (!string.IsNullOrWhiteSpace(soToKhai))
                        {
                            currentSoToKhai = soToKhai;
                        }

                        if (string.IsNullOrWhiteSpace(currentSoToKhai))
                        {
                            currentSoToKhai = "Unknown";
                        }

                        if (!groups.ContainsKey(currentSoToKhai))
                        {
                            groups[currentSoToKhai] = new List<int>();
                        }

                        groups[currentSoToKhai].Add(i);

                        Console.WriteLine($"Page {i} => {currentSoToKhai}");
                    }

                    string outFolder =
                        Path.Combine(tempFolder, "output");

                    Directory.CreateDirectory(outFolder);

                    var excelRows =
                        new List<(string FileName, string SoToKhai)>();

                    // ─────────────────────────────
                    // tạo từng file pdf
                    // ─────────────────────────────
                    foreach (var group in groups)
                    {
                        string soToKhai = group.Key;

                        string safeName =
                            SanitizeFileName(soToKhai);

                        string outPdf =
                            Path.Combine(outFolder, $"{safeName}.pdf");

                        using (PdfWriter writer = new PdfWriter(outPdf))
                        using (PdfDocument newPdf = new PdfDocument(writer))
                        {
                            PdfMerger merger = new PdfMerger(newPdf);

                            foreach (int pageNum in group.Value)
                            {
                                merger.Merge(pdfDoc, pageNum, pageNum);
                            }
                        }

                        excelRows.Add(($"{safeName}.pdf", soToKhai));
                    }

                    // ─────────────────────────────
                    // tạo excel
                    // ─────────────────────────────
                    string excelPath =
                        Path.Combine(outFolder, "Mapping.xlsx");

                    BuildExcel(excelRows, excelPath);

                    // ─────────────────────────────
                    // zip
                    // ─────────────────────────────
                    string zipPath =
                        Path.Combine(
                            tempFolder,
                            $"KetQua_{DateTime.Now:yyyyMMddHHmmss}.zip");

                    ZipFile.CreateFromDirectory(outFolder, zipPath);

                    return PhysicalFile(
                        zipPath,
                        "application/zip",
                        Path.GetFileName(zipPath));
                }
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // ════════════════════════════════════════════════
        // STEP 2
        // Upload ZIP + Excel
        // → Đổi tên PDF
        // ════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult RenameFiles(
            IFormFile pdfZip,
            IFormFile mappingExcel)
        {
            var tempFolder =
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            Directory.CreateDirectory(tempFolder);

            try
            {
                if (pdfZip == null || pdfZip.Length == 0)
                    return BadRequest("Thiếu file ZIP");

                if (mappingExcel == null || mappingExcel.Length == 0)
                    return BadRequest("Thiếu file Excel");

                // ─────────────────────────────
                // unzip
                // ─────────────────────────────
                string zipPath =
                    Path.Combine(tempFolder, "input.zip");

                using (var fs = new FileStream(zipPath, FileMode.Create))
                {
                    pdfZip.CopyTo(fs);
                }

                string extractFolder =
                    Path.Combine(tempFolder, "extract");

                Directory.CreateDirectory(extractFolder);

                ZipFile.ExtractToDirectory(
                    zipPath,
                    extractFolder,
                    true);

                // ─────────────────────────────
                // save excel
                // ─────────────────────────────
                string excelPath =
                    Path.Combine(tempFolder, "mapping.xlsx");

                using (var fs = new FileStream(excelPath, FileMode.Create))
                {
                    mappingExcel.CopyTo(fs);
                }

                // ─────────────────────────────
                // đọc excel
                // ─────────────────────────────
                using var wb = new XLWorkbook(excelPath);

                var ws = wb.Worksheet(1);

                int lastRow =
                    ws.LastRowUsed()?.RowNumber() ?? 1;

                string outputFolder =
                    Path.Combine(tempFolder, "output");

                Directory.CreateDirectory(outputFolder);

                for (int r = 2; r <= lastRow; r++)
                {
                    string oldFile =
                        ws.Cell(r, 1).GetString().Trim();

                    string soToKhai =
                        ws.Cell(r, 2).GetString().Trim();

                    string tenMoi =
                        ws.Cell(r, 3).GetString().Trim();

                    if (string.IsNullOrWhiteSpace(oldFile))
                        continue;

                    string src =
                        Directory.GetFiles(
                            extractFolder,
                            oldFile,
                            SearchOption.AllDirectories)
                        .FirstOrDefault();

                    if (src == null)
                        continue;

                    string finalName;

                    if (string.IsNullOrWhiteSpace(tenMoi))
                    {
                        finalName = oldFile;
                    }
                    else
                    {
                        finalName =
                            $"{tenMoi}_{soToKhai}.pdf";
                    }

                    string dest =
                        Path.Combine(
                            outputFolder,
                            SanitizeFileName(finalName));

                    System.IO.File.Copy(src, dest, true);
                }

                // ─────────────────────────────
                // zip output
                // ─────────────────────────────
                string outZip =
                    Path.Combine(
                        tempFolder,
                        $"Renamed_{DateTime.Now:yyyyMMddHHmmss}.zip");

                ZipFile.CreateFromDirectory(outputFolder, outZip);

                return PhysicalFile(
                    outZip,
                    "application/zip",
                    Path.GetFileName(outZip));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        // ════════════════════════════════════════════════
        // OCR đọc số tờ khai
        // ════════════════════════════════════════════════
        private string ExtractSoToKhaiFromPage(
            string pdfPath,
            int pageNumber)
        {
            // thử text layer
            try
            {
                using var reader = new PdfReader(pdfPath);

                using var pdfDoc = new PdfDocument(reader);

                string text =
                    PdfTextExtractor.GetTextFromPage(
                        pdfDoc.GetPage(pageNumber),
                        new SimpleTextExtractionStrategy()) ?? "";

                string found = FindSoToKhai(text);

                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }
            catch
            {
            }

            // OCR
            if (!HasTesseract)
                return null;

            try
            {
                using var library = DocLib.Instance;

                using var docReader =
                    library.GetDocReader(
                        pdfPath,
                        new PageDimensions(2067, 2924));

                using var pgReader =
                    docReader.GetPageReader(pageNumber - 1);

                int w = pgReader.GetPageWidth();
                int h = pgReader.GetPageHeight();

                byte[] raw = pgReader.GetImage();

                int cropH = (int)(h * 0.25f);

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

                string lang =
                    System.IO.File.Exists(Path.Combine(TessDataPath, "vie.traineddata"))
                    ? "vie+eng"
                    : "eng";

                using var engine =
                    new TesseractEngine(
                        TessDataPath,
                        lang,
                        EngineMode.LstmOnly);

                engine.SetVariable(
                    "tessedit_char_whitelist",
                    "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz :-./\n");

                using var pix = Pix.LoadFromMemory(png);

                using var page =
                    engine.Process(pix, PageSegMode.Auto);

                string ocrText = page.GetText() ?? "";

                return FindSoToKhai(ocrText);
            }
            catch
            {
                return null;
            }
        }

        // ════════════════════════════════════════════════
        // FIND SỐ TỜ KHAI
        // ════════════════════════════════════════════════
        private string FindSoToKhai(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string t = text.ToUpperInvariant();

            foreach (var rawLine in t.Split('\n'))
            {
                string line = rawLine.Trim();

                bool hasLabel =
                    Regex.IsMatch(
                        line,
                        @"S.{0,3}T.{0,3}KHAI",
                        RegexOptions.IgnoreCase);

                if (!hasLabel)
                    continue;

                var m =
                    Regex.Match(line, @"\b(\d{12})\b");

                if (m.Success)
                    return m.Groups[1].Value;
            }

            return null;
        }

        // ════════════════════════════════════════════════
        // BUILD EXCEL
        // ════════════════════════════════════════════════
        private void BuildExcel(
            List<(string FileName, string SoToKhai)> rows,
            string outputPath)
        {
            using var wb = new XLWorkbook();

            var ws = wb.AddWorksheet("Mapping");

            ws.Cell(1, 1).Value = "Tên file cũ";
            ws.Cell(1, 2).Value = "Số tờ khai";
            ws.Cell(1, 3).Value = "Tên mới";

            var hdr = ws.Range(1, 1, 1, 3);

            hdr.Style.Font.Bold = true;
            hdr.Style.Fill.BackgroundColor =
                XLColor.FromHtml("#4472C4");

            hdr.Style.Font.FontColor = XLColor.White;

            for (int i = 0; i < rows.Count; i++)
            {
                int r = i + 2;

                ws.Cell(r, 1).Value = rows[i].FileName;
                ws.Cell(r, 2).Value = rows[i].SoToKhai;
                ws.Cell(r, 3).Value = "";
            }

            ws.Column(1).Width = 40;
            ws.Column(2).Width = 22;
            ws.Column(3).Width = 40;

            wb.SaveAs(outputPath);
        }

        // ════════════════════════════════════════════════
        // SAFE FILE NAME
        // ════════════════════════════════════════════════
        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";

            char[] invalid =
                Path.GetInvalidFileNameChars();

            string safe =
                new string(
                    name.Select(c =>
                        invalid.Contains(c) ? '_' : c)
                    .ToArray());

            return safe[..Math.Min(safe.Length, 150)];
        }
    }
}