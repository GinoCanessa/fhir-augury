namespace FhirAugury.Processing.Jira.Common.Configuration;

public static class JiraProcessingOptionsValidator
{
    public static void ValidateAndThrow(this JiraProcessingOptions options)
    {
        List<string> errors = options.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }
}
