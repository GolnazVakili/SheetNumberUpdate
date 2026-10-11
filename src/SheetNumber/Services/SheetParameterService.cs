using System.Globalization;
using Autodesk.Revit.DB;
using SheetNumber.Models;

namespace SheetNumber.Services;

public static class SheetParameterService
{
    public static SheetNumberCatalog Load(Document doc, ViewSheet? activeSheet)
    {
        List<ViewSheet> sheets = CollectSheets(doc);
        ViewSheet? previewSheet = ResolvePreviewSheet(activeSheet, sheets);
        IReadOnlySet<string> names = CollectSheetParameterNames(doc, previewSheet ?? sheets.FirstOrDefault());
        Dictionary<string, string> previewValues = previewSheet == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadAll(previewSheet);

        return new SheetNumberCatalog
        {
            ParameterNames = names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            SheetCount = sheets.Count,
            PreviewSheetLabel = previewSheet == null ? null : SheetLabel(previewSheet),
            PreviewValues = previewValues,
            ValuesByParameter = CollectValuesByParameter(sheets, names)
        };
    }

    public static SheetNumberApplyResult Apply(Document doc, SheetNumberRequest request)
    {
        List<ViewSheet> sheets = CollectSheets(doc);
        int updated = 0;
        int skippedPrefixed = 0;
        int skippedEmpty = 0;
        var failures = new List<string>();
        var plans = new List<SheetPlan>();
        foreach (ViewSheet sheet in sheets)
            plans.Add(PlanSheet(doc, sheet, request));

        var alreadySet = plans
            .Where(plan => plan.Status == UpdateStatus.SkippedPrefixed)
            .Select(plan => plan.Sheet.Id.Value)
            .ToHashSet();
        bool hasUpdates = plans.Any(plan => plan.Status == UpdateStatus.Updated);
        bool hasMap = sheets.Any(sheet => !alreadySet.Contains(sheet.Id.Value) && NeedsMappedWrite(doc, sheet, request));
        if (!hasUpdates && !hasMap)
        {
            return new SheetNumberApplyResult
            {
                SheetTotal = sheets.Count,
                Updated = 0,
                Mapped = 0,
                SkippedPrefixed = plans.Count(plan => plan.Status == UpdateStatus.SkippedPrefixed),
                SkippedEmpty = plans.Count(plan => plan.Status == UpdateStatus.SkippedEmpty),
                Failures = plans
                    .Where(plan => plan.Status == UpdateStatus.Failed && !string.IsNullOrEmpty(plan.Failure))
                    .Select(plan => $"{plan.Label}: {plan.Failure}")
                    .ToList(),
                ChangesCommitted = false
            };
        }

        using var transaction = new Transaction(doc, "Sheet Parameter Update");
        transaction.Start();
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new QuietWarnings());
        transaction.SetFailureHandlingOptions(options);

        if (doc.IsWorkshared)
        {
            try
            {
                WorksharingUtils.CheckoutElements(doc, sheets.Select(sheet => sheet.Id).ToList());
            }
            catch
            {
                // Sheets that cannot be checked out are reported individually.
            }
        }

        foreach (SheetPlan plan in plans.Where(plan => plan.SetSheetNumber && plan.Status == UpdateStatus.Updated))
        {
            try
            {
                plan.Sheet.SheetNumber = "TMP" + plan.Sheet.Id.Value.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                plan.Status = UpdateStatus.Failed;
                plan.Failure = ex.Message;
            }
        }

        if (plans.Any(plan => plan.SetSheetNumber && plan.Status == UpdateStatus.Updated))
            doc.Regenerate();

        foreach (SheetPlan plan in plans)
        {
            switch (plan.Status)
            {
                case UpdateStatus.SkippedPrefixed:
                    skippedPrefixed++;
                    continue;
                case UpdateStatus.SkippedEmpty:
                    skippedEmpty++;
                    continue;
                case UpdateStatus.Failed:
                    failures.Add($"{plan.Label}: {plan.Failure}");
                    continue;
            }

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            if (TryApplyPlan(plan, out string? failure))
            {
                subTransaction.Commit();
                updated++;
            }
            else
            {
                subTransaction.RollBack();
                if (plan.SetSheetNumber)
                {
                    try { plan.Sheet.SheetNumber = plan.OriginalSheetNumber; }
                    catch { /* The temporary number is rolled back with the transaction if this sheet was not committed. */ }
                }

                failures.Add($"{plan.Label}: {failure}");
            }
        }

