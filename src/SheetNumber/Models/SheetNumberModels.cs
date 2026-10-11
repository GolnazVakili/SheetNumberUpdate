namespace SheetNumber.Models;

public sealed class SheetNumberCatalog
{
    public required IReadOnlyList<string> ParameterNames { get; init; }

    public required int SheetCount { get; init; }

    public string? PreviewSheetLabel { get; init; }

    public required IReadOnlyDictionary<string, string> PreviewValues { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> ValuesByParameter { get; init; }
}

public sealed class SheetNumberRequest
{
    public required string SourceParameter { get; init; }

    public required string DestinationParameter { get; init; }

    public required string PrefixTargetParameter { get; init; }

    public required IReadOnlyList<string> PrefixParameters { get; init; }

    public bool SkipIfAlreadyPrefixed { get; init; }

    public string? ValueMapParameter { get; init; }

    public IReadOnlyDictionary<string, string>? ValueMap { get; init; }
}

public sealed class SheetNumberApplyResult
{
    public required int SheetTotal { get; init; }

    public required int Updated { get; init; }

    public required int Mapped { get; init; }

    public required int SkippedPrefixed { get; init; }

    public required int SkippedEmpty { get; init; }

    public required IReadOnlyList<string> Failures { get; init; }

    public required bool ChangesCommitted { get; init; }
}
