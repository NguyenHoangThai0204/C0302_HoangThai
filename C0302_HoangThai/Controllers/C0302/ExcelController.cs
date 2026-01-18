using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using ClosedXML.Excel;
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
        public IActionResult Upload(IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                ViewBag.Error = "Vui lòng chọn file Excel";
                return View();
            }

            if (!excelFile.FileName.EndsWith(".xlsx") && !excelFile.FileName.EndsWith(".xls"))
            {
                ViewBag.Error = "Chỉ chấp nhận file Excel (.xlsx, .xls)";
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
                    var rows = worksheet.RowsUsed().Skip(1).ToList();
                    var headerRow = worksheet.Row(1);

                    // Nhóm các row theo giá trị cột đầu tiên
                    var groupedRows = rows.GroupBy(row => row.Cell(1).GetString());

                    var fileInfos = new List<ExcelFileInfo>();

                    foreach (var group in groupedRows)
                    {
                        string groupValue = group.Key;
                        string safeFileName = GetSafeFileName(groupValue);
                        string outputPath = Path.Combine(tempFolder, $"{safeFileName}.xlsx");

                        using (var newWorkbook = new XLWorkbook())
                        {
                            var newWorksheet = newWorkbook.AddWorksheet("Data");

                            // Copy header
                            for (int col = 1; col <= headerRow.CellsUsed().Count(); col++)
                            {
                                newWorksheet.Cell(1, col).Value = headerRow.Cell(col).Value;
                            }

                            // Copy data rows
                            int rowIndex = 2;
                            foreach (var row in group)
                            {
                                for (int col = 1; col <= row.CellsUsed().Count(); col++)
                                {
                                    newWorksheet.Cell(rowIndex, col).Value = row.Cell(col).Value;
                                }
                                rowIndex++;
                            }

                            // Format
                            newWorksheet.Columns().AdjustToContents();
                            newWorkbook.SaveAs(outputPath);
                        }

                        fileInfos.Add(new ExcelFileInfo
                        {
                            FileName = $"{safeFileName}.xlsx",
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