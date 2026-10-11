using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SheetNumber.Models;
using SheetNumber.Services;

namespace SheetNumber.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class UpdateSheetNumberCommand : IExternalCommand
{
    private const string Title = "Sheet Parameter Update";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            UIDocument? uiDocument = commandData.Application.ActiveUIDocument;
            Document? doc = uiDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show(Title, "Open a project first.");
                return Result.Cancelled;
            }

            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(Title, "Open a project document. This command does not run in the family editor.");
                return Result.Cancelled;
            }

            string mapPath = Path.Combine(Path.GetDirectoryName(App.AssemblyPath) ?? "", "Condensed_Parameter.json");
            if (!File.Exists(mapPath))
            {
                TaskDialog.Show(Title, "Mapping file was not found:\n" + mapPath);
                return Result.Cancelled;
            }

            Dictionary<string, string> valueMap = ValueMapFile.Read(mapPath);
            SheetNumberCatalog catalog = SheetParameterService.Load(doc, uiDocument!.ActiveView as ViewSheet);
            if (!TryBuildRequest(catalog, valueMap, out SheetNumberRequest? request, out string? error))
            {
                TaskDialog.Show(Title, error);
                return Result.Cancelled;
            }

            SheetNumberApplyResult result = SheetParameterService.Apply(doc, request!);
            TaskDialog.Show(Title, FormatSummary(request!, result));
            return result.ChangesCommitted ? Result.Succeeded : Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show(Title, ex.Message);
            return Result.Failed;
        }
    }

    private static bool TryBuildRequest(
        SheetNumberCatalog catalog,
        IReadOnlyDictionary<string, string> valueMap,
        out SheetNumberRequest? request,
        out string error)
    {
        request = null;
        IReadOnlyList<string> names = catalog.ParameterNames;
        string? source = Find(names, name => name.Equals("Sheet Number", StringComparison.OrdinalIgnoreCase));
        string? destination = Find(names, name => name.Equals("SheetNumber2", StringComparison.OrdinalIgnoreCase));
        string? discipline = Find(names, name => IsDisciplineOriginator(name) && !IsSubDiscipline(name));
        string? subDiscipline = Find(names, IsSubDiscipline);

        var missing = new List<string>();
        if (source == null)
            missing.Add("Sheet Number");
        if (destination == null)
            missing.Add("SheetNumber2");
        if (discipline == null)
            missing.Add("Discipline Originator");
        if (subDiscipline == null)
            missing.Add("Sub-Discipline");

        if (missing.Count > 0)
        {
            error = "This project is missing:\n" + string.Join(Environment.NewLine, missing.Select(name => "• " + name));
            return false;
        }

        if (catalog.SheetCount == 0)
        {
            error = "This project has no sheets to update.";
            return false;
        }

        request = new SheetNumberRequest
        {
            SourceParameter = source!,
            DestinationParameter = destination!,
            PrefixTargetParameter = source!,
            PrefixParameters = [discipline!, subDiscipline!],
            SkipIfAlreadyPrefixed = true,
            ValueMapParameter = discipline,
            ValueMap = valueMap
        };
        error = "";
        return true;
    }

    private static string? Find(IReadOnlyList<string> names, Func<string, bool> predicate)
    {
        return names.FirstOrDefault(predicate);
    }

    private static bool IsSubDiscipline(string name)
    {
        return name.Contains("Sub-Discipline", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Sub Discipline", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Subdiscipline", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisciplineOriginator(string name)
    {
        return name.Contains("Discipline", StringComparison.OrdinalIgnoreCase)
               && (name.Contains("Originator", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Orginator", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatSummary(SheetNumberRequest request, SheetNumberApplyResult result)
    {
        var lines = new List<string>();
        if (result.Updated == 0 && result.Mapped == 0)
        {
            lines.Add(result.SkippedPrefixed > 0
                ? "Already updated. Nothing was changed."
                : "No sheets were changed.");
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
                ? "Skipped 1 sheet that already has the condensed prefix, sub-discipline, and SheetNumber2."
                : $"Skipped {result.SkippedPrefixed} sheets that already have the condensed prefix, sub-discipline, and SheetNumber2.");
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
