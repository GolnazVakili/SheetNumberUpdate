using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SheetNumber.Models;
using SheetNumber.Services;
using SheetNumber.UI;

namespace SheetNumber.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class UpdateSheetNumberCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
            Document? doc = uiDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show("Sheet Number", "Open a project first.");
                return Result.Cancelled;
            }

            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show("Sheet Number", "Open a project document. This command does not run in the family editor.");
                return Result.Cancelled;
            }

            SheetNumberCatalog catalog = SheetParameterService.Load(doc, uiDocument!.ActiveView as ViewSheet);
            if (catalog.ParameterNames.Count == 0)
            {
                TaskDialog.Show("Sheet Number", "This project has no sheet parameters to choose from.");
                return Result.Cancelled;
            }

            var window = new SheetNumberWindow(catalog);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            if (window.ShowDialog() != true || window.Request == null)
                return Result.Cancelled;

            SheetNumberApplyResult result = SheetParameterService.Apply(doc, window.Request);
            TaskDialog.Show("Sheet Number", FormatSummary(window.Request, result));
            return result.ChangesCommitted ? Result.Succeeded : Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("Sheet Number", ex.Message);
            return Result.Failed;
        }
    }

    private static string FormatSummary(SheetNumberRequest request, SheetNumberApplyResult result)
    {
        var lines = new List<string>();
        if (result.Updated == 0 && result.Mapped == 0)
        {
            lines.Add("No sheets were changed.");
        }
        else
        {
            lines.Add($"Updated {result.Updated} of {result.SheetTotal} sheets in this project.");
            string prefixes = string.Join(" and ", request.PrefixParameters);
            if (string.Equals(request.PrefixTargetParameter, request.DestinationParameter, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{request.DestinationParameter} now stores {prefixes} in front of {request.SourceParameter}.");
            }
            else
            {
                lines.Add($"{request.DestinationParameter} now stores {request.SourceParameter}.");
                lines.Add($"{request.PrefixTargetParameter} now starts with {prefixes}.");
            }
        }

        if (result.Mapped > 0 && !string.IsNullOrWhiteSpace(request.ValueMapParameter))
        {
            lines.Add(result.Mapped == 1
                ? $"Mapped {request.ValueMapParameter} on 1 sheet."
                : $"Mapped {request.ValueMapParameter} on {result.Mapped} sheets.");
        }

        if (result.SkippedPrefixed > 0)
        {
            lines.Add(result.SkippedPrefixed == 1
                ? "Skipped 1 sheet whose destination already had this value."
                : $"Skipped {result.SkippedPrefixed} sheets whose destination already had this value.");
        }

        if (result.SkippedEmpty > 0)
        {
            lines.Add(result.SkippedEmpty == 1
                ? $"Skipped 1 sheet with an empty {request.SourceParameter}."
                : $"Skipped {result.SkippedEmpty} sheets with an empty {request.SourceParameter}.");
        }

        if (result.Failures.Count > 0)
        {
            lines.Add("");
            lines.Add("Could not update:");
            const int limit = 12;
            lines.AddRange(result.Failures.Take(limit).Select(failure => "• " + failure));
            if (result.Failures.Count > limit)
                lines.Add($"• and {result.Failures.Count - limit} more.");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public class ProjectDocumentAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        Document? doc = applicationData.ActiveUIDocument?.Document;
        return doc is { IsFamilyDocument: false };
    }
}
