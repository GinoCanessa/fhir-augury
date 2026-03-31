using System;
using System.Collections.Generic;
using System.Text;

namespace FhirAugury.Common.Indexing;

public readonly record struct IndexContent
{
    public required string SourceType { get; init; }
    public required string SourceId { get; init; }
    public required string Text { get; init; }
}
