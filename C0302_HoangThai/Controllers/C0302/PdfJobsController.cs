using C0302_HoangThai.Service.S0302;
using Microsoft.AspNetCore.Mvc;

namespace C0302_HoangThai.Controllers.C0302;

public abstract class PdfJobController(PdfJobs jobs) : Controller
{
    protected async Task<IActionResult> RunJobAsync(IReadOnlyList<IFormFile> files, Func<PdfJob, string[], PdfJobOutput> process)
    {
        Response.Headers.CacheControl = "no-store";
        var job = await jobs.SubmitAsync(files, process, HttpContext.RequestAborted);
        if (job is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Máy chủ đang xử lý nhiều tệp. Vui lòng thử lại sau.");

        if (Request.Headers["X-Pdf-Job"] == "1")
            return Accepted(new
            {
                statusUrl = Url.Action(nameof(PdfJobsController.Status), "PdfJobs", new { id = job.Id }),
                downloadUrl = Url.Action(nameof(PdfJobsController.Download), "PdfJobs", new { id = job.Id })
            });

        var output = await job.Completion.Task.WaitAsync(HttpContext.RequestAborted);
        return output is null
            ? BadRequest(job.Status.Message)
            : File(PdfJobs.OpenResult(output), "application/zip", output.Name);
    }
}

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PdfJobsController(PdfJobs jobs) : Controller
{
    [HttpGet("/pdf-jobs/{id:guid}")]
    public IActionResult Status(Guid id)
    {
        var job = jobs.Find(id);
        if (job is null)
            return NotFound();

        var status = job.Status;
        if (status.State == "completed" && !job.Completion.Task.IsCompleted)
            return Ok(new PdfJobStatus("processing", 99, "Đang chuẩn bị tải xuống."));

        return Ok(status);
    }

    [HttpGet("/pdf-jobs/{id:guid}/download")]
    public IActionResult Download(Guid id)
    {
        var job = jobs.Find(id);
        if (job is null || !job.Completion.Task.IsCompletedSuccessfully || job.Completion.Task.Result is not { } output)
            return NotFound();

        try
        {
            return File(PdfJobs.OpenResult(output), "application/zip", output.Name);
        }
        catch (IOException)
        {
            return NotFound();
        }
    }
}
