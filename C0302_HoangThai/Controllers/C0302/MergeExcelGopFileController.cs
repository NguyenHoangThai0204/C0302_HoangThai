using Microsoft.AspNetCore.Mvc;
using ClosedXML.Excel;
using System.IO.Compression;

namespace C0302_HoangThai.Controllers.C0302
{
    /// <summary>
    /// Chức năng GỘP file Excel: nhận vào 1 file ZIP chứa nhiều file .xlsx
    /// (thường là output của chức năng Tách - RenameExcelTachFileController),
    /// gộp lại thành 1 file Excel duy nhất.
    ///
    /// Quy tắc gộp:
    ///  - Mỗi file con giữ nguyên header + data rows của chính nó (copy value + style).
    ///  - Các block được sắp xếp tăng dần theo "ngày đầu tiên" tìm thấy ở cột D
    ///    (Date) trong phần data rows của file đó. Nếu 1 file có nhiều ngày khác
    ///    nhau ở cột D thì lấy ngày xuất hiện SỚM NHẤT (dòng trên cùng có ngày) để làm mốc sort.
    ///  - Giữa 2 block (2 file) liên tiếp: chèn đúng 1 dòng phân cách, cột A ghi
    ///    chữ "X". Dòng này chỉ được tô MÀU CYAN cho cả dòng khi ngày mốc (cột D)
    ///    chuyển sang một ngày khác so với block ngay trước đó ("qua ngày mới");
    ///    nếu 2 block liền kề cùng ngày mốc thì dòng "X" không tô màu.
    ///  - Border/khung viền của mỗi block được GIỮ NGUYÊN y như file gốc (copy
    ///    trực tiếp style từng cell, không ép kẻ lưới lại).
    /// </summary>
    public class MergeExcelGopFileController : Controller
    {
        private const int DATE_COLUMN = 4; // Cột D = Date

        [HttpGet]
        public IActionResult Upload()
        {
            return View();
        }

        [HttpPost]
        public IActionResult ProcessMerge(IFormFile zipFile)
        {
            var debugLogs = new List<string>();
            var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            try
            {
                if (zipFile == null || zipFile.Length == 0 ||
                    !zipFile.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ViewBag.Error = "Vui lòng chọn file ZIP (.zip) chứa các file Excel cần gộp";
                    return View("Upload");
                }

                Directory.CreateDirectory(tempFolder);

                // Lưu zip upload
                var zipPath = Path.Combine(tempFolder, zipFile.FileName);
                using (var fs = new FileStream(zipPath, FileMode.Create))
                    zipFile.CopyTo(fs);

                debugLogs.Add($"=== NHẬN FILE ZIP: {zipFile.FileName} ===");

                // Giải nén
                var extractFolder = Path.Combine(tempFolder, "extracted");
                Directory.CreateDirectory(extractFolder);
                ZipFile.ExtractToDirectory(zipPath, extractFolder);

                // Tìm tất cả file .xlsx trong zip (kể cả trong thư mục con), bỏ qua file rác của macOS
                var xlsxFiles = Directory.GetFiles(extractFolder, "*.xlsx", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("~$") &&
                                !f.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .ToList();

                debugLogs.Add($"  Tìm thấy {xlsxFiles.Count} file .xlsx trong ZIP");

                if (xlsxFiles.Count == 0)
                {
                    ViewBag.Error = "Không tìm thấy file .xlsx nào trong ZIP đã upload.";
                    ViewBag.DebugLogs = debugLogs;
                    return View("Upload");
                }

                // Đọc từng file thành 1 block
                var blocks = new List<MergeBlock>();
                foreach (var path in xlsxFiles)
                {
                    var block = ReadBlock(path, debugLogs);
                    if (block != null)
                        blocks.Add(block);
                }

                if (blocks.Count == 0)
                {
                    ViewBag.Error = "Không đọc được dữ liệu hợp lệ từ các file trong ZIP.";
                    ViewBag.DebugLogs = debugLogs;
                    return View("Upload");
                }

                // Sắp xếp tăng dần theo ngày đầu tiên tìm được (file không có ngày -> xếp cuối)
                var sortedBlocks = blocks
                    .OrderBy(b => b.SortDate ?? DateTime.MaxValue)
                    .ToList();

                debugLogs.Add("  === THỨ TỰ GỘP (sau khi sort theo cột D tăng dần) ===");
                foreach (var b in sortedBlocks)
                    debugLogs.Add($"    {b.FileName}  ->  ngày mốc: {(b.SortDate.HasValue ? b.SortDate.Value.ToString("dd/MM/yyyy") : "(không có ngày)")}");

                // Gộp thành 1 workbook
                var outputPath = Path.Combine(Path.GetTempPath(), $"Merged_Excel_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
                int totalDataRows = MergeBlocks(sortedBlocks, outputPath, debugLogs);

                ViewBag.Success = $"Đã gộp thành công {sortedBlocks.Count} file thành 1 file Excel ({totalDataRows} dòng dữ liệu)!";
                ViewBag.Blocks = sortedBlocks;
                ViewBag.TotalBlocks = sortedBlocks.Count;
                ViewBag.OutputPath = outputPath;
                ViewBag.OutputFileName = Path.GetFileName(outputPath);
                ViewBag.DebugLogs = debugLogs;

                return View("Upload");
            }
            catch (Exception ex)
            {
                ViewBag.Error = $"Lỗi xử lý: {ex.Message}\n{ex.StackTrace}";
                ViewBag.DebugLogs = debugLogs;
                return View("Upload");
            }
            finally
            {
                // Dọn thư mục giải nén tạm (giữ lại output file để tải về)
                try
                {
                    if (Directory.Exists(tempFolder))
                        Directory.Delete(tempFolder, true);
                }
                catch { /* bỏ qua lỗi dọn rác tạm */ }
            }
        }

        [HttpGet]
        public IActionResult DownloadFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return NotFound();

            var memory = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open))
                stream.CopyTo(memory);
            memory.Position = 0;

