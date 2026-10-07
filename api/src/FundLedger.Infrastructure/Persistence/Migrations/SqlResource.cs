using System.Reflection;

namespace FundLedger.Infrastructure.Persistence.Migrations;

/// <summary>Reads SQL scripts embedded from <c>Persistence/Migrations/Sql/</c>.</summary>
internal static class SqlResource
{
    public static string Read(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"FundLedger.Migrations.Sql.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration SQL '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
