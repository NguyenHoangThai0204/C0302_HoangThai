namespace C0302_HoangThai.Models;

public enum OcrPageSelection { Automatic, StartPages, StartPagesOrFixedSize, BudgetDocuments }

public sealed record OcrWorkspaceModel(
    string Title, string Description, string SplitAction, string RenameAction, string PdfInput, string RenameInstructions,
    OcrPageSelection PageSelection = OcrPageSelection.Automatic, string ZipInput = "pdfZip", string ExcelInput = "mappingExcel",
    bool ShowUprightPdfOption = false);
