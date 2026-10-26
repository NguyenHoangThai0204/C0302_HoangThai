using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using iText.Kernel.Pdf;
using iText.Kernel.Utils;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace C0302_HoangThai.Controllers.C0302
{
    public class MergePDFController : Controller
    {
        [HttpGet]
        public IActionResult Upload() => View();

        // ════════════════════════════════════════════════════════════
        //  POST: Gộp nhiều PDF (upload trực tiếp) + PDF trong ZIP → 1 PDF
        //  Thứ tự merge:
        //    1. Các file PDF upload (giữ thứ tự user chọn)
        //    2. Các file PDF trong ZIP (natural sort theo tên)
        //  File lỗi / password-protected → bỏ qua, ghi vào header
        // ════════════════════════════════════════════════════════════
        [HttpPost]
        [RequestSizeLimit(500_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
        public IActionResult MergePdfs(List<IFormFile> pdfFiles, IFormFile zipFile)
        {
            var uploadedPdfs = new List<(string Name, byte[] Data)>();
            var zippedPdfs = new List<(string Name, byte[] Data)>();

            // ── 1. PDF upload trực tiếp ─────────────────────────────
            if (pdfFiles != null)
            {
                foreach (var f in pdfFiles)
                {
                    if (f == null || f.Length == 0) continue;
                    if (!f.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

                    using var ms = new MemoryStream();
                    f.CopyTo(ms);
                    uploadedPdfs.Add((Path.GetFileName(f.FileName), ms.ToArray()));
                }
            }

            // ── 2. PDF từ ZIP ───────────────────────────────────────
            if (zipFile != null && zipFile.Length > 0)
            {
                if (!zipFile.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return BadRequest("File ZIP không hợp lệ.");

                var tempZip = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");
                var extractFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

                try
                {
                    using (var fs = new FileStream(tempZip, FileMode.Create)) zipFile.CopyTo(fs);
                    Directory.CreateDirectory(extractFolder);
                    ZipFile.ExtractToDirectory(tempZip, extractFolder, overwriteFiles: true);

                    var files = Directory.GetFiles(extractFolder, "*.pdf", SearchOption.AllDirectories)
                        .OrderBy(p => PadNumbers(Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var path in files)
                        zippedPdfs.Add((Path.GetFileName(path), System.IO.File.ReadAllBytes(path)));
                }
                finally
                {
                    try { if (System.IO.File.Exists(tempZip)) System.IO.File.Delete(tempZip); } catch { }
                    try { Directory.Delete(extractFolder, true); } catch { }
                }
            }

            // ── 3. Kết hợp: upload trước, ZIP sau ───────────────────
            var allPdfs = uploadedPdfs.Concat(zippedPdfs).ToList();

            if (allPdfs.Count == 0)
                return BadRequest("Không tìm thấy file PDF nào để gộp.");

            // ── 4. Merge bằng iText7 ────────────────────────────────
            var tempOut = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            var skipped = new List<string>();
            int mergedCount = 0;

            try
            {
                using (var writer = new PdfWriter(tempOut))
                using (var destDoc = new PdfDocument(writer))
                {
                    var merger = new PdfMerger(destDoc);

                    foreach (var (name, data) in allPdfs)
                    {
                        try
                        {
                            using var ms = new MemoryStream(data);
                            using var reader = new PdfReader(ms);
                            using var srcDoc = new PdfDocument(reader);

                            int n = srcDoc.GetNumberOfPages();
                            if (n > 0)
                            {
                                merger.Merge(srcDoc, 1, n);
                                mergedCount++;
                            }
                            else
                            {
                                skipped.Add(name);
                            }
                        }
                        catch
                        {
                            skipped.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { if (System.IO.File.Exists(tempOut)) System.IO.File.Delete(tempOut); } catch { }
                return BadRequest($"Lỗi gộp PDF: {ex.Message}");
            }

            if (mergedCount == 0)
            {
                try { if (System.IO.File.Exists(tempOut)) System.IO.File.Delete(tempOut); } catch { }
                return BadRequest("Không có file PDF nào gộp được. " +
                                  $"Các file lỗi: {string.Join(", ", skipped)}");
            }

            // Thông báo file bị bỏ qua (nếu có)
            if (skipped.Count > 0)
                Response.Headers["X-Skipped-Files"] = string.Join(" | ", skipped);

            // Dọn temp file sau khi response hoàn tất
            HttpContext.Response.OnCompleted(() =>
            {
                try { System.IO.File.Delete(tempOut); } catch { }
                return Task.CompletedTask;
            });

            return PhysicalFile(tempOut, "application/pdf",
                $"Merged_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        }

        // ── Natural sort: file2.pdf < file10.pdf ────────────────────
        private static string PadNumbers(string input) =>
            Regex.Replace(input, @"\d+", m => m.Value.PadLeft(20, '0'));
    }
}