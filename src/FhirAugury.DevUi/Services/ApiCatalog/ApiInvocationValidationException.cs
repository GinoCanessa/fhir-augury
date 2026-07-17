using System;
using System.Collections.Generic;

namespace FhirAugury.DevUi.Services.ApiCatalog;

public sealed class ApiInvocationValidationException : Exception
{
    public IReadOnlyList<string> MissingParameters { get; }

    public ApiInvocationValidationException(IReadOnlyList<string> missing)
        : base($"Missing required parameters: {string.Join(", ", missing)}")
    {
        MissingParameters = missing;
    }
}
