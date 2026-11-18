using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;

namespace C0302_HoangThai.Service.S0302;

public sealed record PdfJobOutput(string Path, string Name);
public sealed record PdfJobStatus(string State, int Percent, string Message, int? Completed = null, int? Total = null);
public sealed class PdfJobInputException(string message) : Exception(message);

public sealed class PdfJob
{
    private PdfJobStatus status = new("queued", 5, "Đang chờ xử lý.");

    internal PdfJob(string directory) => Directory = directory;

    public Guid Id { get; } = Guid.NewGuid();
    public string Directory { get; }
    public PdfJobStatus Status => Volatile.Read(ref status);
    public CancellationToken StoppingToken { get; internal set; }
    internal TaskCompletionSource<PdfJobOutput?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MaxValue;

    public void Report(int percent, string message)
    {
        StoppingToken.ThrowIfCancellationRequested();
        SetStatus(new("processing", percent, message));
    }

    public void ReportUnits(int completed, int total, string message, int startPercent = 10, int endPercent = 85)
    {
        StoppingToken.ThrowIfCancellationRequested();
        int percent = startPercent + (int)((long)(endPercent - startPercent) * completed / Math.Max(total, 1));
        SetStatus(new("processing", percent, message, completed, total));
    }

    internal void SetStatus(PdfJobStatus value) => Volatile.Write(ref status, value);

    public PdfJobOutput CreateZip(string source, string name)
    {
        Report(95, "Đang đóng gói ZIP.");
        var path = Path.Combine(Directory, "result.zip");
        ZipFile.CreateFromDirectory(source, path, CompressionLevel.Fastest, includeBaseDirectory: false);
        return new(path, name);
    }

    public void ExtractZip(string archivePath, string destination)
    {
        Report(10, "Đang giải nén ZIP.");
        using var archive = ZipFile.OpenRead(archivePath);
        const long maxUncompressedBytes = 1_000_000_000;
        if (archive.Entries.Count > 10_000 || archive.Entries.Any(entry => entry.Length > maxUncompressedBytes) ||
            archive.Entries.Sum(entry => entry.Length) > maxUncompressedBytes)
            throw new PdfJobInputException("ZIP vượt quá 10.000 mục hoặc 1 GB sau khi giải nén.");

        archive.ExtractToDirectory(destination);
    }
}

public sealed class PdfJobs(ILogger<PdfJobs> logger) : BackgroundService
{
    private sealed record WorkItem(PdfJob Job, string[] Inputs, Func<PdfJob, string[], PdfJobOutput> Process);

    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(30);
    private readonly string root = Path.Combine(Path.GetTempPath(), "C0302-PdfJobs", Guid.NewGuid().ToString("N"));
    private readonly SemaphoreSlim slots = new(4, 4);
    private readonly ConcurrentDictionary<Guid, PdfJob> jobs = new();
    private readonly Channel<WorkItem> queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    public async Task<PdfJob?> SubmitAsync(
        IReadOnlyList<IFormFile> files, Func<PdfJob, string[], PdfJobOutput> process, CancellationToken cancellationToken)
    {
        if (!slots.Wait(0))
            return null;

        var job = new PdfJob(Path.Combine(root, Guid.NewGuid().ToString("N")));
        var inputs = new string[files.Count];
        bool queued = false;
        try
        {
            Directory.CreateDirectory(job.Directory);
            for (int i = 0; i < files.Count; i++)
            {
                inputs[i] = Path.Combine(job.Directory, $"input-{i}");
                await using var stream = new FileStream(inputs[i], FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 65536, useAsync: true);
                await files[i].CopyToAsync(stream, cancellationToken);
            }

            jobs[job.Id] = job;
            queued = queue.Writer.TryWrite(new(job, inputs, process));
            return queued ? job : null;
        }
        finally
        {
            if (!queued)
            {
                jobs.TryRemove(job.Id, out _);
                RemoveDirectory(job.Directory);
                slots.Release();
            }
        }
    }

    public PdfJob? Find(Guid id) => jobs.GetValueOrDefault(id);

    internal static FileStream OpenResult(PdfJobOutput output) => new(
        output.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
        bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var cleanup = CleanupExpiredAsync(stoppingToken);
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                Process(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            queue.Writer.TryComplete();
            await cleanup;
            RemoveDirectory(root);
        }
    }

    private void Process(WorkItem item, CancellationToken stoppingToken)
    {
        var job = item.Job;
        job.StoppingToken = stoppingToken;
        PdfJobOutput? output = null;
        try
        {
            job.Report(8, "Đang chuẩn bị tài liệu.");
            output = item.Process(job, item.Inputs);
            job.SetStatus(new("completed", 100, "Đã xử lý xong. ZIP sẵn sàng tải xuống."));
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                PdfJobInputException => ex.Message,
                PdfOcrUnavailableException => "Máy chủ chưa khởi tạo được OCR. Vui lòng liên hệ quản trị viên.",
                OperationCanceledException when stoppingToken.IsCancellationRequested => "Máy chủ đang khởi động lại. Vui lòng tải lại tệp.",
                _ => "Không đọc được tệp. Kiểm tra định dạng, mật khẩu và nội dung tệp rồi thử lại."
            };
            if (ex is not PdfJobInputException && !stoppingToken.IsCancellationRequested)
                logger.LogWarning("PDF job {JobId} failed ({ExceptionType}).", job.Id, ex.GetType().Name);
            if (ex is PdfOcrUnavailableException)
                logger.LogError(ex, "Native OCR initialization failed.");

            job.SetStatus(new("failed", job.Status.Percent, message));
        }
        finally
        {
            if (output is null)
                RemoveDirectory(job.Directory);
            else
                RemoveInputs(job.Directory, output.Path);

            job.ExpiresAt = DateTimeOffset.UtcNow.Add(ResultLifetime);
            job.Completion.TrySetResult(output);
            slots.Release();
        }
    }

    private async Task CleanupExpiredAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var (id, job) in jobs)
                    if (job.Completion.Task.IsCompleted && job.ExpiresAt <= DateTimeOffset.UtcNow && jobs.TryRemove(id, out _))
                        RemoveDirectory(job.Directory);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void RemoveInputs(string directory, string outputPath)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
                if (path != outputPath)
                    File.Delete(path);
            foreach (var path in Directory.EnumerateDirectories(directory))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not clean PDF job files ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private void RemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not clean PDF job folder ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