            return File(memory, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Path.GetFileName(filePath));
        }

        // ── Đọc 1 file .xlsx thành 1 MergeBlock (header row + data rows + ngày mốc để sort) ──
        private MergeBlock ReadBlock(string path, List<string> debugLogs)
        {
            try
            {
                using var wb = new XLWorkbook(path);
                var ws = wb.Worksheet(1);

                int maxRow = ws.LastRowUsed()?.RowNumber() ?? 0;
                int maxCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

                if (maxRow < 2)
                {
                    debugLogs.Add($"  ⚠ Bỏ qua {Path.GetFileName(path)}: không có dòng dữ liệu (chỉ có header hoặc rỗng)");
                    return null;
                }

                // Ngày mốc để sort = ngày đầu tiên (từ trên xuống) tìm thấy ở cột D trong các dòng data (row 2..maxRow)
                DateTime? sortDate = null;
                for (int r = 2; r <= maxRow; r++)
                {
                    var cell = ws.Cell(r, DATE_COLUMN);
                    if (cell.DataType == XLDataType.DateTime)
                    {
                        sortDate = cell.GetDateTime();
                        break;
                    }
                    if (DateTime.TryParse(cell.GetValue<string>(), out var parsed))
                    {
                        sortDate = parsed;
                        break;
                    }
                }

                debugLogs.Add($"  Đọc {Path.GetFileName(path)}: {maxRow - 1} dòng dữ liệu, ngày mốc = " +
                               $"{(sortDate.HasValue ? sortDate.Value.ToString("dd/MM/yyyy") : "(không tìm thấy)")}");

                return new MergeBlock
                {
                    FileName = Path.GetFileName(path),
                    SourcePath = path,
                    MaxRow = maxRow,
                    MaxCol = maxCol,
                    SortDate = sortDate,
                    DataRowCount = maxRow - 1
                };
            }
            catch (Exception ex)
            {
                debugLogs.Add($"  ✗ Lỗi đọc {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        // ── Gộp danh sách block (đã sort) vào 1 file output, chèn dòng "X" phân cách giữa các block ──
        private int MergeBlocks(List<MergeBlock> blocks, string outputPath, List<string> debugLogs)
        {
            using var wbNew = new XLWorkbook();
            var wsNew = wbNew.AddWorksheet("Merged");

            int currentRow = 1;
            int maxColOverall = blocks.Max(b => b.MaxCol);
            int totalDataRows = 0;
            bool columnWidthsSet = false;

            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                using var wbSrc = new XLWorkbook(block.SourcePath);
                var wsSrc = wbSrc.Worksheet(1);

                // Set độ rộng cột theo file đầu tiên (chỉ set 1 lần)
                if (!columnWidthsSet)
                {
                    for (int c = 1; c <= maxColOverall; c++)
                    {
                        if (c <= block.MaxCol)
                            wsNew.Column(c).Width = wsSrc.Column(c).Width;
                    }
                    columnWidthsSet = true;
                }

                int blockStartRow = currentRow;

                // Copy toàn bộ dòng (header row 1 + data rows 2..MaxRow) của block này sang wsNew
                var srcRowRange = Enumerable.Range(1, block.MaxRow).ToList();
                var srcToNewRow = new Dictionary<int, int>();

                foreach (int srcRowIdx in srcRowRange)
                {
                    var srcRow = wsSrc.Row(srcRowIdx);
                    var dstRow = wsNew.Row(currentRow);
                    dstRow.Height = srcRow.Height;
                    srcToNewRow[srcRowIdx] = currentRow;

                    for (int c = 1; c <= block.MaxCol; c++)
                    {
                        var srcCell = wsSrc.Cell(srcRowIdx, c);
                        var dstCell = wsNew.Cell(currentRow, c);

                        if (srcCell.HasFormula)
                            dstCell.Value = srcCell.CachedValue;
                        else
                            dstCell.Value = srcCell.Value;

                        dstCell.Style = srcCell.Style;
                    }
                    currentRow++;
                }

                // Copy merged cells nằm trong phạm vi block này
                foreach (var merge in wsSrc.MergedRanges.ToList())
                {
                    int mr1 = merge.FirstRow().RowNumber();
                    int mr2 = merge.LastRow().RowNumber();
                    if (mr1 >= 1 && mr2 <= block.MaxRow &&
                        srcToNewRow.ContainsKey(mr1) && srcToNewRow.ContainsKey(mr2))
                    {
                        wsNew.Range(srcToNewRow[mr1], merge.FirstColumn().ColumnNumber(),
                                    srcToNewRow[mr2], merge.LastColumn().ColumnNumber()).Merge();
                    }
                }

                totalDataRows += block.DataRowCount;
                debugLogs.Add($"  ✓ Đã ghi block {block.FileName} vào dòng {blockStartRow}..{currentRow - 1}");

                // Dòng phân cách giữa các block (không chèn sau block cuối cùng): đúng 1 dòng, chữ "X" ở cột A.
                // Chỉ tô màu cyan cho cả dòng khi ngày mốc của block kế tiếp KHÁC ngày mốc block hiện tại
                // (tức là "qua ngày mới"); nếu cùng ngày thì không tô màu.
                if (i < blocks.Count - 1)
                {
                    var nextBlock = blocks[i + 1];
                    bool isNewDate = block.SortDate?.Date != nextBlock.SortDate?.Date;

                    if (isNewDate)
                    {
                        for (int c = 1; c <= maxColOverall; c++)
                            wsNew.Cell(currentRow, c).Style.Fill.BackgroundColor = XLColor.Cyan;
                    }
                    wsNew.Cell(currentRow, 1).Value = "X";
                    currentRow++;
                }
            }

            // Freeze header row của block đầu tiên
            wsNew.SheetView.Freeze(1, 0);

            wbNew.SaveAs(outputPath);
            return totalDataRows;
        }

        public class MergeBlock
        {
            public string FileName { get; set; }
            public string SourcePath { get; set; }
            public int MaxRow { get; set; }
            public int MaxCol { get; set; }
            public DateTime? SortDate { get; set; }
            public int DataRowCount { get; set; }
        }
    }
}