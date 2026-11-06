using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using ClosedXML.Excel;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.IO.Compression;

namespace C0302_HoangThai.Controllers.C0302
{
    public class ExcelController : Controller
    {
        // GET: Excel/Upload
        [HttpGet]
        public IActionResult Upload()
        {
            return View("~/Views/Excel/Upload.cshtml");
        }

        // POST: Excel/Upload
        [HttpPost]
        public IActionResult Upload(IFormFile excelFile, int headerRow = 1, int startRow = 2, string splitColumn = "A")
        {
            // Giữ lại giá trị đã nhập để hiển thị lại trên form
            ViewBag.HeaderRow = headerRow;
            ViewBag.StartRow = startRow;
            ViewBag.SplitColumn = splitColumn;

            if (excelFile == null || excelFile.Length == 0)
            {
                ViewBag.Error = "Vui lòng chọn file Excel";
                return View();
            }

            if (!excelFile.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                ViewBag.Error = "Chỉ chấp nhận file Excel (.xlsx)";
                return View();
            }

            if (headerRow < 1 || startRow < 1)
            {
                ViewBag.Error = "Số hàng phải lớn hơn hoặc bằng 1";
                return View();
            }

            if (startRow <= headerRow)
            {
                ViewBag.Error = "Hàng bắt đầu tách phải lớn hơn hàng tiêu đề";
                return View();
            }

            // Cột tách: nhập số (1, 2...) hoặc chữ (A, B...)
            int splitCol;
            splitColumn = (splitColumn ?? "").Trim();
            if (!int.TryParse(splitColumn, out splitCol))
            {
                if (string.IsNullOrEmpty(splitColumn) || !splitColumn.All(char.IsLetter))
                {
                    ViewBag.Error = "Cột tách không hợp lệ (nhập số hoặc chữ cái, ví dụ: 1 hoặc A)";
                    return View();
                }
                splitCol = XLHelper.GetColumnNumberFromLetter(splitColumn.ToUpper());
            }

            if (splitCol < 1 || splitCol > XLHelper.MaxColumnNumber)
            {
                ViewBag.Error = "Cột tách không hợp lệ";
                return View();
            }

            try
            {
                // Tạo thư mục tạm để lưu các file đã tách
                var tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempFolder);

                using (var stream = excelFile.OpenReadStream())
                using (var workbook = new XLWorkbook(stream))
                {
                    var worksheet = workbook.Worksheet(1);
                    var lastRowUsed = worksheet.LastRowUsed();
                    var lastColUsed = worksheet.LastColumnUsed();

                    if (lastRowUsed == null || lastColUsed == null)
                    {
                        ViewBag.Error = "File Excel không có dữ liệu";
                        return View();
                    }

                    int lastCol = lastColUsed.ColumnNumber();

                    if (headerRow > lastRowUsed.RowNumber())
                    {
                        ViewBag.Error = $"Hàng tiêu đề ({headerRow}) vượt quá số hàng của file ({lastRowUsed.RowNumber()})";
                        return View();
                    }

                    if (splitCol > lastCol)
                    {
                        ViewBag.Error = $"Cột tách vượt quá số cột của file (file có {lastCol} cột)";
                        return View();
                    }

                    // Chỉ lấy các hàng có dữ liệu, từ hàng bắt đầu trở đi
                    var rows = worksheet.RowsUsed()
                                        .Where(r => r.RowNumber() >= startRow)
                                        .ToList();

                    if (!rows.Any())
                    {
                        ViewBag.Error = $"Không có dữ liệu từ hàng {startRow} trở đi";
                        return View();
                    }

                    // Nhóm các row theo giá trị của cột được chọn
                    var groupedRows = rows.GroupBy(row => row.Cell(splitCol).GetString().Trim());

                    var fileInfos = new List<ExcelFileInfo>();
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var group in groupedRows)
                    {
                        string safeFileName = GetSafeFileName(group.Key);

                        // Tránh trùng tên file (do ký tự bị thay thế hoặc bị cắt 50 ký tự)
                        string uniqueName = safeFileName;
                        int n = 2;
                        while (!usedNames.Add(uniqueName))
                        {
                            uniqueName = $"{safeFileName}_{n++}";
                        }

                        string outputPath = Path.Combine(tempFolder, $"{uniqueName}.xlsx");

                        using (var newWorkbook = new XLWorkbook())
                        {
                            var newWorksheet = newWorkbook.AddWorksheet(worksheet.Name);

                            // Copy tiêu đề + dữ liệu, giữ nguyên định dạng
                            CopyGroupToSheet(worksheet, newWorksheet, lastCol, headerRow,
                                             group.Select(r => r.RowNumber()).ToList());

                            newWorkbook.SaveAs(outputPath);
                        }

                        fileInfos.Add(new ExcelFileInfo
                        {
                            FileName = $"{uniqueName}.xlsx",
                            FilePath = outputPath,
                            RowCount = group.Count()
                        });
                    }

                    // Nén tất cả file vào 1 file ZIP
                    var zipPath = Path.Combine(Path.GetTempPath(), $"ExcelFiles_{DateTime.Now:yyyyMMddHHmmss}.zip");
                    ZipFile.CreateFromDirectory(tempFolder, zipPath);

                    ViewBag.Files = fileInfos;
                    ViewBag.ZipPath = zipPath;
                    ViewBag.TotalFiles = fileInfos.Count;
                    ViewBag.Success = $"Đã tách thành công {fileInfos.Count} file!";

                    return View();
                }
            }
            catch (Exception ex)
            {
                ViewBag.Error = $"Lỗi xử lý file: {ex.Message}";
                return View();
            }
        }

        // GET: Excel/DownloadZip
        [HttpGet]
        public IActionResult DownloadZip(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var memory = new MemoryStream();
            using (var stream = new FileStream(filePath, FileMode.Open))
            {
                stream.CopyTo(memory);
            }
            memory.Position = 0;

            return File(memory, "application/zip", Path.GetFileName(filePath));
        }

        /// <summary>
        /// Copy 1 hàng tiêu đề + các hàng dữ liệu của nhóm sang sheet mới,
        /// giữ nguyên style (viền, màu, font, định dạng số), độ rộng cột,
        /// chiều cao hàng và các ô gộp.
        /// </summary>
        private void CopyGroupToSheet(IXLWorksheet src, IXLWorksheet dst, int lastCol,
                                      int headerRowNumber, List<int> dataRowNumbers)
        {
            // Copy độ rộng cột (không dùng AdjustToContents để giữ đúng bản gốc)
            for (int c = 1; c <= lastCol; c++)
            {
                var srcCol = src.Column(c);
                var dstCol = dst.Column(c);
                dstCol.Width = srcCol.Width;
                if (srcCol.IsHidden) dstCol.Hide();
            }

            // Map: số hàng nguồn -> số hàng mới (hàng tiêu đề = 1)
            var allRows = new List<int> { headerRowNumber };
            allRows.AddRange(dataRowNumbers);

            var rowMap = new Dictionary<int, int>();
            for (int i = 0; i < allRows.Count; i++)
            {
                rowMap[allRows[i]] = i + 1;
            }

            // Copy từng hàng
            foreach (var kv in rowMap)
            {
                int srcRowIdx = kv.Key;
                int dstRowIdx = kv.Value;

                var srcRow = src.Row(srcRowIdx);
                var dstRow = dst.Row(dstRowIdx);
                dstRow.Height = srcRow.Height;

                for (int c = 1; c <= lastCol; c++)
                {
                    var srcCell = src.Cell(srcRowIdx, c);
                    var dstCell = dst.Cell(dstRowIdx, c);

                    // Công thức: lấy giá trị đã tính, tránh lỗi tham chiếu ô sau khi tách
                    if (srcCell.HasFormula)
                        dstCell.Value = srcCell.CachedValue;
                    else
                        dstCell.Value = srcCell.Value;

                    // Copy style: viền, màu nền, font, căn lề, định dạng số...
                    dstCell.Style = srcCell.Style;
                }
            }

            // Copy các ô gộp nằm trong các hàng được copy
            foreach (var merge in src.MergedRanges.ToList())
            {
                int r1 = merge.FirstRow().RowNumber();
                int r2 = merge.LastRow().RowNumber();
                int c1 = merge.FirstColumn().ColumnNumber();
                int c2 = merge.LastColumn().ColumnNumber();

                if (c2 > lastCol) continue;
                if (!rowMap.ContainsKey(r1) || !rowMap.ContainsKey(r2)) continue;

                int newR1 = rowMap[r1];
                int newR2 = rowMap[r2];

                // Chỉ gộp khi các hàng vẫn liền nhau như bản gốc
                if (newR2 - newR1 != r2 - r1) continue;

                dst.Range(newR1, c1, newR2, c2).Merge();
            }

            // Cố định hàng tiêu đề
            dst.SheetView.Freeze(1, 0);
        }

        private string GetSafeFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string safe = new string(fileName.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "empty" : safe.Substring(0, Math.Min(safe.Length, 50));
        }
    }

    public class ExcelFileInfo
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public int RowCount { get; set; }
    }
}