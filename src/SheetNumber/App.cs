using System.Reflection;
using Autodesk.Revit.UI;

namespace SheetNumber;

public class App : IExternalApplication
{
    public static string AssemblyPath { get; private set; } = string.Empty;

    public Result OnStartup(UIControlledApplication application)
    {
        AssemblyPath = Assembly.GetExecutingAssembly().Location;
        CreateRibbon(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static void CreateRibbon(UIControlledApplication app)
    {
        const string tabName = "Automation";
        try { app.CreateRibbonTab(tabName); }
        catch { /* tab already exists */ }

        RibbonPanel panel = GetOrCreatePanel(app, tabName, "Sheets");

        var buttonData = new PushButtonData(
            "SheetParameterUpdate.Update",
            "Sheet Parameter\nUpdate",
            AssemblyPath,
            "SheetNumber.Commands.UpdateSheetNumberCommand")
        {
            ToolTip = "Refactored the Sheet Number and integrate condensed parameter.",
            AvailabilityClassName = "SheetNumber.Commands.ProjectDocumentAvailability"
        };

        panel.AddItem(buttonData);
    }

    private static RibbonPanel GetOrCreatePanel(UIControlledApplication app, string tabName, string panelName)
    {
        foreach (RibbonPanel existing in app.GetRibbonPanels(tabName))
        {
            if (existing.Name.Equals(panelName, StringComparison.OrdinalIgnoreCase))
                return existing;
        }

        return app.CreateRibbonPanel(tabName, panelName);
    }
}
