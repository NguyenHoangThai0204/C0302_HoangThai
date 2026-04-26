using Microsoft.AspNetCore.Mvc;
using ClosedXML.Excel;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace C0302_HoangThai.Controllers.C0302
{
    public class RenameExcelTachFileController : Controller
    {
        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

        [HttpPost]
        public IActionResult ProcessFiles(IFormFile excelFile)
        {
            var debugLogs = new List<string>();
            var fileInfos = new List<SplitFileInfo>();

            try
            {
                if (excelFile == null || excelFile.Length == 0 ||
                    !excelFile.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    ViewBag.Error = "Vui lòng chọn file Excel (.xlsx)";
                    return View("Upload");
                }

                // Lưu file upload vào temp
                var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempFolder);

                var inputPath = Path.Combine(tempFolder, excelFile.FileName);
                using (var fs = new FileStream(inputPath, FileMode.Create))
                    excelFile.CopyTo(fs);

                debugLogs.Add($"=== XỬ LÝ FILE: {excelFile.FileName} ===");

                // Đọc và tách file
                SplitExcelFile(inputPath, tempFolder, fileInfos, debugLogs);

                if (fileInfos.Count == 0)
                {
                    ViewBag.Error = "Không tìm thấy nhóm nào để tách. Vui lòng kiểm tra cấu trúc file.";
                    ViewBag.DebugLogs = debugLogs;
                    return View("Upload");
                }

                // Lưu debug log
                System.IO.File.WriteAllLines(Path.Combine(tempFolder, "split_log.txt"), debugLogs);

                // Đóng gói ZIP
                var outputZipPath = Path.Combine(Path.GetTempPath(), $"Split_Excel_{DateTime.Now:yyyyMMddHHmmss}.zip");
                // Chỉ zip các file .xlsx kết quả (không zip file gốc và log)
                var outputFolder = Path.Combine(tempFolder, "output");
                Directory.CreateDirectory(outputFolder);
                foreach (var info in fileInfos)
                    System.IO.File.Copy(info.FilePath, Path.Combine(outputFolder, info.NewName), true);
                ZipFile.CreateFromDirectory(outputFolder, outputZipPath);

                ViewBag.Success = $"Đã tách thành công {fileInfos.Count} file Excel!";
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

        [HttpGet]
        public IActionResult DownloadZip(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return NotFound();

            var memory = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open))
                stream.CopyTo(memory);
            memory.Position = 0;

            return File(memory, "application/zip", Path.GetFileName(filePath));
        }

        // ── Đọc file Excel, tách theo nhóm header ──
        private void SplitExcelFile(string inputPath, string outputFolder,
            List<SplitFileInfo> fileInfos, List<string> debugLogs)
        {
            using var wbSrc = new XLWorkbook(inputPath);
            var wsSrc = wbSrc.Worksheet(1);

            int maxRow = wsSrc.LastRowUsed()?.RowNumber() ?? 0;
            int maxCol = wsSrc.LastColumnUsed()?.ColumnNumber() ?? 0;
            debugLogs.Add($"  Sheet: '{wsSrc.Name}', {maxRow} rows × {maxCol} cols");

            // Xác định các nhóm: mỗi nhóm = 1 header row + data rows tiếp theo
            var groups = new List<(int HeaderRow, List<int> DataRows)>();
            int? currentHeader = null;
            var currentData = new List<int>();

            for (int r = 1; r <= maxRow; r++)
            {
                var cellA = wsSrc.Cell(r, 1).GetValue<string>();
                var cellB = wsSrc.Cell(r, 2).GetValue<string>();
                bool isHeader = cellA?.Trim() == "Week" && cellB != null && cellB.Contains("INVOICE");

                if (isHeader)
                {
                    if (currentHeader.HasValue)
                        groups.Add((currentHeader.Value, currentData));
                    currentHeader = r;
                    currentData = new List<int>();
                }
                else if (currentHeader.HasValue)
                {
                    currentData.Add(r);
                }
            }
            if (currentHeader.HasValue)
                groups.Add((currentHeader.Value, currentData));

            debugLogs.Add($"  Tìm thấy {groups.Count} nhóm");

            // Tách từng nhóm
            foreach (var (headerRow, dataRows) in groups)
            {
                // Thu thập danh sách invoice trong nhóm (cột B, không trùng)
                var invoices = new List<string>();
                foreach (var r in dataRows)
                {
                    var val = wsSrc.Cell(r, 2).GetValue<string>()?.Trim();
                    if (!string.IsNullOrEmpty(val) &&
                        Regex.IsMatch(val, @"^[A-Z]+-\d+$") &&
                        !invoices.Contains(val))
                        invoices.Add(val);
                }

                string fileName = invoices.Count > 0
                    ? BuildFileName(invoices)
                    : $"Group_row{headerRow}";

                // Tránh tên trùng
                string safeFileName = SanitizeFileName(fileName);
                string outputPath = Path.Combine(outputFolder, $"{safeFileName}.xlsx");
                int counter = 1;
                while (System.IO.File.Exists(outputPath))
                    outputPath = Path.Combine(outputFolder, $"{safeFileName}_{counter++}.xlsx");

                // Tạo workbook mới với ClosedXML
                using var wbNew = new XLWorkbook();
                var wsNew = wbNew.AddWorksheet(wsSrc.Name);

                // Copy column widths
                for (int c = 1; c <= maxCol; c++)
                {
                    var srcCol = wsSrc.Column(c);
                    wsNew.Column(c).Width = srcCol.Width;
                }

                // Copy header + data rows
                var allRows = new List<int> { headerRow };
                allRows.AddRange(dataRows);

                for (int newRowIdx = 1; newRowIdx <= allRows.Count; newRowIdx++)
                {
                    int srcRowIdx = allRows[newRowIdx - 1];
                    var srcRow = wsSrc.Row(srcRowIdx);
                    var dstRow = wsNew.Row(newRowIdx);
                    dstRow.Height = srcRow.Height;

                    for (int c = 1; c <= maxCol; c++)
                    {
                        var srcCell = wsSrc.Cell(srcRowIdx, c);
                        var dstCell = wsNew.Cell(newRowIdx, c);

                        // Copy giá trị (bỏ formula, giữ giá trị tính toán)
                        if (srcCell.HasFormula)
                            dstCell.Value = srcCell.CachedValue;
                        else
                            dstCell.Value = srcCell.Value;

                        // Copy style
                        dstCell.Style = srcCell.Style;
                    }
                }

                // Copy merged cells nằm trong các rows được copy
                var srcRowSet = new HashSet<int>(allRows);
                foreach (var merge in wsSrc.MergedRanges.ToList())
                {
                    int mr1 = merge.FirstRow().RowNumber();
                    int mr2 = merge.LastRow().RowNumber();
                    if (srcRowSet.Contains(mr1) && srcRowSet.Contains(mr2))
                    {
                        int newR1 = allRows.IndexOf(mr1) + 1;
                        int newR2 = allRows.IndexOf(mr2) + 1;
                        wsNew.Range(newR1, merge.FirstColumn().ColumnNumber(),
                                    newR2, merge.LastColumn().ColumnNumber()).Merge();
                    }
                }

                // Freeze header row
                wsNew.SheetView.Freeze(1, 0);

                wbNew.SaveAs(outputPath);

                string newName = Path.GetFileName(outputPath);
                fileInfos.Add(new SplitFileInfo
                {
                    OriginalGroup = $"Header row {headerRow}, {dataRows.Count} data rows",
                    NewName = newName,
                    FilePath = outputPath,
                    Invoices = string.Join(", ", invoices)
                });

                debugLogs.Add($"  ✓ {newName}  (invoices: {string.Join(", ", invoices)})");
            }
        }

        // ── Đặt tên file theo danh sách invoice ──
        // ITA-0027832, ITA-0027833          → ITA-0027832-33
        // VIE-0008000, VIE-0008001          → VIE-0008000-01
        // ITA-0027999, VIE-0008000, VIE-0008001 → ITA-0027999-8000-8001
        private string BuildFileName(List<string> invoices)
        {
            if (invoices.Count == 0) return "Unknown";
            if (invoices.Count == 1) return invoices[0];

            (string prefix, string num) Parse(string inv)
            {
                var m = Regex.Match(inv, @"^([A-Z]+)-(\d+)$");
                return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (inv, "");
            }

            var parsed = invoices.Select(Parse).ToList();
            var (firstPrefix, firstNum) = parsed[0];
            string result = invoices[0];

            for (int i = 1; i < parsed.Count; i++)
            {
                var (prefix, num) = parsed[i];
                if (string.IsNullOrEmpty(num)) { result += $"-{invoices[i]}"; continue; }

                if (prefix == firstPrefix)
                {
                    // Cùng prefix → tìm suffix khác (tối thiểu 2 ký tự)
                    int diffStart = 0;
                    for (int j = 0; j < Math.Min(firstNum.Length, num.Length); j++)
                    {
                        if (firstNum[j] == num[j]) diffStart = j + 1;
                        else break;
                    }
                    string suffix = num[diffStart..];
                    if (suffix.Length < 2) suffix = num[Math.Max(0, diffStart - 1)..];
                    if (suffix.Length < 2) suffix = num[^2..];
                    result += $"-{suffix}";
                }
                else
                {
                    // Khác prefix → bỏ leading zeros
                    result += $"-{long.Parse(num)}";
                }
            }

            return result;
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "Unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return safe[..Math.Min(safe.Length, 150)];
        }

        public class SplitFileInfo
        {
            public string OriginalGroup { get; set; }
            public string NewName { get; set; }
            public string FilePath { get; set; }
            public string Invoices { get; set; }
        }
    }
}