        int mapped = ApplyValueMap(doc, sheets, request, failures, alreadySet);

        if (updated > 0 || mapped > 0)
            doc.Regenerate();

        bool committed = false;
        if (updated > 0 || mapped > 0)
        {
            transaction.Commit();
            committed = true;
        }
        else
        {
            transaction.RollBack();
        }

        return new SheetNumberApplyResult
        {
            SheetTotal = sheets.Count,
            Updated = updated,
            Mapped = mapped,
            SkippedPrefixed = skippedPrefixed,
            SkippedEmpty = skippedEmpty,
            Failures = failures,
            ChangesCommitted = committed
        };
    }

    private static bool NeedsMappedWrite(Document doc, ViewSheet sheet, SheetNumberRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ValueMapParameter) || request.ValueMap is not { Count: > 0 } valueMap)
            return false;

        List<ParamHit> hits = FindHits(doc, sheet, request.ValueMapParameter);
        string current = Prefer(hits, writable: false)?.Value ?? "";
        if (current.Length == 0 || !valueMap.TryGetValue(current, out string? replacement))
            return false;

        if (string.Equals(current, replacement, StringComparison.Ordinal))
            return false;

        return WritableParameters(hits).Count > 0;
    }

    private static int ApplyValueMap(Document doc, List<ViewSheet> sheets, SheetNumberRequest request, List<string> failures, HashSet<long> alreadySet)
    {
        if (string.IsNullOrWhiteSpace(request.ValueMapParameter) || request.ValueMap is not { Count: > 0 } valueMap)
            return 0;

        int mapped = 0;
        foreach (ViewSheet sheet in sheets)
        {
            if (alreadySet.Contains(sheet.Id.Value))
                continue;

            List<ParamHit> hits = FindHits(doc, sheet, request.ValueMapParameter);
            string current = Prefer(hits, writable: false)?.Value ?? "";
            if (current.Length == 0 || !valueMap.TryGetValue(current, out string? replacement))
                continue;

            if (string.Equals(current, replacement, StringComparison.Ordinal))
                continue;

            List<Parameter> parameters = WritableParameters(hits);
            if (parameters.Count == 0)
            {
                failures.Add($"{SheetLabel(sheet)}: {request.ValueMapParameter} is read-only.");
                continue;
            }

            bool wrote = false;
            foreach (Parameter parameter in parameters)
            {
                if (IsBuiltInSheetNumber(parameter))
                {
                    try
                    {
                        sheet.SheetNumber = replacement;
                        wrote = true;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{SheetLabel(sheet)}: {ex.Message}");
                    }

                    continue;
                }

                if (TrySet(parameter, replacement, out string? error))
                    wrote = true;
                else
                    failures.Add($"{SheetLabel(sheet)}: Could not write {request.ValueMapParameter}. {error}");
            }

            if (wrote)
                mapped++;
        }

        return mapped;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectValuesByParameter(List<ViewSheet> sheets, IReadOnlySet<string> names)
    {
        var values = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (ViewSheet sheet in sheets)
        {
            foreach (Element host in Hosts(sheet))
            {
                foreach (Parameter parameter in host.Parameters)
                {
                    if (!IsListed(parameter))
                        continue;

                    string name = parameter.Definition.Name;
                    if (!names.Contains(name))
                        continue;

                    string value = ReadParameter(parameter);
                    if (value.Length == 0)
                        continue;

                    if (!values.TryGetValue(name, out SortedSet<string>? set))
                    {
                        set = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                        values[name] = set;
                    }

                    set.Add(value);
                }
            }
        }

        return values.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static SheetPlan PlanSheet(Document doc, ViewSheet sheet, SheetNumberRequest request)
    {
        var plan = new SheetPlan
        {
            Sheet = sheet,
            Label = SheetLabel(sheet),
            OriginalSheetNumber = sheet.SheetNumber ?? ""
        };

        List<ParamHit> sourceHits = FindHits(doc, sheet, request.SourceParameter);
        List<ParamHit> destinationHits = FindHits(doc, sheet, request.DestinationParameter);
        List<ParamHit> targetHits = FindHits(doc, sheet, request.PrefixTargetParameter);
        bool targetIsDestination = NamesEqual(request.PrefixTargetParameter, request.DestinationParameter);

        ParamHit? source = Prefer(sourceHits, writable: false);
        if (source == null)
        {
            plan.Status = UpdateStatus.Failed;
            plan.Failure = $"Could not find {request.SourceParameter} on this sheet.";
            return plan;
        }

        List<Parameter> destinationParameters = WritableParameters(destinationHits);
        if (destinationParameters.Count == 0)
        {
            plan.Status = UpdateStatus.Failed;
            plan.Failure = DescribeMissing(request.DestinationParameter, destinationHits);
            return plan;
        }

        List<Parameter> targetParameters = targetIsDestination
            ? destinationParameters
            : WritableParameters(targetHits);
        if (targetParameters.Count == 0)
        {
            plan.Status = UpdateStatus.Failed;
            plan.Failure = DescribeMissing(request.PrefixTargetParameter, targetHits);
            return plan;
        }

        var prefixParts = new List<string>(request.PrefixParameters.Count);
        foreach (string parameterName in request.PrefixParameters)
        {
            List<ParamHit> hits = FindHits(doc, sheet, parameterName);
            if (hits.Count == 0)
            {
                plan.Status = UpdateStatus.Failed;
                plan.Failure = $"Could not find {parameterName} on this sheet.";
                return plan;
            }

            string raw = Prefer(hits, writable: false)?.Value ?? "";
            prefixParts.Add(ValueMapFile.MappedValue(request.ValueMap, parameterName, request.ValueMapParameter, raw));
        }

        string prefix = string.Concat(prefixParts);
        string savedNumber = Prefer(destinationHits, writable: false)?.Value ?? "";
        if (request.SkipIfAlreadyPrefixed && AlreadyUpdated(source.Value, savedNumber, prefix))
        {
            plan.Status = UpdateStatus.SkippedPrefixed;
            return plan;
        }

        string sourceValue = source.Value;
        ParamHit? prefixTarget = targetIsDestination
            ? Prefer(destinationHits, writable: false)
            : Prefer(targetHits, writable: false);
        string baseValue = targetIsDestination ? sourceValue : prefixTarget?.Value ?? "";
        if (string.IsNullOrEmpty(baseValue))
        {
            plan.Status = UpdateStatus.SkippedEmpty;
            return plan;
        }

        string updated = prefix + baseValue;
        string existingTarget = targetIsDestination ? destinationHits.First().Value : prefixTarget?.Value ?? "";
        if (request.SkipIfAlreadyPrefixed && string.Equals(existingTarget, updated, StringComparison.Ordinal))
        {
            plan.Status = UpdateStatus.SkippedPrefixed;
            return plan;
        }

        plan.Status = UpdateStatus.Updated;
        plan.TargetParameters = targetParameters;
        plan.TargetValue = updated;
        plan.SetSheetNumber = targetParameters.Any(IsBuiltInSheetNumber) || destinationParameters.Any(IsBuiltInSheetNumber) && targetIsDestination;
        if (targetParameters.Any(IsBuiltInSheetNumber))
        {
            plan.SetSheetNumber = true;
            plan.NewSheetNumber = updated;
        }

        if (!targetIsDestination)
        {
            plan.WriteDestination = true;
            plan.DestinationParameters = destinationParameters.Where(parameter => !IsBuiltInSheetNumber(parameter)).ToList();
            plan.DestinationValue = sourceValue;
            if (destinationParameters.Any(IsBuiltInSheetNumber))
            {
                plan.SetSheetNumber = true;
                plan.NewSheetNumber = sourceValue;
            }
        }

        return plan;
    }

    private static bool TryApplyPlan(SheetPlan plan, out string? failure)
    {
        failure = null;
        if (plan.WriteDestination)
        {
            foreach (Parameter parameter in plan.DestinationParameters)
            {
                if (!TrySet(parameter, plan.DestinationValue, out string? copyError))
                {
                    failure = $"Could not write the destination parameter. {copyError}";
                    return false;
                }
            }
        }

        if (plan.SetSheetNumber && !string.IsNullOrEmpty(plan.NewSheetNumber))
        {
            try
            {
                plan.Sheet.SheetNumber = plan.NewSheetNumber;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }

        foreach (Parameter parameter in plan.TargetParameters.Where(parameter => !IsBuiltInSheetNumber(parameter)))
        {
            if (!TrySet(parameter, plan.TargetValue, out string? writeError))
            {
                failure = $"Could not write the prefixed parameter. {writeError}";
                return false;
            }
        }

        return true;
    }

    private static List<Parameter> WritableParameters(List<ParamHit> hits)
    {
        return hits
            .Where(hit => !hit.Parameter.IsReadOnly && !IsTypeParameter(hit.Parameter))
            .Select(hit => hit.Parameter)
            .GroupBy(parameter => (ElementId: parameter.Element?.Id.Value ?? 0, ParameterId: parameter.Id.Value))
            .Select(group => group.First())
            .ToList();
    }

    private static bool TrySet(Parameter parameter, string value, out string? error)
    {
        error = null;
        try
        {
            if (parameter.IsReadOnly)
            {
                error = "The parameter is read-only.";
                return false;
            }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    if (parameter.Set(value))
                        return true;
                    error = "Revit did not accept the new value.";
                    return false;

                case StorageType.Integer:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                    {
                        error = $"\"{value}\" is not a whole number.";
                        return false;
                    }

                    if (parameter.Set(number))
                        return true;
                    error = "Revit did not accept the new value.";
                    return false;

                default:
                    error = "This parameter type cannot store the value.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<ViewSheet> CollectSheets(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(sheet => !sheet.IsTemplate)
            .OrderBy(sheet => sheet.SheetNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static ViewSheet? ResolvePreviewSheet(ViewSheet? activeSheet, List<ViewSheet> sheets)
    {
        if (activeSheet != null && !activeSheet.IsTemplate && !activeSheet.IsPlaceholder)
            return activeSheet;

        return sheets.FirstOrDefault();
    }

    private static Dictionary<string, string> ReadAll(ViewSheet sheet)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Element host in Hosts(sheet))
        {
            foreach (Parameter parameter in host.Parameters)
                Accumulate(values, parameter);
        }

        return values;
    }

    private static void Accumulate(Dictionary<string, string> values, Parameter parameter)
    {
        if (!IsListed(parameter))
            return;

        string name = parameter.Definition.Name;
        string value = ReadParameter(parameter);
        if (IsBuiltInSheetNumber(parameter))
        {
            values[name] = value;
            return;
        }

        if (!values.TryGetValue(name, out string? existing)
            || (string.IsNullOrEmpty(existing) && !string.IsNullOrEmpty(value)))
        {
            values[name] = value;
        }
    }

    private static List<ParamHit> FindHits(Document doc, ViewSheet sheet, string parameterName)
    {
        var hits = new List<ParamHit>();
        foreach (Element host in Hosts(sheet))
        {
            Parameter? parameter = host.LookupParameter(parameterName);
            if (parameter == null || !IsListed(parameter))
                continue;

            hits.Add(new ParamHit(parameter, ReadParameter(parameter)));
        }

        return hits;
    }

    private static string DescribeMissing(string parameterName, List<ParamHit> hits)
    {
        if (hits.Count == 0)
            return $"Could not find {parameterName} on this sheet.";

        if (hits.All(hit => IsTypeParameter(hit.Parameter)))
            return $"{parameterName} is a type parameter. Each sheet needs its own instance parameter.";

        return $"{parameterName} is read-only on this sheet.";
    }

    private static ParamHit? Prefer(List<ParamHit> hits, bool writable)
    {
        List<ParamHit> pool = writable
            ? hits.Where(hit => !hit.Parameter.IsReadOnly && !IsTypeParameter(hit.Parameter)).ToList()
            : hits;

        if (pool.Count == 0)
            return null;

        ParamHit? sheetNumber = pool.FirstOrDefault(hit => IsBuiltInSheetNumber(hit.Parameter));
        if (sheetNumber != null)
            return sheetNumber;

        ParamHit? withValue = pool.FirstOrDefault(hit => !string.IsNullOrEmpty(hit.Value));
        return withValue ?? pool[0];
    }

    private static IEnumerable<Element> Hosts(ViewSheet sheet)
    {
        yield return sheet;

        foreach (Element titleBlock in new FilteredElementCollector(sheet.Document, sheet.Id)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            yield return titleBlock;
        }
    }

    private static IReadOnlySet<string> CollectSheetParameterNames(Document doc, ViewSheet? sheet)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sheet == null)
            return names;

        var otherViewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        View? otherView = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => view is not ViewSheet && !view.IsTemplate);
        if (otherView != null)
            CollectNames(otherView, otherViewNames);

        foreach (Parameter parameter in sheet.GetOrderedParameters())
        {
            if (!IsListed(parameter))
                continue;

            string name = parameter.Definition.Name;
            bool sheetBuiltIn = IsBuiltInSheetNumber(parameter);
            if (sheetBuiltIn || !otherViewNames.Contains(name))
                names.Add(name);
        }

        Category? sheets = Category.GetCategory(doc, BuiltInCategory.OST_Sheets);
        if (sheets == null)
            return names;

        DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not Definition definition || string.IsNullOrWhiteSpace(definition.Name))
                continue;
            if (iterator.Current is not ElementBinding binding || !IsBoundOnlyToSheets(binding, sheets))
                continue;

            names.Add(definition.Name);
        }

        return names;
    }

    private static bool IsBoundOnlyToSheets(ElementBinding binding, Category sheets)
    {
        bool includesSheets = false;
        foreach (Category category in binding.Categories)
        {
            if (category.Id == sheets.Id)
                includesSheets = true;
            else
                return false;
        }

        return includesSheets;
    }

    private static void CollectNames(Element element, ISet<string> names)
    {
        foreach (Parameter parameter in element.GetOrderedParameters())
        {
            if (IsListed(parameter))
                names.Add(parameter.Definition.Name);
        }
    }

    private static bool IsListed(Parameter parameter)
    {
        try
        {
            if (parameter.Definition?.Name is not { Length: > 0 })
                return false;

            if (parameter.StorageType is not (StorageType.String or StorageType.Integer))
                return false;

            if (parameter.Definition is InternalDefinition definition
                && definition.GetDataType() == SpecTypeId.Boolean.YesNo)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBuiltInSheetNumber(Parameter parameter)
    {
        return parameter.Definition is InternalDefinition definition
               && definition.BuiltInParameter == BuiltInParameter.SHEET_NUMBER;
    }

    private static bool IsTypeParameter(Parameter parameter)
    {
        try
        {
            return parameter.Element is ElementType;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadParameter(Parameter parameter)
    {
        try
        {
            string raw = parameter.StorageType switch
            {
                StorageType.String => parameter.AsString() ?? "",
                StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
                _ => ""
            };

            return raw.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static bool AlreadyUpdated(string sheetNumber, string sheetNumber2, string prefix)
    {
        return prefix.Length > 0
               && sheetNumber2.Length > 0
               && string.Equals(sheetNumber, prefix + sheetNumber2, StringComparison.Ordinal);
    }

    private static bool NamesEqual(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string SheetLabel(ViewSheet sheet)
    {
        string number = sheet.SheetNumber?.Trim() ?? "";
        string name = sheet.Name?.Trim() ?? "";
        if (number.Length == 0)
            return name.Length == 0 ? "Sheet" : name;
        if (name.Length == 0)
            return number;
        return $"{number} — {name}";
    }

    private sealed class SheetPlan
    {
        public required ViewSheet Sheet { get; init; }
        public required string Label { get; init; }
        public required string OriginalSheetNumber { get; init; }
        public UpdateStatus Status { get; set; }
        public string? Failure { get; set; }
        public bool WriteDestination { get; set; }
        public string DestinationValue { get; set; } = "";
        public List<Parameter> DestinationParameters { get; set; } = [];
        public bool SetSheetNumber { get; set; }
        public string NewSheetNumber { get; set; } = "";
        public List<Parameter> TargetParameters { get; set; } = [];
        public string TargetValue { get; set; } = "";
    }

    private enum UpdateStatus
    {
        Updated,
        SkippedPrefixed,
        SkippedEmpty,
        Failed
    }

    private sealed class ParamHit(Parameter parameter, string value)
    {
        public Parameter Parameter { get; } = parameter;
        public string Value { get; } = value;
    }

    private sealed class QuietWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(failure);
            }

            return FailureProcessingResult.Continue;
        }
    }
}
