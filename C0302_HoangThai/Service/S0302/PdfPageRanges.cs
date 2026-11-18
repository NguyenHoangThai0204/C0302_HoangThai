using iText.Kernel.Pdf;
using iText.Kernel.Utils;

namespace C0302_HoangThai.Service.S0302;

internal static class PdfPageRanges
{
    public static List<(int Start, int End)> FromStarts(string input, int pageCount)
    {
        if (pageCount == 0)
            throw new PdfJobInputException("File PDF không có trang nào.");

        var starts = new SortedSet<int>();
        foreach (string token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, out int page) || page < 1)
                throw new PdfJobInputException("Dãy số trang không hợp lệ. Ví dụ 1, 4, 7.");
            if (page > pageCount)
                throw new PdfJobInputException($"Trang {page} vượt quá tổng số trang ({pageCount}).");
            starts.Add(page);
        }

        if (starts.Count == 0 || starts.Min != 1)
            throw new PdfJobInputException("Dãy số trang phải bắt đầu bằng 1.");

        var ranges = new List<(int, int)>(starts.Count);
        int previous = 1;
        foreach (int start in starts.Skip(1))
        {
            ranges.Add((previous, start - 1));
            previous = start;
        }
        ranges.Add((previous, pageCount));
        return ranges;
    }

    public static List<(int Start, int End)> BySize(int pageCount, int size)
    {
        if (pageCount == 0)
            throw new PdfJobInputException("File PDF không có trang nào.");

        var ranges = new List<(int, int)>();
        for (int start = 1; start <= pageCount; start += size)
            ranges.Add((start, Math.Min(start + size - 1, pageCount)));
        return ranges;
    }

    public static string WriteChunk(PdfDocument source, int start, int end, string output, string name, int firstSuffix)
    {
        string fileName = AvailableName(output, name, ".pdf", firstSuffix);
        using var target = new PdfDocument(new PdfWriter(Path.Combine(output, fileName)));
        new PdfMerger(target).Merge(source, start, end);
        return fileName;
    }

    public static string AvailableName(string directory, string name, string extension, int firstSuffix)
    {
        string fileName = name + extension;
        for (int suffix = firstSuffix; File.Exists(Path.Combine(directory, fileName)); suffix++)
            fileName = $"{name}_{suffix}{extension}";
        return fileName;
    }
}
