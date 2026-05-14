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
            Directory.Exists(TessDataPath);

        // =========================================================
        // MODEL
        // =========================================================

        class PageScanResult
        {
            public int PageNumber { get; set; }

            public bool IsStartPage { get; set; }

            public bool HasBarcodePage { get; set; }

            public List<string> RawNumbers { get; set; } = new();

            public List<string> Numbers { get; set; } = new();

            public string RawText { get; set; }
        }

        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

        // =========================================================
        // SPLIT PDF
        // =========================================================
        private bool IsLooseSimilar(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) ||
                string.IsNullOrWhiteSpace(b))
                return false;

            if (a.Length != b.Length)
                return false;

            int diff = 0;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    diff++;
            }

            // OCR sai tối đa 2 ký tự
            if (diff <= 2)
                return true;

            // prefix mạnh + suffix mạnh
            bool samePrefix5 =
                a.Substring(0, 5) ==
                b.Substring(0, 5);

            bool sameSuffix5 =
                a.Substring(a.Length - 5) ==
                b.Substring(b.Length - 5);

            if (samePrefix5 && sameSuffix5 && diff <= 3)
                return true;

            return false;
        }

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
                string pdfPath = Path.Combine(tempFolder, file.FileName);

                using (var fs = new FileStream(pdfPath, FileMode.Create))
                    file.CopyTo(fs);

                List<PageScanResult> scans = new();
                HashSet<string> globalContext = new();

                using (PdfReader reader = new PdfReader(pdfPath))
                using (PdfDocument pdfDoc = new PdfDocument(reader))
                {
                    int totalPages = pdfDoc.GetNumberOfPages();

                    // =====================================================
                    // PASS 1
                    // OCR đúng 1 lần/page
                    // =====================================================

                    for (int i = 1; i <= totalPages; i++)
                    {
                        var scan = ScanPageRaw(
                            pdfDoc,
                            pdfPath,
                            i);

                        scans.Add(scan);

                        foreach (var n in scan.RawNumbers)
                        {
                            if (IsValidSoToKhai(n))
                                globalContext.Add(n);
                        }
                    }

                    Console.WriteLine("");
                    Console.WriteLine("========================================");
                    Console.WriteLine($">>> GLOBAL CONTEXT: {string.Join(", ", globalContext)}");
                    Console.WriteLine("========================================");

                    // =====================================================
                    // PASS 2
                    // KHÔNG OCR
                    // chỉ normalize
                    // =====================================================

                    foreach (var scan in scans)
                    {
                        foreach (var raw in scan.RawNumbers)
                        {
                            string? normalized =
                                NormalizeTo12Digits(
                                    raw,
                                    globalContext,
                                    false);

                            if (normalized != null)
                                scan.Numbers.Add(normalized);
                        }

                        scan.Numbers =
                            scan.Numbers
                                .Distinct()
                                .ToList();

                        //Console.WriteLine($"=== RAW TEXT PAGE {scan.PageNumber} ===");
                        //Console.WriteLine(scan.RawText ?? "(null)");
                        //Console.WriteLine($"=== Numbers found: {string.Join(", ", scan.Numbers)} ===");
                        //Console.WriteLine($"=== IsStartPage: {scan.IsStartPage} ===");

                        if (scan.IsStartPage && scan.Numbers.Any())
                        {
                            Console.WriteLine("");
                            Console.WriteLine("========================================");
                            Console.WriteLine($"TAO FILE MOI => PAGE {scan.PageNumber}");
                            Console.WriteLine($"SO TO KHAI   => {scan.Numbers.First()}");
                            Console.WriteLine("========================================");
                        }
                        else
                        {
                            string nums = scan.Numbers.Any()
                                ? string.Join(",", scan.Numbers)
                                : "NONE";

                            Console.WriteLine($"PAGE {scan.PageNumber} => {nums}");
                        }
                    }

                    // =====================================================
                    // GROUPING
                    // =====================================================

                    Dictionary<string, List<int>> groups = new();

                    string currentGroup = "Unknown";

                    Dictionary<string, int> currentCounter = new();

                    int groupStartPage = 1;

                    List<(string SoToKhai, int Start, int End)> timelines = new();

                    for (int idx = 0; idx < scans.Count; idx++)
                    {
                        var scan = scans[idx];

                        if (scan.IsStartPage && scan.Numbers.Any())
                        {
                            string best = scan.Numbers
                                .GroupBy(x => x)
                                .OrderByDescending(x => x.Count())
                                .First().Key;

                            if (currentGroup != "Unknown" &&
                                groups.ContainsKey(currentGroup) &&
                                groups[currentGroup].Any())
                            {
                                timelines.Add((
                                    currentGroup,
                                    groupStartPage,
                                    scan.PageNumber - 1));
                            }

                            currentGroup = best;
                            currentCounter.Clear();
                            groupStartPage = scan.PageNumber;
                        }

                        if (!groups.ContainsKey(currentGroup))
                            groups[currentGroup] = new List<int>();

                        groups[currentGroup].Add(scan.PageNumber);

                        foreach (var n in scan.Numbers)
                        {
                            if (!currentCounter.ContainsKey(n))
                                currentCounter[n] = 0;

                            currentCounter[n]++;
                        }

                        if (currentCounter.Any())
                        {
                            currentGroup = currentCounter
                                .OrderByDescending(x => x.Value)
                                .First().Key;
                        }
                    }

                    // close last group
                    if (currentGroup != "Unknown" &&
                        groups.ContainsKey(currentGroup) &&
                        groups[currentGroup].Any())
                    {
                        timelines.Add((
                            currentGroup,
                            groupStartPage,
                            scans.Last().PageNumber));
                    }

                    // =====================================================
                    // MERGE TIMELINE
                    // =====================================================

                    List<(string SoToKhai, int Start, int End)> merged = new();

                    foreach (var t in timelines)
                    {
                        if (!merged.Any())
                        {
                            merged.Add(t);
                            continue;
                        }

                        var last = merged.Last();

                        // CASE 1: giống hệt
                        if (last.SoToKhai == t.SoToKhai &&
                            t.Start <= last.End + 2)
                        {
                            merged[^1] =
                                (last.SoToKhai, last.Start, t.End);

                            continue;
                        }

                        // CASE 2: OCR lệch 1 ký tự
                        bool similar =
                            IsSimilarSoToKhai(
                                last.SoToKhai,
                                t.SoToKhai,
                                1);

                        bool nearPage =
                            t.Start <= last.End + 2;

                        if (similar && nearPage)
                        {
                            Console.WriteLine(
                                $">>> OCR FIX: {t.SoToKhai} => {last.SoToKhai}");

                            merged[^1] =
                                (last.SoToKhai, last.Start, t.End);

                            continue;
                        }

                        // CASE 3: OCR transposition
                        bool sameSet =
                            IsSameDigitSet(
                                last.SoToKhai,
                                t.SoToKhai);

                        bool nearPage3 =
                            t.Start <= last.End + 2;

                        if (sameSet && nearPage3)
                        {
                            Console.WriteLine(
                                $">>> TRANSPOSE FIX: {t.SoToKhai} => {last.SoToKhai}");

                            merged[^1] =
                                (last.SoToKhai, last.Start, t.End);

                            continue;
                        }

                        merged.Add(t);
                    }

                    // =====================================================
                    // THAY TOÀN BỘ BLOCK:
                    // SECOND PASS MERGE
                    // =====================================================

                    bool changed = true;

                    while (changed)
                    {
                        changed = false;

                        // =====================================================
                        // CASE A
                        // merge block đơn lẻ bị OCR sai
                        // =====================================================

                        for (int i = 1; i < merged.Count - 1; i++)
                        {
                            var prev = merged[i - 1];
                            var curr = merged[i];
                            var next = merged[i + 1];

                            int prevPages =
                                prev.End - prev.Start + 1;

                            int currPages =
                                curr.End - curr.Start + 1;

                            int nextPages =
                                next.End - next.Start + 1;

                            bool connected =
                                curr.Start <= prev.End + 2 &&
                                next.Start <= curr.End + 2;

                            if (!connected)
                                continue;

                            bool simPrevNext =
                                IsLooseSimilar(prev.SoToKhai, next.SoToKhai)
                                || IsSameDigitSet(prev.SoToKhai, next.SoToKhai);

                            // =====================================================
                            // CASE 1
                            // prev và next giống nhau
                            // curr chỉ 1-2 page
                            // =====================================================

                            if (simPrevNext &&
                                currPages <= 2)
                            {
                                Console.WriteLine(
                                    $">>> MERGE MIDDLE BLOCK: {curr.SoToKhai} INTO: {prev.SoToKhai}");

                                merged[i - 1] =
                                    (prev.SoToKhai, prev.Start, next.End);

                                merged.RemoveAt(i + 1);
                                merged.RemoveAt(i);

                                changed = true;
                                break;
                            }

                            // =====================================================
                            // CASE 2
                            // curr chỉ có 1 page
                            // curr gần giống prev hoặc next
                            // =====================================================

                            bool currLooksLikePrev =
                                IsLooseSimilar(curr.SoToKhai, prev.SoToKhai)
                                || IsLikelyOCRNoise(curr.SoToKhai, prev.SoToKhai);

                            bool currLooksLikeNext =
                                IsLooseSimilar(curr.SoToKhai, next.SoToKhai)
                                || IsLikelyOCRNoise(curr.SoToKhai, next.SoToKhai);

                            if (currPages <= 2 &&
                                (currLooksLikePrev || currLooksLikeNext))
                            {
                                string target =
                                    currLooksLikePrev
                                        ? prev.SoToKhai
                                        : next.SoToKhai;

                                Console.WriteLine(
                                    $">>> SINGLE PAGE OCR FIX: {curr.SoToKhai} => {target}");

                                if (currLooksLikePrev)
                                {
                                    merged[i - 1] =
                                        (prev.SoToKhai, prev.Start, curr.End);

                                    merged.RemoveAt(i);
                                }
                                else
                                {
                                    merged[i + 1] =
                                        (next.SoToKhai, curr.Start, next.End);

                                    merged.RemoveAt(i);
                                }

                                changed = true;
                                break;
                            }
                        }

                        if (changed)
                            continue;

                        // =====================================================
                        // CASE B
                        // merge page lẻ nằm giữa
                        // ví dụ:
                        // 240 => sai OCR
                        // 241-245 => đúng
                        // =====================================================

                        for (int i = 1; i < merged.Count; i++)
                        {
                            var prev = merged[i - 1];
                            var curr = merged[i];

                            int prevPages =
                                prev.End - prev.Start + 1;

                            int currPages =
                                curr.End - curr.Start + 1;

                            bool near =
                                curr.Start <= prev.End + 2;

                            if (!near)
                                continue;

                            bool looksWrong =
                                IsLikelyOCRNoise(prev.SoToKhai, curr.SoToKhai);

                            // block 1 page thường OCR lỗi
                            if (looksWrong &&
                                (prevPages == 1 || currPages >= 3))
                            {
                                string keep =
                                    currPages >= prevPages
                                        ? curr.SoToKhai
                                        : prev.SoToKhai;

                                Console.WriteLine(
                                    $">>> FINAL OCR MERGE: {prev.SoToKhai} + {curr.SoToKhai} => {keep}");

                                merged[i - 1] =
                                    (
                                        keep,
                                        Math.Min(prev.Start, curr.Start),
                                        Math.Max(prev.End, curr.End)
                                    );

                                merged.RemoveAt(i);

                                changed = true;
                                break;
                            }
                        }

                        // =====================================================
                        // CASE C
                        // block 1 page đứng trước block lớn
                        // auto merge
                        // =====================================================

                        for (int i = 1; i < merged.Count; i++)
                        {
                            var prev = merged[i - 1];
                            var curr = merged[i];

                            int prevPages =
                                prev.End - prev.Start + 1;

                            int currPages =
                                curr.End - curr.Start + 1;

                            bool near =
                                curr.Start <= prev.End + 1;

                            if (!near)
                                continue;

                            // block trước chỉ có 1 page
                            // block sau >=3 page
                            if (prevPages == 1 &&
                                currPages >= 3)
                            {
                                //Console.WriteLine(
                                //    $">>> FORCE MERGE SINGLE PAGE: {prev.SoToKhai} => {curr.SoToKhai}");

                                merged[i - 1] =
                                (
                                    curr.SoToKhai,
                                    prev.Start,
                                    curr.End
                                );

                                merged.RemoveAt(i);

                                changed = true;
                                break;
                            }
                        }
                    }

                    // =====================================================
                    // REBUILD GROUPS
                    // =====================================================

                    groups.Clear();

                    foreach (var item in merged)
                    {
                        if (!groups.ContainsKey(item.SoToKhai))
                            groups[item.SoToKhai] = new List<int>();

                        for (int p = item.Start; p <= item.End; p++)
                            groups[item.SoToKhai].Add(p);
                    }

                    groups = groups.ToDictionary(
                        x => x.Key,
                        x => x.Value
                            .Distinct()
                            .OrderBy(x => x)
                            .ToList());

                    // =====================================================
                    // SUMMARY
                    // =====================================================

                    Console.WriteLine("");
                    Console.WriteLine("========================================");
                    Console.WriteLine("============== SUMMARY =================");
                    Console.WriteLine("========================================");

                    int stt = 1;

                    foreach (var t in merged)
                    {
                        Console.WriteLine(t.Start == t.End
                            ? $"\n{stt:D3}. {t.SoToKhai} => PAGE {t.Start}"
                            : $"\n{stt:D3}. {t.SoToKhai} => PAGE {t.Start} -> {t.End}");

                        stt++;
                    }

                    // =====================================================
                    // EXPORT PDF
                    // =====================================================

                    string outFolder =
                        Path.Combine(tempFolder, "output");

                    Directory.CreateDirectory(outFolder);

                    var excelRows =
                        new List<(string FileName, string SoToKhai)>();

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
                                merger.Merge(pdfDoc, pageNum, pageNum);
                        }

                        excelRows.Add(($"{safeName}.pdf", soToKhai));
                    }

                    // =====================================================
                    // EXCEL
                    // =====================================================

                    string excelPath =
                        Path.Combine(outFolder, "Mapping.xlsx");

                    BuildExcel(excelRows, excelPath);

                    // =====================================================
                    // ZIP
                    // =====================================================

                    string zipPath = Path.Combine(
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
                return BadRequest(ex.ToString());
            }
        }

        // =========================================================
        // SCAN PAGE RAW
        // =========================================================
        // =====================================================
        // THÊM HÀM NÀY
        // =====================================================

        private bool IsLikelyOCRNoise(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) ||
                string.IsNullOrWhiteSpace(b))
                return false;

            if (a.Length != 12 || b.Length != 12)
                return false;

            // prefix 5 số đầu giống nhau
            bool samePrefix =
                a.Substring(0, 5) ==
                b.Substring(0, 5);

            if (!samePrefix)
                return false;

            // OCR thường sai 1-2 ký tự cuối
            int diff = 0;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    diff++;
            }

            return diff <= 2;
        }
        private PageScanResult ScanPageRaw(
            PdfDocument pdfDoc,
            string pdfPath,
            int pageNumber)
        {
            // =====================================================
            // TEXT LAYER
            // =====================================================

            string textLayerText = "";

            try
            {
                textLayerText =
                    PdfTextExtractor.GetTextFromPage(
                        pdfDoc.GetPage(pageNumber),
                        new SimpleTextExtractionStrategy()) ?? "";
            }
            catch { }

            // =====================================================
            // OCR
            // =====================================================

            string ocrText = "";

            if (HasTesseract && textLayerText.Length < 50)
            {
                try
                {
                    ocrText = RunOCR(pdfPath, pageNumber);
                }
                catch { }
            }

            string text =
                string.IsNullOrWhiteSpace(textLayerText)
                    ? ocrText
                    : textLayerText;

            text ??= "";

            string clean = NormalizeOCRText(text);

            var result = new PageScanResult
            {
                PageNumber = pageNumber,
                RawText = clean
            };

            // =====================================================
            // START PAGE DETECT
            // =====================================================

            bool hasKhai = Regex.IsMatch(
                clean,
                @"to\s*khai|td\s*khai|hang\s*hoa\s*nhap\s*khau",
                RegexOptions.IgnoreCase);

            bool hasLoaiHinh = Regex.IsMatch(
                clean,
                @"e31|a11|f14|loai\s*hinh",
                RegexOptions.IgnoreCase);

            bool isPhuLuc = Regex.IsMatch(
                clean,
                @"phu\s*luc\s*dinh\s*kem",
                RegexOptions.IgnoreCase);

            result.IsStartPage =
      hasKhai && !isPhuLuc;

            // =====================================================
            // FIND RAW NUMBERS
            // =====================================================

            var lines = clean.Split('\n');

            foreach (var line in lines)
            {
                bool isToKhaiLine = Regex.IsMatch(
                    line,
                    @"so\s*to\s*khai|phu\s*luc.*to\s*khai|to\s*khai\s*:?\s*[0-9]",
                    RegexOptions.IgnoreCase);

                if (!isToKhaiLine)
                    continue;

                var nums =
                    Regex.Matches(line, @"[0-9OIlSB]{10,15}");

                foreach (Match m in nums)
                {
                    string v = NormalizeDigits(m.Value);

                    result.RawNumbers.Add(v);
                }
            }

            // fallback toàn trang
            // =====================================================
            // FALLBACK TOÀN TRANG
            // CHỈ CHẠY CHO PAGE GIỐNG TỜ KHAI
            // =====================================================

            if (!result.RawNumbers.Any() && result.IsStartPage)
            {
                foreach (var line in lines)
                {
                    string lower = line.ToLower();

                    // =========================================
                    // BỎ QUA DOCUMENT KHÔNG PHẢI TỜ KHAI
                    // =========================================

                    bool badLine =
                        lower.Contains("invoice") ||
                        lower.Contains("packing") ||
                        lower.Contains("eori") ||
                        lower.Contains("hawb") ||
                        lower.Contains("air waybill") ||
                        lower.Contains("arrival notice") ||
                        lower.Contains("tracking") ||
                        lower.Contains("bill to") ||
                        lower.Contains("ship to") ||
                        lower.Contains("vat") ||
                        lower.Contains("dhl") ||
                        lower.Contains("fedex") ||
                        lower.Contains("ups");

                    if (badLine)
                        continue;

                    // =========================================
                    // BỎ QUA MÃ HÀNG
                    // =========================================

                    bool isMaHang = Regex.IsMatch(
                        line,
                        @"#&|tu\s*di[eê]n|di[eê]n\s*tr[oở]|cu[oộ]n\s*c[aả]m|th[aạ]ch\s*anh|transistor|m[aạ]ch\s*in|m[aạ]ch\s*\u0111i[e\u1ec7]n",
                        RegexOptions.IgnoreCase);

                    if (isMaHang)
                        continue;

                    // =========================================
                    // TÌM SỐ
                    // =========================================

                    var nums =
                        Regex.Matches(line, @"[0-9OIlSB]{10,15}");

                    foreach (Match m in nums)
                    {
                        string v = NormalizeDigits(m.Value);

                        result.RawNumbers.Add(v);
                    }
                }
            }

            result.RawNumbers =
                result.RawNumbers
                    .Distinct()
                    .ToList();

            return result;
        }

        // =========================================================
        // NORMALIZE
        // =========================================================

        private string? NormalizeTo12Digits(
            string v,
            HashSet<string> contextNumbers,
            bool buildContextOnly = false)
        {
            if (string.IsNullOrWhiteSpace(v))
                return null;

            if (v.StartsWith("2024") ||
                v.StartsWith("2025") ||
                v.StartsWith("2026"))
                return null;

            if (v.StartsWith("030") ||
                v.StartsWith("010"))
                return null;

            // 12 digit chuẩn
            if (v.Length == 12)
            {
                if (v.StartsWith("10") ||
                    v.StartsWith("11"))
                    return v;

                return null;
            }

            // 11 / 13 digit
            if (v.Length == 11 || v.Length == 13)
            {
                if (buildContextOnly)
                    return null;

                var candidates = new List<string>();

                // 13 digit
                if (v.Length == 13)
                {
                    for (int pos = 0; pos < v.Length; pos++)
                    {
                        string candidate =
                            v.Remove(pos, 1);

                        if (IsValidSoToKhai(candidate))
                            candidates.Add(candidate);
                    }
                }
                else
                {
                    // 11 digit
                    for (int pos = 0; pos <= v.Length; pos++)
                    {
                        for (char d = '0'; d <= '9'; d++)
                        {
                            string candidate =
                                v.Insert(pos, d.ToString());

                            if (IsValidSoToKhai(candidate))
                                candidates.Add(candidate);
                        }
                    }
                }

                if (!candidates.Any())
                    return null;

                // exact context
                foreach (var c in candidates)
                {
                    if (contextNumbers.Contains(c))
                    {
                        Console.WriteLine(
                            $">>> {v.Length}-DIGIT FIX (exact context): {v} => {c}");

                        return c;
                    }
                }

                // similar context
                foreach (var c in candidates)
                {
                    foreach (var ctx in contextNumbers)
                    {
                        if (IsSimilarSoToKhai(c, ctx, 1))
                        {
                            Console.WriteLine(
                                $">>> {v.Length}-DIGIT FIX (similar context {ctx}): {v} => {c}");

                            return c;
                        }
                    }
                }

                Console.WriteLine(
                    $">>> {v.Length}-DIGIT FIX (fallback): {v} => {candidates[0]}");

                return candidates[0];
            }

            return null;
        }

        // =========================================================
        // VALIDATE
        // =========================================================

        private bool IsValidSoToKhai(string v)
        {
            if (v.Length != 12)
                return false;

            if (!v.StartsWith("10") &&
                !v.StartsWith("11"))
                return false;

            if (v.StartsWith("2024") ||
                v.StartsWith("2025") ||
                v.StartsWith("2026"))
                return false;

            if (v.StartsWith("030") ||
                v.StartsWith("010"))
                return false;

            return true;
        }

        // =========================================================
        // OCR
        // =========================================================

        private string RunOCR(string pdfPath, int pageNumber)
        {
            using var library = DocLib.Instance;

            using var docReader = library.GetDocReader(
                pdfPath,
                new PageDimensions(2067, 2924));

            using var pgReader = docReader.GetPageReader(pageNumber - 1);

            int w = pgReader.GetPageWidth();
            int h = pgReader.GetPageHeight();

            byte[] raw = pgReader.GetImage();

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

            string lang =
                System.IO.File.Exists(
                    Path.Combine(TessDataPath, "vie.traineddata"))
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
                engine.Process(
                    pix,
                    PageSegMode.Auto);

            return page.GetText() ?? "";
        }

        // =========================================================
        // NORMALIZE OCR
        // =========================================================

        private string NormalizeOCRText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = Regex.Replace(text, @"[ \t]+", " ");

            return text;
        }

        // =========================================================
        // FIX OCR DIGITS
        // =========================================================

        private string NormalizeDigits(string value)
        {
            return value
                .Replace("O", "0")
                .Replace("o", "0")
                .Replace("I", "1")
                .Replace("l", "1")
                .Replace("|", "1")
                .Replace("B", "8")
                .Replace("S", "5")
                .Replace(" ", "")
                .Replace(".", "")
                .Replace(",", "");
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

                        var cellFile = ws.Cell(row, 1).GetString().Trim();
                        var cellTenDatFile = ws.Cell(row, 2).GetString().Trim();
                        var cellTenMoi = ws.Cell(row, 3).GetString().Trim();
                       

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

                        newName = $"{info.TenMoi}_{tenDatFile}.pdf";
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
        // SIMILAR
        // =========================================================

        private bool IsSimilarSoToKhai(
            string a,
            string b,
            int maxDiff = 1)
        {
            if (string.IsNullOrWhiteSpace(a) ||
                string.IsNullOrWhiteSpace(b))
                return false;

            if (a.Length != b.Length)
                return false;

            int diff = 0;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    diff++;

                if (diff > maxDiff)
                    return false;
            }

            return true;
        }

        // =========================================================
        // SAME DIGIT SET
        // =========================================================

        private bool IsSameDigitSet(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) ||
                string.IsNullOrWhiteSpace(b))
                return false;

            if (a.Length != b.Length)
                return false;

            var sortedA =
                string.Concat(a.OrderBy(c => c));

            var sortedB =
                string.Concat(b.OrderBy(c => c));

            return sortedA == sortedB;
        }

        // =========================================================
        // BUILD EXCEL
        // =========================================================

        private void BuildExcel(
            List<(string FileName, string SoToKhai)> rows,
            string outputPath)
        {
            using var wb = new XLWorkbook();

            var ws = wb.AddWorksheet("Mapping");

            ws.Cell(1, 1).Value = "Tên file cũ";
            ws.Cell(1, 2).Value = "Số tờ khai";
            ws.Cell(1, 3).Value = "Tên mới";

            for (int i = 0; i < rows.Count; i++)
            {
                int r = i + 2;

                ws.Cell(r, 1).Value = rows[i].FileName;
                ws.Cell(r, 2).Value = rows[i].SoToKhai;
                ws.Cell(r, 3).Value = "";
            }

            ws.Columns().AdjustToContents();

            wb.SaveAs(outputPath);
        }

        // =========================================================
        // SAFE FILE NAME
        // =========================================================

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";

            char[] invalid =
                Path.GetInvalidFileNameChars();

            string safe = new string(
                name.Select(c =>
                    invalid.Contains(c) ? '_' : c).ToArray());

            return safe[..Math.Min(safe.Length, 150)];
        }
    }
}